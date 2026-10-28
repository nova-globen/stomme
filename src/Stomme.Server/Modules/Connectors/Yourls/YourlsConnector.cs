#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Yourls
{
    /// <summary>
    /// YOURLS (BL-229, n8n's YOURLS node: a URL shortened, a short URL expanded, its stats read) over YOURLS's API
    /// (<c>{address}/yourls-api.php</c>) by its signature token, sent in the request's form body - never in the address. A test
    /// asks YOURLS for its version.
    /// </summary>
    /// <remarks>
    /// <c>shorten</c> (a URL, with a keyword and a title if given; a URL YOURLS already has answered as its existing short URL,
    /// marked <c>existing</c>, where n8n's node failed - YOURLS says 409 with the link), <c>expand</c> and <c>stats</c> (a short
    /// URL as its keyword or its whole address). A URL is held to a web address before it is sent - YOURLS itself stores "not a
    /// url" as a link (measured) - and a keyword to YOURLS's letters and digits. YOURLS's message on a failure: a keyword taken,
    /// a short URL not found, a wrong signature. Measured against YOURLS 1.10.6 (<c>scripts/yourls-test-server.ps1</c>).
    /// </remarks>
    public sealed class YourlsConnector : IConnector, IConnectorExtractor
    {
        private static readonly string[] Known = { "shorten", "expand", "stats" };

        // A new keyword: YOURLS's default characters, lowercase letters and digits - it silently drops the rest, an uppercase letter
        // among them (measured: MenyAB1 became eny1, meny-x meny x), so anything else is refused rather than changed behind the step's back.
        private static readonly Regex KeywordPattern = new("^[0-9a-z]{1,200}$", RegexOptions.CultureInvariant);

        // An existing short URL's keyword, looked up: letters and digits, as an installation with YOURLS's 62-character set has them.
        private static readonly Regex LookupPattern = new("^[0-9A-Za-z]{1,200}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "yourls";

        public string DisplayName => "YOURLS";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Signature token (YOURLS admin - Tools)" };

        public string EndpointExample => "https://sho.rt";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://yourls.org/");

        public Uri ApiDocumentationUrl { get; } = new("https://yourls.org/docs/guide/advanced/api");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField ShortUrl = Field("short_url", "Short URL", "Its keyword (meny1) or its whole address (https://sho.rt/meny1).", required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("shorten", "Shorten a URL", "n8n's Shorten - a URL YOURLS already has comes back as its existing short URL.")
            {
                Parameters = new(new[]
                {
                    Field("url", "URL", "https://kafe.example/menu.", required: true),
                    Field("keyword", "Keyword", "The short URL's own - meny1. Empty: YOURLS's next."),
                    Field("title", "Title", "Dagens meny. Empty: the page's own, as YOURLS reads it."),
                }),
            },
            new ConnectorOperation("expand", "Expand a short URL", "n8n's Expand.") { Parameters = new(new[] { ShortUrl }) },
            new ConnectorOperation("stats", "Get a short URL's stats", "n8n's Stats - its clicks, title and date.") { Parameters = new(new[] { ShortUrl }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var (_, answer) = await session.SendAsync(new Dictionary<string, string> { ["action"] = "version" }, cancellationToken);
                return Text(answer?["version"]) is { } version
                    ? ConnectionTestResult.Ok($"YOURLS {version} answers.")
                    : ConnectionTestResult.Fail("YOURLS answered without its version - check the address: a sign-in page or a proxy answers so.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "shorten").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"YOURLS has no operation '{context.OperationName}' - shorten, expand or stats.");
            var p = context.Parameters;

            // Everything held to YOURLS's shape before anything is sent.
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            if (operation == "shorten")
            {
                var url = Values.Get(p, "url") ?? throw new InvalidOperationException("Give the URL to shorten - https://kafe.example/menu (url).");
                form["action"] = "shorturl";
                form["url"] = url.Length <= 8000 && Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Scheme is "https" or "http" && link.Host.Length > 0 && link.UserInfo.Length == 0
                    ? url
                    : throw new InvalidOperationException($"'{(url.Length > 60 ? url[..60] + "..." : url)}' is not a web address - https://kafe.example/menu (url).");
                if (Values.Get(p, "keyword") is { } keyword)
                {
                    form["keyword"] = KeywordPattern.IsMatch(keyword) ? keyword : throw new InvalidOperationException($"'{keyword}' is not a keyword - YOURLS's are lowercase letters and digits, and it would drop the rest: meny1 (keyword).");
                }

                if (Values.Get(p, "title") is { } title)
                {
                    form["title"] = title.Length <= 500 && !title.Any(char.IsControl) ? title : throw new InvalidOperationException("A title is at most 500 characters, on one line (title).");
                }
            }
            else
            {
                form["action"] = operation == "expand" ? "expand" : "url-stats";
                form["shorturl"] = Short(Values.Get(p, "short_url") ?? throw new InvalidOperationException("Give the short URL - its keyword (meny1) or its whole address (short_url)."));
            }

            using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            var (status, answer) = await session.SendAsync(form, cancellationToken, existing: operation == "shorten");
            switch (operation)
            {
                case "shorten":
                    {
                        var made = answer?["url"] as JsonObject ?? throw new InvalidOperationException("YOURLS answered without the link - check the address: a sign-in page or a proxy answers so.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["short_url"] = Text(answer?["shorturl"]),
                            ["keyword"] = Text(made["keyword"]),
                            ["url"] = Text(made["url"]),
                            ["title"] = Text(answer?["title"]) ?? Text(made["title"]),
                            ["date"] = Text(made["date"]),
                            ["existing"] = status == HttpStatusCode.Conflict, // YOURLS had it already: its short URL, as it was
                        });
                        break;
                    }

                case "expand":
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["short_url"] = Text(answer?["shorturl"]),
                        ["keyword"] = Text(answer?["keyword"]),
                        ["url"] = Text(answer?["longurl"]) ?? throw new InvalidOperationException("YOURLS answered without the long URL - check the address: a sign-in page or a proxy answers so."),
                        ["title"] = Text(answer?["title"]),
                    });
                    break;
                default:
                    {
                        var link = answer?["link"] as JsonObject ?? throw new InvalidOperationException("YOURLS answered without the link's stats - check the address: a sign-in page or a proxy answers so.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["short_url"] = Text(link["shorturl"]),
                            ["url"] = Text(link["url"]),
                            ["title"] = Text(link["title"]),
                            ["date"] = Text(link["timestamp"]),
                            ["clicks"] = Number(link["clicks"]),
                        });
                        break;
                    }
            }
        }

        // A short URL as its keyword or its whole address - YOURLS takes either (measured); nothing else is sent.
        private static string Short(string given)
        {
            if (LookupPattern.IsMatch(given))
            {
                return given;
            }

            return Uri.TryCreate(given, UriKind.Absolute, out var link) && link.Scheme is "https" or "http" && link.Query.Length == 0 && link.UserInfo.Length == 0
                && LookupPattern.IsMatch(link.AbsolutePath.Trim('/'))
                ? given
                : throw new InvalidOperationException($"'{given}' is not a short URL - its keyword (meny1) or its whole address, https://sho.rt/meny1 (short_url).");
        }

        private static long? Number(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<long>(out var whole) ? whole : value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed) ? parsed : null
            : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string signature;

            private Session(HttpClient client, string api, string signature)
            {
                this.client = client;
                this.api = api;
                this.signature = signature;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give YOURLS's address - https://sho.rt.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                if (root.EndsWith("/yourls-api.php", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/yourls-api.php".Length]; // the API's own address given: YOURLS's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not YOURLS's address - https://sho.rt, with no query.");
                }

                var signature = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The YOURLS connection has no signature token.");
                return new Session(egress.CreateHttpClient(timeout), root + "/yourls-api.php", signature);
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call with the signature in the form's body; YOURLS's JSON, or its message - a link it already has handed back when asked.</summary>
            public async Task<(HttpStatusCode Status, JsonNode? Json)> SendAsync(Dictionary<string, string> form, CancellationToken cancellationToken, bool existing = false)
            {
                var fields = new Dictionary<string, string>(form, StringComparer.Ordinal) { ["format"] = "json", ["signature"] = signature };
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, api) { Content = new FormUrlEncodedContent(fields) };
                    request.Headers.Accept.ParseAdd("application/json");
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
                    throw new InvalidOperationException("YOURLS answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (response.IsSuccessStatusCode)
                {
                    return json is JsonObject ? (response.StatusCode, json) : throw new InvalidOperationException("YOURLS answered with what is not its answer - check the address: a sign-in page or a proxy answers so.");
                }

                if (existing && response.StatusCode == HttpStatusCode.Conflict && Text(json?["code"]) == "error:url" && json?["url"] is JsonObject)
                {
                    return (response.StatusCode, json); // a URL YOURLS already has: its short URL, as it was
                }

                var status = (int)response.StatusCode;
                var said = Text(json?["message"]) ?? string.Empty;
                said = said.Length > 300 ? said[..300] : said;
                throw new InvalidOperationException($"YOURLS answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 403 ? " - check the signature token (YOURLS admin - Tools)." : string.Empty));
            }
        }
    }
}
