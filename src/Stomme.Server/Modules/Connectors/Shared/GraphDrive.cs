#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Microsoft.AspNetCore.StaticFiles;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>
    /// One Microsoft Graph drive's files (BL-229 - OneDrive's and a SharePoint site's document library alike): its items by id or
    /// by path, their pages, and their bytes to and from the pipeline run's folder (<see cref="IConnectorFiles"/>). Graph answers a
    /// download with a redirect to a short-lived pre-authenticated address on Microsoft's own download host; that one redirect is
    /// followed only when it is HTTPS on a SharePoint host (or on Graph's own address), WITHOUT the token, through the same egress -
    /// any other address it names is refused. An upload is one PUT of the whole content, sent again from the start after a
    /// throttle.
    /// </summary>
    public sealed class GraphDrive
    {
        private static readonly Regex ItemId = new("^[A-Za-z0-9!_.-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly string[] DownloadHosts = { ".sharepoint.com", ".sharepoint.us", ".sharepoint.de", ".sharepoint.cn" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly HttpClient client;
        private readonly string token;
        private readonly string service;

        public GraphDrive(HttpClient client, string token, string root, string service)
        {
            this.client = client;
            this.token = token;
            Root = root;
            this.service = service;
        }

        /// <summary>The drive's address - <c>https://graph.microsoft.com/v1.0/users/{user}/drive</c>, <c>.../sites/{site}/drive</c>.</summary>
        public string Root { get; }

        /// <summary>
        /// An item's address: <c>root</c>, a path from the drive's root (<c>/Kvitton/2026/1042.pdf</c>, each segment encoded, no
        /// <c>.</c> or <c>..</c> segment, no control character) or an id.
        /// </summary>
        public string Item(string reference, string what) => Address(Root, reference, what);

        /// <summary>An item's address under the drive at <paramref name="root"/> - checked without a drive in hand, before a token is asked for.</summary>
        public static string Address(string root, string reference, string what)
        {
            var given = reference.Trim();
            if (string.Equals(given, "root", StringComparison.OrdinalIgnoreCase) || given == "/")
            {
                return root + "/root";
            }

            if (given.StartsWith('/'))
            {
                var segments = given.Trim('/').Split('/');
                if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
                {
                    throw new InvalidOperationException($"'{reference}' is not a path from the drive's root - /Kvitton/2026/1042.pdf ({what}).");
                }

                return root + "/root:/" + string.Join("/", segments.Select(Uri.EscapeDataString)) + ":";
            }

            return ItemId.IsMatch(given) ? root + "/items/" + Uri.EscapeDataString(given) : throw new InvalidOperationException($"'{reference}' is not an item - its id, or its path from the drive's root, /Kvitton/1042.pdf ({what}).");
        }

        /// <summary>A request Graph answers by accepting it for later (a copy's 202): its status and the address that follows it - said, never fetched.</summary>
        public async Task<(int Status, string? Location)> AcceptedAsync(string url, JsonObject body, CancellationToken cancellationToken)
        {
            var content = body.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response, TryParse(await response.Content.ReadAsStringAsync(cancellationToken)));
            }

            return ((int)response.StatusCode, response.Headers.Location?.ToString());
        }

        /// <summary>A path for a new item under <paramref name="parent"/> (an <see cref="Item"/> address): <c>{parent}:/{name}:</c>.</summary>
        public static string Child(string parent, string name)
        {
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or ".." || name.Any(char.IsControl))
            {
                throw new InvalidOperationException($"'{name}' is not a file's name - one name, no slash.");
            }

            // An item's address already ends with ':' when it is a path: its child is then 'root:/a/b/name:'.
            return parent.EndsWith(':') ? parent[..^1] + "/" + Uri.EscapeDataString(name) + ":" : parent + ":/" + Uri.EscapeDataString(name) + ":";
        }

        public async Task<JsonObject> JsonAsync(HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            return await ReadAsync(response, cancellationToken);
        }

        /// <summary>A collection page by page, its next link kept to the collection (<see cref="GraphPage"/>), up to <paramref name="max"/>.</summary>
        public async IAsyncEnumerable<ConnectorRecord> PagesAsync(string url, int max, Func<ConnectorRecord, bool>? keep = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var read = 0;
            string? next = url;
            while (next is not null && read < max)
            {
                var current = next;
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return request;
                }, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw Failure(response, Parse(payload, response));
                }

                Parse(payload, response);
                var (records, link) = GraphPage.Read(payload, current);
                foreach (var record in records.Where(record => keep?.Invoke(record) ?? true))
                {
                    if (read == max)
                    {
                        yield break;
                    }

                    read++;
                    yield return Clean(record);
                }

                next = link;
            }
        }

        /// <summary>
        /// An item's bytes written into the run's folder at <paramref name="path"/> - ending with <c>/</c>, under its own name - the
        /// item's metadata read first (a folder refused, a file over the folder's cap refused by its size); the bytes written.
        /// </summary>
        public async Task<(string Path, long Written, JsonObject Item)> DownloadAsync(string item, IConnectorFiles files, string path, CancellationToken cancellationToken)
        {
            var meta = await JsonAsync(HttpMethod.Get, item, null, cancellationToken);
            var name = Text(meta["name"]) ?? "file";
            if (meta["folder"] is not null)
            {
                throw new InvalidOperationException($"'{name}' is a folder; a folder is not downloaded.");
            }

            if (meta["size"] is JsonValue size && size.TryGetValue<long>(out var bytes) && bytes > files.MaxBytes)
            {
                throw new InvalidOperationException($"'{name}' is {bytes} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
            }

            var target = path.EndsWith('/') ? path + Named(name) : path;
            var content = item + "/content";
            using var first = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, content);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            HttpResponseMessage? redirected = null;
            try
            {
                var answer = first;
                if ((int)first.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    // Graph's pre-authenticated download address: followed once, on Microsoft's own host, without the token.
                    var location = first.Headers.Location is { } named ? named.IsAbsoluteUri ? named : new Uri(new Uri(content), named) : throw new InvalidOperationException($"{service} answered a download with a redirect that names no address.");
                    if (!Downloadable(location, new Uri(Root)))
                    {
                        throw new InvalidOperationException($"{service} named a download address that is not Microsoft's ({location.Host}) - it is not followed.");
                    }

                    redirected = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, location), cancellationToken, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)redirected.StatusCode is >= 300 and < 400)
                    {
                        throw new InvalidOperationException($"{service}'s download address redirected again - it is not followed.");
                    }

                    answer = redirected;
                }

                if (!answer.IsSuccessStatusCode)
                {
                    var payload = await answer.Content.ReadAsStringAsync(cancellationToken);
                    throw Failure(answer, TryParse(payload));
                }

                if (answer.Content.Headers.ContentLength is { } length && length > files.MaxBytes)
                {
                    throw new InvalidOperationException($"'{name}' is {length} bytes, over the pipeline folder's {files.MaxBytes}; it is not fetched.");
                }

                await using var stream = await answer.Content.ReadAsStreamAsync(cancellationToken);
                return (target, await files.WriteAsync(target, stream, cancellationToken), meta);
            }
            finally
            {
                redirected?.Dispose();
            }
        }

        /// <summary>
        /// Content PUT at <paramref name="url"/> (a new item's <c>{parent}:/{name}:/content</c> or an item's <c>/content</c>): a file
        /// of the run's folder - the same bytes from the start for each attempt - or text, typed by <paramref name="contentType"/> or
        /// the name's extension.
        /// </summary>
        public async Task<JsonObject> UploadAsync(string url, string name, IConnectorFiles? files, string? file, byte[]? text, string? contentType, CancellationToken cancellationToken)
        {
            var type = contentType ?? (text is not null ? "text/plain; charset=utf-8" : new FileExtensionContentTypeProvider().TryGetContentType(name, out var byName) ? byName : "application/octet-stream");
            HttpRequestMessage Request(HttpContent body, long length)
            {
                body.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                body.Headers.ContentLength = length;
                var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = body };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }

            if (text is not null)
            {
                using var sent = await RetryAfterPolicy.Default.SendAsync(client, () => Request(new ByteArrayContent(text), text.LongLength), cancellationToken);
                return await ReadAsync(sent, cancellationToken);
            }

            await using var opened = await files!.OpenReadAsync(file!, cancellationToken);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                opened.Content.Position = 0;
                return Request(new StreamContent(new LentStream(opened.Content)), opened.Length);
            }, cancellationToken);
            return await ReadAsync(response, cancellationToken);
        }

        /// <summary>An item downloaded into the run's folder (<see cref="DownloadAsync"/>), as a record: its id, name, path written, size and type.</summary>
        public async Task<ConnectorRecord> DownloadRecordAsync(string item, IConnectorFiles files, string target, CancellationToken cancellationToken)
        {
            var (path, written, meta) = await DownloadAsync(item, files, target, cancellationToken);
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(meta["id"]), ["name"] = Text(meta["name"]), ["file"] = path, ["size"] = written, ["mimeType"] = Text(meta["file"]?["mimeType"]),
            });
        }

        /// <summary>A new item - a file of the run's folder or text - named <paramref name="name"/> in <paramref name="folder"/> (an item's address), a name in use replaced, renamed or refused.</summary>
        public async Task<ConnectorRecord> UploadRecordAsync(string folder, string name, IConnectorFiles? files, string? file, string? text, string? contentType, string conflict, CancellationToken cancellationToken) =>
            Record(await UploadAsync(Child(folder, name) + "/content?@microsoft.graph.conflictBehavior=" + conflict, name, files, file, text is null ? null : Encoding.UTF8.GetBytes(text), contentType, cancellationToken));

        /// <summary>An item's content (a file of the run's folder or text) and then its name, each when given.</summary>
        public async Task<ConnectorRecord> UpdateRecordAsync(string item, string? name, IConnectorFiles? files, string? file, string? text, string? contentType, CancellationToken cancellationToken)
        {
            JsonObject updated = new();
            if (file is not null || text is not null)
            {
                updated = await UploadAsync(item + "/content", name ?? file ?? "text.txt", files, file, text is null ? null : Encoding.UTF8.GetBytes(text), contentType, cancellationToken);
            }

            if (name is not null)
            {
                updated = await JsonAsync(HttpMethod.Patch, item, new JsonObject { ["name"] = name }, cancellationToken);
            }

            return Record(updated);
        }

        /// <summary>A record of an item: its fields, Graph's annotations and the pre-authenticated download address left out.</summary>
        public static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => !pair.Key.StartsWith("@", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : JsonRecords.ToClrValue(JsonSerializer.SerializeToElement(pair.Value)), StringComparer.Ordinal));

        // Graph's download address: HTTPS on a SharePoint host, or Graph's own address (a stand-in, a sovereign cloud's Graph).
        internal static bool Downloadable(Uri target, Uri graph) =>
            (target.Scheme == Uri.UriSchemeHttps && DownloadHosts.Any(host => target.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)))
            || (target.Scheme == graph.Scheme && string.Equals(target.Authority, graph.Authority, StringComparison.OrdinalIgnoreCase));

        private static ConnectorRecord Clean(ConnectorRecord record) =>
            new(record.Fields.Where(pair => !pair.Key.StartsWith("@", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        // A file's own name as a file of the folder: its unsafe characters replaced.
        private static string Named(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
            return safe.Length == 0 ? "file" : safe;
        }

        private async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = Parse(payload, response);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response, json);
            }

            return json as JsonObject ?? new JsonObject();
        }

        // JSON or nothing; a success that is not JSON a failure that says so.
        private JsonNode? Parse(string payload, HttpResponseMessage response)
        {
            try
            {
                return string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{service} answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonNode? TryParse(string payload)
        {
            try
            {
                return string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static InvalidOperationException Failure(HttpResponseMessage response, JsonNode? json)
        {
            var code = Text(json?["error"]?["code"]);
            var said = Text(json?["error"]?["message"]);
            var text = string.Join(": ", new[] { code, said }.Where(part => !string.IsNullOrEmpty(part)));
            return new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
