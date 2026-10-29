#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Infrastructure.Security;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>How a paginated request finds its next page (n8n's HTTP Request pagination).</summary>
    public enum HttpPagination
    {
        /// <summary>One request.</summary>
        Off,

        /// <summary>Follow the response's <c>Link</c> header, <c>rel="next"</c> (GitHub's and RFC 8288's way).</summary>
        NextLinkHeader,

        /// <summary>Follow the URL in a field of the body (a cursor API's <c>next</c>).</summary>
        NextUrlInBody,

        /// <summary>Add one to a query parameter until a page has no records.</summary>
        PageNumber,
    }

    public enum HttpRequestMethod
    {
        GET,
        POST,
        PUT,
        PATCH,
        DELETE,
        HEAD,
    }

    /// <summary>
    /// the predecessor platform's REST action and its ControlFlow HTTP request, as one engine step. The URL and the authorization
    /// header value are inputs of the step (a Connection with a vaulted secret is the Connectors absorption's);
    /// the response body is the result, the status code an output, and a non-2xx answer or a transport failure is a
    /// FAILURE under the step's policy (fault, or the <c>Failed</c> port with the body still in the result). Egress is
    /// the framework's ONE outbound guard (<see cref="OutboundConnectGuard"/>): the destination is checked
    /// before the send and again inside the connect step, and redirects are followed by the step so each hop is checked.
    /// BL-229 (n8n's binary data on its HTTP Request node): a file in the site's pipeline folder can be SENT - its bytes as the
    /// body, or as a multipart form's file - and a response SAVED to one as it streams in, binary or text, so a download is no
    /// longer read into memory as text (and garbled when it is not text). And pagination (n8n's): a GET that follows the next page -
    /// by the Link header, a URL in the body or a page number - and gathers every page's records into one list, each page's
    /// address checked by the same guard, at most so many pages and <c>MaxResponseBytes</c> in all.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "HTTP", "Send an HTTP request; the response body is the result.", DisplayName = "HTTP request")]
    public class SendHttpRequest : DataStep
    {
        [Input(Description = "The absolute http(s) URL.", Order = 1)]
        public Input<string> Url { get; set; } = null!;

        [Input(DefaultValue = HttpRequestMethod.GET, Order = 2)]
        public Input<HttpRequestMethod> Method { get; set; } = new(new Literal<HttpRequestMethod>(HttpRequestMethod.GET));

        [Input(UIHint = InputUIHints.MultiLine, Description = "The request body (not sent with GET or HEAD). Empty: the previous step's result when the method carries a body.", Order = 3)]
        public Input<string?>? Body { get; set; }

        public const string DefaultContentType = "application/json";

        [Input(DisplayName = "Content type", Description = "The body's media type. For a file sent, left as it is: the file's own, by its extension.", DefaultValue = DefaultContentType, Order = 4)]
        public Input<string> ContentType { get; set; } = new(new Literal<string>("application/json"));

        [Input(UIHint = InputUIHints.MultiLine, Description = "Extra request headers, one 'Name: value' per line.", Order = 5)]
        public Input<string?>? Headers { get; set; }

        [Input(Description = "The Authorization header's value, for example 'Bearer …'.", CanContainSecrets = true, Order = 6)]
        public Input<string?>? Authorization { get; set; }

        [Input(DisplayName = "Timeout (seconds)", DefaultValue = 30, Order = 7)]
        public Input<int> TimeoutSeconds { get; set; } = new(new Literal<int>(30));

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, DisplayName = "Send a file", Description = "A file in the site's pipeline folder sent as the body (not with GET or HEAD): its bytes as they are, or a multipart form's file when 'File field' is given. Empty: the Body.", Order = 8)]
        public Input<string?>? SendFile { get; set; }

        [Input(DisplayName = "File field", Description = "Send the file as multipart/form-data under this field name - an upload, as a browser sends one. Empty: the file's bytes are the whole body.", Order = 9)]
        public Input<string?>? FileField { get; set; }

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, DisplayName = "Save the response to", Description = "A file in the site's pipeline folder the response body is written to as it comes - a download, binary or text, up to Pipelines:Files:MaxFileBytes; the result is then the file's path. Empty: the body is the result.", Order = 10)]
        public Input<string?>? SaveTo { get; set; }

        [Input(Description = "Off: one request. NextLinkHeader: follow the response's Link rel=\"next\". NextUrlInBody: follow the URL in the body's 'Next' field. PageNumber: add one to the 'Next' query parameter until a page has no records. Every page's records are gathered into one list (GET only).", DefaultValue = HttpPagination.Off, Order = 11)]
        public Input<HttpPagination> Pagination { get; set; } = new(new Literal<HttpPagination>(HttpPagination.Off));

        [Input(DisplayName = "Records field", Description = "Paginated: where a page's records are in its JSON body, e.g. 'data' or 'result.items'. Empty: the body is the list.", Order = 12)]
        public Input<string?>? RecordsField { get; set; }

        [Input(DisplayName = "Next", Description = "NextUrlInBody: the body's field holding the next page's URL, e.g. 'links.next'. PageNumber: the query parameter counting the pages, e.g. 'page' - from its value in the URL, else 1.", Order = 13)]
        public Input<string?>? NextField { get; set; }

        [Input(DisplayName = "Most pages", Description = "Paginated: stop after this many pages (at most Pipelines:Http:MaxPages).", DefaultValue = 10, Order = 14)]
        public Input<int> MaxPages { get; set; } = new(new Literal<int>(10));

        [Output(DisplayName = "Status code")]
        public Output<int>? StatusCode { get; set; }

        [Output(DisplayName = "Response content type")]
        public Output<string?>? ResponseContentType { get; set; }

        [Output(DisplayName = "Saved bytes", Description = "How many bytes 'Save the response to' wrote.")]
        public Output<long>? SavedBytes { get; set; }

        [Output(DisplayName = "Pages", Description = "Paginated: how many pages were read.")]
        public Output<int>? Pages { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var options = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Http ?? new HttpStepOptions();
            var url = Required(context, Url, "Url");
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var uri))
            {
                await FailAsync(context, "INVALID_URL", $"'{url}' is not an absolute URL.").ConfigureAwait(false);
                return;
            }

            var allowedHosts = (IReadOnlyCollection<string>)options.AllowedHosts;
            if (await OutboundConnectGuard.CheckDestinationAsync(uri, allowedHosts, context.CancellationToken).ConfigureAwait(false) is { } refusal)
            {
                await FailAsync(context, "EGRESS_REFUSED", refusal).ConfigureAwait(false);
                return;
            }

            // The extra headers and the Authorization, read once: each value rendered on its own (TemplatedLines), so a value from
            // the run is that header's and never another header; and a header is one line, so one holding a line break is refused
            // before anything is sent.
            var (headers, headerRefusal) = await TemplatedLines.HeadersAsync(Headers, context).ConfigureAwait(false);
            if (headers is null)
            {
                await FailAsync(context, "HEADER_INVALID", headerRefusal!).ConfigureAwait(false);
                return;
            }

            var authorization = Authorization?.GetOrDefault(context);
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                if (TemplatedLines.BreaksLine(authorization))
                {
                    await FailAsync(context, "HEADER_INVALID", "The Authorization holds a line break; a header is one line.").ConfigureAwait(false);
                    return;
                }

                headers.Add(("Authorization", authorization));
            }

            var method = new HttpMethod(Method.GetOrDefault(context).ToString());
            var carriesBody = method != HttpMethod.Get && method != HttpMethod.Head;
            var maxFileBytes = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;

            // A file sent or a response saved: both in the site's own folder, both judged before anything is sent.
            var sendFile = carriesBody ? SendFile?.GetOrDefault(context) : null;
            string? sendPath = null;
            if (!string.IsNullOrWhiteSpace(sendFile))
            {
                sendPath = PipelineFileSandbox.Resolve(context, sendFile, out var sendRefusal);
                if (sendPath is null)
                {
                    await FailAsync(context, "PATH_REFUSED", sendRefusal!).ConfigureAwait(false);
                    return;
                }

                if (!File.Exists(sendPath))
                {
                    await FailAsync(context, "FILE_NOT_FOUND", $"'{sendFile}' is not a file in the site's pipeline folder.").ConfigureAwait(false);
                    return;
                }

                if (new FileInfo(sendPath).Length > maxFileBytes)
                {
                    await FailAsync(context, "FILE_TOO_LARGE", $"'{sendFile}' is over Pipelines:Files:MaxFileBytes ({maxFileBytes}).").ConfigureAwait(false);
                    return;
                }
            }

            var saveTo = SaveTo?.GetOrDefault(context);
            string? savePath = null;
            if (!string.IsNullOrWhiteSpace(saveTo))
            {
                savePath = PipelineFileSandbox.Resolve(context, saveTo, out var saveRefusal);
                if (savePath is null)
                {
                    await FailAsync(context, "PATH_REFUSED", saveRefusal!).ConfigureAwait(false);
                    return;
                }
            }

            var fileField = FileField?.GetOrDefault(context);
            var contentType = ContentType.GetOrDefault(context);
            var body = carriesBody && sendPath is null ? TextOrLastResult(context, Body) : null;

            var pagination = Pagination.GetOrDefault(context);
            if (pagination != HttpPagination.Off)
            {
                var next = NextField?.GetOrDefault(context);
                var refusedPaging = method != HttpMethod.Get ? "A paginated request is a GET."
                    : savePath is not null ? "A paginated request gathers records; it is not saved to a file."
                    : pagination is HttpPagination.NextUrlInBody or HttpPagination.PageNumber && string.IsNullOrWhiteSpace(next)
                        ? $"{pagination} needs 'Next': {(pagination == HttpPagination.PageNumber ? "the query parameter counting the pages" : "the field holding the next page's URL")}."
                        : null;
                if (refusedPaging is not null)
                {
                    await FailAsync(context, "PAGINATION_INVALID", refusedPaging).ConfigureAwait(false);
                    return;
                }

                await RunPagesAsync(context, options, uri, allowedHosts, headers, pagination, RecordsField?.GetOrDefault(context), next?.Trim(),
                    Math.Clamp(MaxPages.GetOrDefault(context), 1, Math.Max(1, options.MaxPages)), TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds.GetOrDefault(context), 1, Math.Max(1, options.MaxTimeoutSeconds)))).ConfigureAwait(false);
                return;
            }
            var timeout = Math.Clamp(TimeoutSeconds.GetOrDefault(context), 1, Math.Max(1, options.MaxTimeoutSeconds));

            using var handler = OutboundConnectGuard.CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(timeout) };

            try
            {
                var target = uri;
                for (var hop = 0; ; hop++)
                {
                    using var request = new HttpRequestMessage(method, target);
                    request.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowedHosts);
                    if (sendPath is not null)
                    {
                        // Opened per hop: a 307 or 308 sends the body again.
                        request.Content = FileContent(sendPath, fileField, contentType);
                    }
                    else if (body is not null)
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, contentType ?? DefaultContentType);
                    }

                    AddHeaders(request, headers);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < options.MaxRedirects)
                    {
                        target = location.IsAbsoluteUri ? location : new Uri(target, location);
                        if (await OutboundConnectGuard.CheckDestinationAsync(target, allowedHosts, context.CancellationToken).ConfigureAwait(false) is { } hopRefusal)
                        {
                            await FailAsync(context, "EGRESS_REFUSED", $"Redirect to '{target}' refused: {hopRefusal}").ConfigureAwait(false);
                            return;
                        }

                        if (status == 303 || ((status == 301 || status == 302) && method == HttpMethod.Post))
                        {
                            method = HttpMethod.Get;
                            body = null;
                            sendPath = null;
                        }

                        continue;
                    }

                    context.Set(StatusCode, status);
                    context.Set(ResponseContentType, response.Content.Headers.ContentType?.ToString());
                    if (response.IsSuccessStatusCode && savePath is not null)
                    {
                        var saved = await SaveAsync(response, savePath, maxFileBytes, context).ConfigureAwait(false);
                        if (saved < 0)
                        {
                            await FailAsync(context, "FILE_TOO_LARGE", $"The response is over Pipelines:Files:MaxFileBytes ({maxFileBytes}); nothing was saved to '{saveTo}'.").ConfigureAwait(false);
                            return;
                        }

                        context.Set(SavedBytes, saved);
                        context.AddExecutionLogEntry("Saved", $"Saved {saved} bytes to '{saveTo}'.");
                        await SucceedAsync(context, saveTo).ConfigureAwait(false);
                        return;
                    }

                    var text = await ReadBoundedAsync(response, options.MaxResponseBytes, context).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        await SucceedAsync(context, text).ConfigureAwait(false);
                        return;
                    }

                    context.Set(Result, text);
                    await FailAsync(context, "HTTP_ERROR", $"HTTP {status} {response.ReasonPhrase}: {Truncate(text)}").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (OutboundConnectGuard.IsDenial(ex))
            {
                await FailAsync(context, "EGRESS_REFUSED", OutboundConnectGuard.FindDenial(ex)!.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                await FailAsync(context, "HTTP_REQUEST_FAILED", ex is TaskCanceledException ? $"No response within {timeout} seconds." : ex.Message).ConfigureAwait(false);
            }
        }

        // The extra headers and the Authorization, onto a request (its content's headers when it has a body).
        private static void AddHeaders(HttpRequestMessage request, IReadOnlyList<(string Name, string Value)> headers)
        {
            foreach (var (name, value) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        // Pagination: page after page, each address checked by the guard and each redirect followed as one request's are, every
        // page's records gathered; it stops when there is no next page, a page repeats, or at the most pages.
        private async Task RunPagesAsync(StepExecutionContext context, HttpStepOptions options, Uri first, IReadOnlyCollection<string> allowedHosts,
            IReadOnlyList<(string Name, string Value)> headers, HttpPagination pagination, string? recordsField, string? next, int maxPages, TimeSpan timeout)
        {
            var page = 1;
            var url = first;
            if (pagination == HttpPagination.PageNumber)
            {
                var given = QueryHelpers.ParseQuery(first.Query).TryGetValue(next!, out var values) ? values.ToString() : null;
                page = int.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? start : 1;
                url = WithQuery(first, next!, page);
            }

            var gathered = new JsonArray();
            var read = new HashSet<string>(StringComparer.Ordinal);
            var bytes = 0L;
            var pages = 0;
            using var handler = OutboundConnectGuard.CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
            try
            {
                while (url is not null && pages < maxPages && read.Add(url.AbsoluteUri))
                {
                    if (await OutboundConnectGuard.CheckDestinationAsync(url, allowedHosts, context.CancellationToken).ConfigureAwait(false) is { } refusal)
                    {
                        await FailAsync(context, "EGRESS_REFUSED", $"Page {pages + 1} at '{url}' refused: {refusal}").ConfigureAwait(false);
                        return;
                    }

                    using var response = await GetFollowingAsync(client, url, allowedHosts, headers, options, context).ConfigureAwait(false);
                    if (response is null)
                    {
                        return; // refused, said so
                    }

                    pages++;
                    var text = await ReadBoundedAsync(response, options.MaxResponseBytes, context).ConfigureAwait(false);
                    context.Set(StatusCode, (int)response.StatusCode);
                    context.Set(ResponseContentType, response.Content.Headers.ContentType?.ToString());
                    context.Set(Pages, pages);
                    if (!response.IsSuccessStatusCode)
                    {
                        context.Set(Result, text);
                        await FailAsync(context, "HTTP_ERROR", $"Page {pages}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text)}").ConfigureAwait(false);
                        return;
                    }

                    bytes += Encoding.UTF8.GetByteCount(text);
                    if (bytes > options.MaxResponseBytes)
                    {
                        await FailAsync(context, "RESPONSE_TOO_LARGE", $"The pages are over Pipelines:Http:MaxResponseBytes ({options.MaxResponseBytes}) together, at page {pages}.").ConfigureAwait(false);
                        return;
                    }

                    JsonNode? body;
                    try
                    {
                        body = JsonNode.Parse(text);
                    }
                    catch (JsonException)
                    {
                        await FailAsync(context, "PAGE_NOT_RECORDS", $"Page {pages} is not JSON.").ConfigureAwait(false);
                        return;
                    }

                    var list = string.IsNullOrWhiteSpace(recordsField) ? body as JsonArray
                        : body is JsonObject whole ? Records.ReadPath(whole, recordsField.Trim()) as JsonArray : null;
                    if (list is null)
                    {
                        await FailAsync(context, "PAGE_NOT_RECORDS", string.IsNullOrWhiteSpace(recordsField)
                            ? $"Page {pages}'s body is not a list - name the field holding its records in 'Records field'."
                            : $"Page {pages}'s body has no list at '{recordsField.Trim()}'.").ConfigureAwait(false);
                        return;
                    }

                    foreach (var record in list)
                    {
                        gathered.Add(record?.DeepClone());
                    }

                    url = pagination switch
                    {
                        HttpPagination.NextLinkHeader => NextLink(response, url),
                        HttpPagination.NextUrlInBody => body is JsonObject withNext && Records.Text(Records.ReadPath(withNext, next!)) is { Length: > 0 } link && Uri.TryCreate(url, link, out var to) ? to : null,
                        _ => list.Count == 0 ? null : WithQuery(url, next!, ++page),
                    };
                }

                if (url is not null && pages >= maxPages && !read.Contains(url.AbsoluteUri))
                {
                    context.AddExecutionLogEntry("Truncated", $"Stopped after {pages} pages ('Most pages'); there were more.");
                }

                await SucceedAsync(context, gathered.ToJsonString()).ConfigureAwait(false);
            }
            catch (Exception ex) when (OutboundConnectGuard.IsDenial(ex))
            {
                await FailAsync(context, "EGRESS_REFUSED", OutboundConnectGuard.FindDenial(ex)!.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                await FailAsync(context, "HTTP_REQUEST_FAILED", ex is TaskCanceledException ? $"No response within {timeout.TotalSeconds} seconds." : ex.Message).ConfigureAwait(false);
            }
        }

        // One GET, redirects followed and each hop checked; null when a hop was refused (and said so).
        private async Task<HttpResponseMessage?> GetFollowingAsync(HttpClient client, Uri url, IReadOnlyCollection<string> allowedHosts,
            IReadOnlyList<(string Name, string Value)> headers, HttpStepOptions options, StepExecutionContext context)
        {
            var target = url;
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowedHosts);
                AddHeaders(request, headers);
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hop < options.MaxRedirects)
                {
                    response.Dispose();
                    target = location.IsAbsoluteUri ? location : new Uri(target, location);
                    if (await OutboundConnectGuard.CheckDestinationAsync(target, allowedHosts, context.CancellationToken).ConfigureAwait(false) is { } hopRefusal)
                    {
                        await FailAsync(context, "EGRESS_REFUSED", $"Redirect to '{target}' refused: {hopRefusal}").ConfigureAwait(false);
                        return null;
                    }

                    continue;
                }

                return response;
            }
        }

        // RFC 8288: <url>; rel="next" - relative to the page it came with.
        private static Uri? NextLink(HttpResponseMessage response, Uri page)
        {
            if (!response.Headers.TryGetValues("Link", out var values))
            {
                return null;
            }

            foreach (var part in values.SelectMany(value => value.Split(',')))
            {
                var open = part.IndexOf('<');
                var close = part.IndexOf('>');
                if (open < 0 || close <= open)
                {
                    continue;
                }

                var parameters = part[(close + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parameters.Any(parameter => parameter.Replace(" ", "").Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase) || parameter.Replace(" ", "").Equals("rel=next", StringComparison.OrdinalIgnoreCase))
                    && Uri.TryCreate(page, part[(open + 1)..close].Trim(), out var next))
                {
                    return next;
                }
            }

            return null;
        }

        // The URL with one query parameter set to the page number, the others kept.
        private static Uri WithQuery(Uri url, string name, int value)
        {
            var kept = QueryHelpers.ParseQuery(url.Query)
                .Where(pair => !string.Equals(pair.Key, name, StringComparison.Ordinal))
                .SelectMany(pair => pair.Value.Select(item => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(item ?? "")))
                .Append(Uri.EscapeDataString(name) + "=" + value.ToString(CultureInfo.InvariantCulture));
            return new UriBuilder(url) { Query = string.Join("&", kept) }.Uri;
        }

        // The file as a request body: its bytes, typed by the Content type when it was changed from the default, else by the file's
        // extension; or a multipart form's one file under the field named.
        private static HttpContent FileContent(string path, string? field, string? contentType)
        {
            var type = string.IsNullOrWhiteSpace(contentType) || contentType == DefaultContentType ? TypeOf(path) : contentType;
            var file = new StreamContent(File.OpenRead(path));
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            if (string.IsNullOrWhiteSpace(field))
            {
                return file;
            }

            var form = new MultipartFormDataContent();
            form.Add(file, field.Trim(), System.IO.Path.GetFileName(path));
            return form;
        }

        private static string TypeOf(string path) =>
            new FileExtensionContentTypeProvider().TryGetContentType(path, out var type) ? type : "application/octet-stream";

        // The response streamed into a file beside the target, then moved over it - so a failed or oversized download never
        // leaves half a file under the name. -1 when it is over the limit (nothing kept).
        private static async Task<long> SaveAsync(HttpResponseMessage response, string path, long maxBytes, StepExecutionContext context)
        {
            await using var source = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
            return await PipelineConnectorFiles.WriteBoundedAsync(source, path, maxBytes, context.CancellationToken).ConfigureAwait(false);
        }

        /// <summary>A response's text, cut at <paramref name="maxBytes"/> (logged when it is) - also the RSS feed step's.</summary>
        internal static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, StepExecutionContext context)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var total = 0L;
            var cut = false;
            while (true)
            {
                var read = await stream.ReadAsync(chunk, context.CancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (total + read > maxBytes)
                {
                    buffer.Write(chunk, 0, (int)(maxBytes - total));
                    cut = true;
                    break;
                }

                buffer.Write(chunk, 0, read);
                total += read;
            }

            if (cut)
            {
                context.AddExecutionLogEntry("Truncated", $"The response was cut at Pipelines:Http:MaxResponseBytes ({maxBytes}).");
            }

            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset.Trim('"'));
                }
                catch (ArgumentException)
                {
                }
            }

            return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
    }
}
