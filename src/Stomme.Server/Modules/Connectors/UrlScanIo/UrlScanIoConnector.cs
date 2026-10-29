#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.UrlScanIo
{
    /// <summary>
    /// urlscan.io (BL-229, n8n's urlscan.io node: a URL scanned, a scan read, scans searched) over urlscan.io's API v1
    /// (<c>https://urlscan.io/api/v1</c>) by an API key in its <c>API-Key</c> header - never in the address. A test asks
    /// urlscan.io for the key's quotas.
    /// </summary>
    /// <remarks>
    /// <c>scan</c> (a URL submitted - public, unlisted or private, n8n's private by default - with up to ten tags, a user agent,
    /// a referer and whether urlscan.io may reclassify it; and, beyond n8n's node, waiting for its result when asked, so a run
    /// has the verdict without a Wait step), <c>result</c> (a scan's result: its verdict, score, page, screenshot and urlscan.io's
    /// own sections - asked again, a few times, while urlscan.io says the scan is not finished) and <c>search</c> (scans by
    /// urlscan.io's query - domain:kafe.example - page by page by its search_after cursor, 100 a page, up to <c>max</c>). Every
    /// URL, id, tag, choice and length held to urlscan.io's shape before anything is sent; urlscan.io's message and description
    /// on a failure, a 429 its quota's.
    /// </remarks>
    public sealed class UrlScanIoConnector(TimeSpan? pollInterval = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://urlscan.io";

        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100; // urlscan.io's search page, as n8n's node asks it

        private const int MostTags = 10;

        private const int WaitTries = 30; // a scan waited for: about a minute at two seconds a try

        private const int ResultTries = 5; // a result read: a scan just finishing, not one never made - urlscan.io answers 404 for both

        private static readonly string[] Known = { "scan", "result", "search" };

        private static readonly string[] Visibilities = { "public", "unlisted", "private" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(2);

        public string ConnectorType => "urlscanio";

        public string DisplayName => "urlscan.io";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (urlscan.io Settings & API)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://urlscan.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://urlscan.io/docs/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("scan", "Scan a URL", "n8n's Perform - and its result, if asked to wait.")
            {
                Parameters = new(new[]
                {
                    Field("url", "URL", "https://kassa.example/login.", required: true),
                    Field("visibility", "Visibility", "private (n8n's default), unlisted or public."),
                    Field("tags", "Tags", "Up to ten, by commas - phishing, kafe."),
                    Field("custom_agent", "User agent", "The browser urlscan.io says it is."),
                    Field("referer", "Referer", "https://kafe.example/."),
                    Field("override_safety", "Override safety", "true: urlscan.io may not make it private for what it finds."),
                    Field("wait", "Wait for the result", "true: the step's record is the scan's result, its verdict among it - about a minute at most."),
                }),
            },
            new ConnectorOperation("result", "Get a scan's result", "n8n's Get.") { Parameters = new(new[] { Field("scan", "Scan", "Its id - the uuid a scan answered.", required: true) }) },
            new ConnectorOperation("search", "Search scans", "n8n's Get Many.")
            {
                Parameters = new(new[]
                {
                    Field("query", "Query", "urlscan.io's - domain:kafe.example, page.ip:203.0.113.7. Empty: the key's own recent scans."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many scans, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var (_, answer) = await session.SendAsync(HttpMethod.Get, "/user/quotas/", null, cancellationToken);
                return answer?["limits"] is JsonObject limits
                    ? ConnectionTestResult.Ok($"urlscan.io answers - {Number(limits["public"]?["day"]?["remaining"] ?? limits["public"]?["day"]?["limit"])} public scans left today.")
                    : ConnectionTestResult.Fail("urlscan.io answered without the key's quotas - check the address: a sign-in page or a proxy answers so.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "scan").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"urlscan.io has no operation '{context.OperationName}' - scan, result or search.");
            var p = context.Parameters;

            // Everything held to urlscan.io's shape before anything is sent.
            JsonObject? body = null;
            string? scanId = null;
            string? query = null;
            var wait = false;
            var max = DefaultMax;
            switch (operation)
            {
                case "scan":
                    {
                        var url = Values.Get(p, "url") ?? throw new InvalidOperationException("Give the URL to scan - https://kassa.example/login (url).");
                        body = new JsonObject { ["url"] = Link(url, "url") };
                        var visibility = (Values.Get(p, "visibility") ?? "private").ToLowerInvariant();
                        body["visibility"] = Visibilities.Contains(visibility) ? visibility : throw new InvalidOperationException($"'{Values.Get(p, "visibility")}' is not public, unlisted or private (visibility).");
                        if (Values.Get(p, "tags") is { } given)
                        {
                            var tags = given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
                            if (tags.Count > MostTags)
                            {
                                throw new InvalidOperationException($"A scan has at most {MostTags} tags - these are {tags.Count} (tags).");
                            }

                            body["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)(tag.Length <= 100 && !tag.Any(char.IsControl) ? tag : throw new InvalidOperationException($"A tag is at most 100 characters, on one line (tags)."))).ToArray());
                        }

                        if (Values.Get(p, "custom_agent") is { } agent)
                        {
                            body["customagent"] = agent.Length <= 512 && !agent.Any(char.IsControl) ? agent : throw new InvalidOperationException("A user agent is at most 512 characters, on one line (custom_agent).");
                        }

                        if (Values.Get(p, "referer") is { } referer)
                        {
                            body["referer"] = Link(referer, "referer");
                        }

                        if (Values.Get(p, "override_safety") is { } overrideSafety)
                        {
                            body["overrideSafety"] = bool.TryParse(overrideSafety, out var safety) ? safety : throw new InvalidOperationException($"'{overrideSafety}' is not true or false (override_safety).");
                        }

                        if (Values.Get(p, "wait") is { } waiting)
                        {
                            wait = bool.TryParse(waiting, out var flag) ? flag : throw new InvalidOperationException($"'{waiting}' is not true or false (wait).");
                        }

                        break;
                    }

                case "result":
                    scanId = Scan(p);
                    break;
                default:
                    if (Values.Get(p, "query") is { } q)
                    {
                        query = q.Length <= 2000 && !q.Any(char.IsControl) ? q : throw new InvalidOperationException("A query is at most 2000 characters, on one line (query).");
                    }

                    max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
                    break;
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            switch (operation)
            {
                case "scan":
                    {
                        var (_, answer) = await session.SendAsync(HttpMethod.Post, "/scan/", body, cancellationToken);
                        var submitted = answer as JsonObject ?? throw new InvalidOperationException("urlscan.io answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                        var uuid = Text(submitted["uuid"]) ?? throw new InvalidOperationException("urlscan.io answered without the scan's id - check the address: a sign-in page or a proxy answers so.");
                        if (!wait)
                        {
                            yield return Record(submitted, ("id", uuid));
                            break;
                        }

                        yield return await ResultAsync(session, uuid, WaitTries, cancellationToken);
                        break;
                    }

                case "result":
                    yield return await ResultAsync(session, scanId!, ResultTries, cancellationToken);
                    break;
                default:
                    {
                        // Page by page by urlscan.io's search_after cursor - the last result's sort - while it has more.
                        string? after = null;
                        var read = 0;
                        while (true)
                        {
                            var address = "/search/?size=" + Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)
                                + (query is null ? string.Empty : "&q=" + Uri.EscapeDataString(query))
                                + (after is null ? string.Empty : "&search_after=" + Uri.EscapeDataString(after));
                            var (_, answer) = await session.SendAsync(HttpMethod.Get, address, null, cancellationToken);
                            var results = (answer as JsonObject)?["results"] as JsonArray ?? throw new InvalidOperationException("urlscan.io answered without its results - check the address: a sign-in page or a proxy answers so.");
                            JsonObject? last = null;
                            foreach (var found in results.OfType<JsonObject>())
                            {
                                last = found;
                                yield return Record(found, ("id", Text(found["_id"])));
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (last?["sort"] is not JsonArray sort || sort.Count == 0 || answer?["has_more"] is JsonValue more && more.TryGetValue<bool>(out var hasMore) && !hasMore)
                            {
                                break;
                            }

                            after = string.Join(",", sort.Select(part => part is JsonValue value && value.TryGetValue<string>(out var text) ? text : part?.ToJsonString()));
                        }

                        break;
                    }
            }
        }

        // A scan's result: urlscan.io answers 404 while the scan is still running - asked again, a few times, before it is said.
        private async Task<ConnectorRecord> ResultAsync(Session session, string uuid, int tries, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                var (status, answer) = await session.SendAsync(HttpMethod.Get, $"/result/{uuid}/", null, cancellationToken, pending: true);
                if (status != HttpStatusCode.NotFound)
                {
                    var result = answer as JsonObject ?? throw new InvalidOperationException("urlscan.io answered with what is not a scan's result - check the address: a sign-in page or a proxy answers so.");
                    var verdict = result["verdicts"]?["overall"];
                    return Record(result,
                        ("id", uuid),
                        ("malicious", verdict?["malicious"] is JsonValue malicious && malicious.TryGetValue<bool>(out var bad) ? bad : null),
                        ("score", verdict?["score"] is JsonValue score && score.TryGetValue<long>(out var points) ? points : null),
                        ("page_url", Text(result["page"]?["url"])),
                        ("page_domain", Text(result["page"]?["domain"])),
                        ("page_ip", Text(result["page"]?["ip"])),
                        ("page_country", Text(result["page"]?["country"])),
                        ("screenshot", Text(result["task"]?["screenshotURL"])));
                }

                if (attempt == tries)
                {
                    throw new InvalidOperationException($"urlscan.io had no result for the scan {uuid} after {tries} asks - {Text(answer?["message"]) ?? "not found"}: read it later with 'result', or check its id.");
                }

                await Task.Delay(poll, cancellationToken);
            }
        }

        private static string Link(string given, string name) =>
            given.Length <= 2048 && Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var link) && link.Scheme is "https" or "http" && link.Host.Length > 0 && link.UserInfo.Length == 0
                ? given
                : throw new InvalidOperationException($"'{given}' is not a web address - https://kassa.example/login ({name}).");

        private static string Scan(IReadOnlyDictionary<string, string> p)
        {
            var given = Values.Get(p, "scan") ?? throw new InvalidOperationException("Give the scan - its id, the uuid a scan answered (scan).");
            return Guid.TryParseExact(given, "D", out var uuid) ? uuid.ToString("D") : throw new InvalidOperationException($"'{given}' is not a scan's id - 0196c4c4-9a2b-7000-8000-0123456789ab (scan).");
        }

        // An object as a record: its plain values as they are, a list or an object as JSON, the named fields beside them.
        private static ConnectorRecord Record(JsonObject data, params (string Name, object? Value)[] beside)
        {
            var fields = data.Where(pair => pair.Key != "_id").ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                JsonValue value when value.TryGetValue<long>(out var whole) => whole,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                var other => (object?)other.ToJsonString(),
            }, StringComparer.Ordinal);
            foreach (var (name, value) in beside)
            {
                fields[name] = value;
            }

            return new ConnectorRecord(fields);
        }

        private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string key;

            private Session(HttpClient client, string api, string key)
            {
                this.client = client;
                this.api = api;
                this.key = key;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/api/v1", StringComparison.Ordinal))
                {
                    root = root[..^"/api/v1".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not urlscan.io's address - its own, {DefaultEndpoint}.");
                }

                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The urlscan.io connection has no API key.");
                return new Session(egress.CreateHttpClient(timeout), root + "/api/v1", key);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the key in its header; the status and JSON, or urlscan.io's message and description - a pending scan's 404 handed back when asked.</summary>
            public async Task<(HttpStatusCode Status, JsonNode? Json)> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken, bool pending = false)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Add("API-Key", key);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("urlscan.io answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is JsonObject ? (response.StatusCode, json) : throw new InvalidOperationException("urlscan.io answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                if (pending && response.StatusCode == HttpStatusCode.NotFound && json is JsonObject)
                {
                    return (response.StatusCode, json); // a scan not finished yet, as urlscan.io says it
                }

                var status = (int)response.StatusCode;
                var said = string.Join(" - ", new[] { Text(json?["message"]), Text(json?["description"]) }.Where(part => !string.IsNullOrEmpty(part)));
                said = said.Length > 300 ? said[..300] : said;
                throw new InvalidOperationException($"urlscan.io answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 401 ? " - check the API key." : status == 429 ? " - the key's quota is spent; try again later." : string.Empty));
            }
        }
    }
}
