#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>A file a form's file field was sent, held aside until the submission is accepted.</summary>
    public sealed record PipelineFormUpload(string Key, string FileName, string HeldAt, long Size, string ContentType);

    /// <summary>
    /// A form's submission as it was read (n8n's Form trigger with a File field): its answers' text, and the files of its
    /// file fields - each written aside, outside every site's folder, while it is read, bounded by <c>Pipelines:Forms:MaxFileBytes</c>.
    /// Only an accepted submission's files are kept (<see cref="KeepAsync"/>), in the site's pipeline folder; whatever was not kept
    /// is deleted when the submission is disposed - a refused form, a robot's, a failure part way - so nothing is left behind.
    /// </summary>
    public sealed class PipelineFormSubmission : IDisposable
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, PipelineFormUpload> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>What was wrong with a file as it was read - too large - per field.</summary>
        public Dictionary<string, string> FileErrors { get; } = new(StringComparer.Ordinal);

        /// <summary>The answer under a key as the form's rules read it: a file field's file's name, else the text sent.</summary>
        public string? Posted(string key) =>
            Files.TryGetValue(key, out var file) ? file.FileName : Values.TryGetValue(key, out var value) ? value : null;

        /// <summary>
        /// Keeps the files in the site's folder - <c>forms/{folder}/{yyyy-MM-dd}/{8 hex}-{name}</c> - and makes each file field's
        /// answer what it kept: its path in the folder, its name, its size and its type.
        /// </summary>
        public void Keep(JsonObject answers, string siteRoot, string folder, DateTimeOffset now)
        {
            foreach (var file in Files.Values)
            {
                var relative = "forms/" + PathPart(folder) + "/" + now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/"
                    + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + "-" + SafeName(file.FileName);
                var full = Path.GetFullPath(Path.Combine(siteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Move(file.HeldAt, full);
                answers[file.Key] = new JsonObject
                {
                    ["path"] = relative,
                    ["name"] = file.FileName,
                    ["size"] = file.Size,
                    ["contentType"] = file.ContentType,
                };
            }

            Files.Clear(); // kept: nothing to delete
        }

        public void Dispose()
        {
            foreach (var file in Files.Values)
            {
                try
                {
                    File.Delete(file.HeldAt);
                }
                catch (IOException)
                {
                    // the folder of files being taken in is swept with every upload's; one that could not be deleted now is not lost
                }
            }

            Files.Clear();
        }

        // A form's name as a folder: letters, digits, '-' and '_' kept, anything else '_' - never a "..".
        private static string PathPart(string text)
        {
            var part = new string((text ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '_').ToArray()).Trim('_');
            return part.Length == 0 ? "form" : part[..Math.Min(part.Length, 100)];
        }

        // A file's name as it is kept: its own letters, digits, '.', '-' and '_', the rest '-', at most 100 characters with its extension.
        internal static string SafeName(string fileName)
        {
            var name = Path.GetFileName(fileName ?? "");
            var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('.', '-');
            if (safe.Length == 0)
            {
                return "file";
            }

            var extension = Path.GetExtension(safe);
            return safe.Length <= 100 ? safe : safe[..(100 - extension.Length)] + extension;
        }
    }

    /// <summary>Reads a form's submission: URL-encoded, or multipart when the form has a file field.</summary>
    public static class PipelineFormSubmissions
    {
        /// <summary>Room for a multipart submission's own parts and headers, besides its answers and files.</summary>
        public const int MultipartRoom = 64 * 1024;

        /// <summary>
        /// The submission, or the status that refuses it outright: 415 for a body neither kind, 413 for answers past
        /// <c>MaxBodyBytes</c>. A file past <c>MaxFileBytes</c> is not a refusal of the whole: it is that field's error.
        /// </summary>
        public static async Task<(PipelineFormSubmission? Submission, int? Refusal)> ReadAsync(HttpRequest request, FormTriggerOptions settings,
            IReadOnlyList<PipelineFormField> fields, string heldIn, CancellationToken ct)
        {
            var type = request.ContentType?.Split(';')[0].Trim();
            if (string.Equals(type, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadEncodedAsync(request, settings, ct);
            }

            var fileFields = fields.Where(field => field.Type == PipelineFormFieldTypes.File).Select(PipelineForms.KeyOf).ToHashSet(StringComparer.Ordinal);
            if (!string.Equals(type, "multipart/form-data", StringComparison.OrdinalIgnoreCase) || fileFields.Count == 0
                || !MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || HeaderUtilities.RemoveQuotes(media.Boundary).Value is not { Length: > 0 } boundary)
            {
                return (null, StatusCodes.Status415UnsupportedMediaType);
            }

            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = settings.MaxBodyBytes + MultipartRoom + fileFields.Count * Math.Max(0, settings.MaxFileBytes);
            }

            var submission = new PipelineFormSubmission();
            try
            {
                var reader = new MultipartReader(boundary, request.Body) { HeadersLengthLimit = 16 * 1024 };
                long text = 0;
                while (await reader.ReadNextSectionAsync(ct) is { } section)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                        || HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { Length: > 0 } key)
                    {
                        continue;
                    }

                    if (disposition.IsFileDisposition())
                    {
                        var fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "");
                        // A part for a field that is no file field, a second file for one, or no file chosen (the browser sends an empty
                        // part with no name): read past it, kept nowhere.
                        if (!fileFields.Contains(key) || fileName.Length == 0 || submission.Files.ContainsKey(key) || submission.FileErrors.ContainsKey(key))
                        {
                            await section.Body.CopyToAsync(Stream.Null, ct);
                            continue;
                        }

                        Directory.CreateDirectory(heldIn);
                        var heldAt = Path.Combine(heldIn, "form-" + Guid.NewGuid().ToString("N"));
                        var (written, whole) = await HoldAsync(section.Body, heldAt, settings.MaxFileBytes, ct);
                        if (!whole)
                        {
                            File.Delete(heldAt);
                            submission.FileErrors[key] = settings.MaxFileBytes <= 0 ? "This form takes no files here." : "At most " + Describe(settings.MaxFileBytes) + ".";
                            await section.Body.CopyToAsync(Stream.Null, ct);
                            continue;
                        }

                        submission.Files[key] = new PipelineFormUpload(key, fileName, heldAt, written, string.IsNullOrWhiteSpace(section.ContentType) ? "application/octet-stream" : section.ContentType!);
                        continue;
                    }

                    var value = await ReadTextAsync(section.Body, settings.MaxBodyBytes - text, ct);
                    if (value is null)
                    {
                        submission.Dispose();
                        return (null, StatusCodes.Status413PayloadTooLarge);
                    }

                    text += Encoding.UTF8.GetByteCount(value);
                    submission.Values.TryAdd(key, value);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
            {
                // A body cut short, a malformed part or one past the request's size: nothing of it is kept.
                submission.Dispose();
                return (null, ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
            }

            return (submission, null);
        }

        private static async Task<(PipelineFormSubmission?, int?)> ReadEncodedAsync(HttpRequest request, FormTriggerOptions settings, CancellationToken ct)
        {
            if (request.ContentLength is { } length && length > settings.MaxBodyBytes)
            {
                return (null, StatusCodes.Status413PayloadTooLarge);
            }

            var body = await ReadTextAsync(request.Body, settings.MaxBodyBytes, ct);
            if (body is null)
            {
                return (null, StatusCodes.Status413PayloadTooLarge);
            }

            var submission = new PipelineFormSubmission();
            foreach (var (key, values) in QueryHelpers.ParseQuery(body))
            {
                submission.Values[key] = values.ToString();
            }

            return (submission, null);
        }

        // At most so many bytes as UTF-8 text; null past them.
        private static async Task<string?> ReadTextAsync(Stream body, long limit, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await body.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // Writes a part aside, stopping past the limit: how much it wrote, and whether that was the whole part.
        private static async Task<(long Written, bool Whole)> HoldAsync(Stream part, string heldAt, long limit, CancellationToken ct)
        {
            long written = 0;
            var chunk = new byte[64 * 1024];
            await using var output = File.Create(heldAt);
            while (true)
            {
                var read = await part.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    return (written, true);
                }

                if (written + read > limit)
                {
                    return (written, false);
                }

                await output.WriteAsync(chunk.AsMemory(0, read), ct);
                written += read;
            }
        }

        public static string Describe(long bytes) =>
            bytes >= 1024 * 1024 ? (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
            : bytes >= 1024 ? (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
            : bytes + " bytes";
    }
}
