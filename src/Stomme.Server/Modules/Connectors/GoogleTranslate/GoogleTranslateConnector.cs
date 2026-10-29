#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.GoogleTranslate
{
    /// <summary>
    /// Google Translate (n8n's Google Translate node) over Cloud Translation's Basic API
    /// (<c>https://translation.googleapis.com/language/translate/v2</c>) as a person signed in - an OAuth client's refresh token
    /// granted the cloud-translation scope, exchanged at Google's token endpoint through the shared
    /// <see cref="OAuth2TokenProvider"/>, as Gmail's is. A test lists the languages Google translates.
    /// </summary>
    /// <remarks>
    /// <c>translate</c> (a text - over several lines from the step's Query box - or a JSON array of texts, into a language, from
    /// one given or detected, as text or HTML, by a model if asked - n8n's Translate); <c>detect</c> (each text's language, its
    /// confidence and whether Google calls it reliable); <c>languages</c> (the languages Google translates, named in a language
    /// if asked). Every language held to a language tag's shape, a format and a model to Google's own, and a request to Google's
    /// 100K bytes before a token is asked for; Google's status and message on a failure, a 401 or 403 pointing at the token's
    /// scopes.
    /// </remarks>
    public sealed class GoogleTranslateConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://translation.googleapis.com";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        private const int MaxRequestBytes = 100_000; // Cloud Translation Basic's own limit on a request

        private static readonly string[] Known = { "translate", "detect", "languages" };

        private static readonly Regex Language = new("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8}){0,2}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googletranslate";

        public string DisplayName => "Google Translate";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "OAuth client id (Google Cloud console > APIs & Services > Credentials)",
            ["client-secret"] = "OAuth client secret (the same client)",
            ["refresh-token"] = "Refresh token (granted cloud-translation)",
        };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://cloud.google.com/translate");

        public Uri ApiDocumentationUrl { get; } = new("https://cloud.google.com/translate/docs/reference/rest/v2/translate");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false, Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static readonly ConnectorConfigField TextField = Field("text", "Text", "What to translate - over several lines, from the step's Query box.");

        private static readonly ConnectorConfigField TextsField = Field("texts", "Texts", "Several at once - a JSON array, [\"Kassan är stängd.\", \"Åter klockan åtta.\"].");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("translate", "Translate", "A text into a language - n8n's Translate.")
            {
                Parameters = new(new[]
                {
                    TextField,
                    TextsField,
                    Field("to", "Into", "The language - sv, en, zh-CN.", required: true),
                    Field("from", "From", "Its language. Empty: Google detects it."),
                    Field("format", "Format", "text (the default) or html."),
                    Field("model", "Model", "nmt or base. Empty: Google's choice."),
                }),
            },
            new ConnectorOperation("detect", "Detect a language", "Each text's language and how sure Google is.") { Parameters = new(new[] { TextField, TextsField }) },
            new ConnectorOperation("languages", "Get languages", "The languages Google translates.") { Parameters = new(new[] { Field("names_in", "Named in", "A language to name them in - sv. Empty: their codes alone.") }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                var answer = await session.JsonAsync(HttpMethod.Get, "/languages", null, cancellationToken);
                return ConnectionTestResult.Ok($"Google Translate answers - {(answer?["data"]?["languages"] as JsonArray)?.Count ?? 0} languages.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "translate").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Translate has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var body = new JsonObject();
            var texts = new List<string>();
            string route;
            switch (operation)
            {
                case "translate" or "detect":
                    {
                        texts = Texts(p);
                        body["q"] = new JsonArray(texts.Select(text => (JsonNode?)text).ToArray());
                        if (operation == "translate")
                        {
                            body["target"] = Tag(Values.Get(p, "to") ?? throw new InvalidOperationException("Give the language to translate into - sv, en (to)."), "to");
                            if (Values.Get(p, "from") is { } from)
                            {
                                body["source"] = Tag(from, "from");
                            }

                            body["format"] = (Values.Get(p, "format") ?? "text").ToLowerInvariant() is ("text" or "html") and var format ? format : throw new InvalidOperationException($"'{Values.Get(p, "format")}' is not text or html (format).");
                            if (Values.Get(p, "model") is { } model)
                            {
                                body["model"] = model.ToLowerInvariant() is ("nmt" or "base") and var named ? named : throw new InvalidOperationException($"'{model}' is not nmt or base (model).");
                            }

                            route = string.Empty;
                        }
                        else
                        {
                            route = "/detect";
                        }

                        if (Encoding.UTF8.GetByteCount(body.ToJsonString(Wire)) > MaxRequestBytes)
                        {
                            throw new InvalidOperationException($"The texts are more than Google Translate takes in one request - {MaxRequestBytes / 1000}K bytes; split them over steps (text, texts).");
                        }

                        break;
                    }

                default:
                    route = "/languages" + (Values.Get(p, "names_in") is { } namesIn ? "?target=" + Uri.EscapeDataString(Tag(namesIn, "names_in")) : string.Empty);
                    break;
            }

            using var session = await Session.OpenAsync(this, context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            switch (operation)
            {
                case "translate":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, route, body, cancellationToken);
                        var translations = (answer?["data"]?["translations"] as JsonArray ?? new JsonArray()).ToList();
                        for (var i = 0; i < translations.Count; i++)
                        {
                            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = i < texts.Count ? texts[i] : null, ["to"] = Text(body["target"]) };
                            foreach (var (name, value) in translations[i] as JsonObject ?? new JsonObject())
                            {
                                fields[name] = Text(value);
                            }

                            yield return new ConnectorRecord(fields);
                        }

                        break;
                    }

                case "detect":
                    {
                        var answer = await session.JsonAsync(HttpMethod.Post, route, body, cancellationToken);
                        var detections = (answer?["data"]?["detections"] as JsonArray ?? new JsonArray()).ToList();
                        for (var i = 0; i < detections.Count; i++)
                        {
                            // Each text's detections, the likeliest first.
                            var best = (detections[i] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["text"] = i < texts.Count ? texts[i] : null,
                                ["language"] = Text(best["language"]),
                                ["confidence"] = best["confidence"] is JsonValue confidence && confidence.TryGetValue<double>(out var sure) ? sure : null,
                                ["isReliable"] = best["isReliable"] is JsonValue reliable && reliable.TryGetValue<bool>(out var known) ? known : null,
                            });
                        }

                        break;
                    }

                default:
                    {
                        var answer = await session.JsonAsync(HttpMethod.Get, route, null, cancellationToken);
                        foreach (var language in (answer?["data"]?["languages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(language.ToDictionary(pair => pair.Key, pair => (object?)Text(pair.Value), StringComparer.Ordinal));
                        }

                        break;
                    }
            }
        }

        // The texts: a JSON array of them, or one - over several lines from the step's Query box.
        private static List<string> Texts(IReadOnlyDictionary<string, string> p)
        {
            if (Values.Get(p, "texts") is { } given)
            {
                JsonArray? array;
                try
                {
                    array = JsonNode.Parse(given) as JsonArray;
                }
                catch (JsonException)
                {
                    array = null;
                }

                var texts = array?.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).ToList();
                return texts is { Count: > 0 } && texts.All(text => !string.IsNullOrWhiteSpace(text))
                    ? texts!
                    : throw new InvalidOperationException("The texts are a JSON array of texts, none empty - [\"Kassan är stängd.\"] (texts).");
            }

            return new List<string>
            {
                (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : Values.Get(p, "text"))
                    ?? throw new InvalidOperationException("Give the text (text) - over several lines in the step's Query box - or several as a JSON array (texts)."),
            };
        }

        private static string Tag(string given, string name) =>
            Language.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a language Google names - sv, en, zh-CN ({name}).");

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string token;
            private readonly string api;

            private Session(HttpClient client, string token, string api)
            {
                this.client = client;
                this.token = token;
                this.api = api;
            }

            public static async Task<Session> OpenAsync(GoogleTranslateConnector connector, string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var root = Values.BaseUrl(endpoint, DefaultEndpoint);
                if (root.EndsWith("/language/translate/v2", StringComparison.Ordinal))
                {
                    root = root[..^"/language/translate/v2".Length]; // the API's own address given: its host
                }

                var uri = new Uri(root);
                if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Google Translate's address - its own, {DefaultEndpoint}.");
                }

                var token = await connector.tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
                {
                    ClientId = Values.Get(credentials, "client-id"),
                    ClientSecret = Values.Get(credentials, "client-secret"),
                    Timeout = timeout,
                }, cancellationToken);
                return new Session(egress.CreateHttpClient(timeout), token, root + "/language/translate/v2");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A call's JSON, or Google's refusal with its status and message.</summary>
            public async Task<JsonNode?> JsonAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Google Translate answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json;
                }

                var status = (int)response.StatusCode;
                var error = json?["error"];
                var message = Text(error?["message"]) is { } said ? said.Length > 300 ? said[..300] : said : null;
                throw new InvalidOperationException($"Google Translate answered HTTP {status}{(Text(error?["status"]) is { } kind ? " " + kind : string.Empty)}{(message is null ? "." : ": " + message)}"
                    + (status is 401 or 403 ? " - check the refresh token and its scopes." : string.Empty));
            }
        }
    }
}
