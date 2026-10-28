#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace Stomme.Modules.Connectors.DeepL
{
    /// <summary>
    /// DeepL (BL-229, n8n's DeepL node: a text translated): DeepL's API v2 with an authentication key - its Free API for a key
    /// ending in :fx, its Pro API otherwise - over the host's egress. A test names the plan and how much of its characters are
    /// used.
    /// </summary>
    /// <remarks>
    /// <c>translate</c> (a text, or a JSON list of texts sent fifty to a call, into a language - from one, or detected - with a
    /// formality, sentence splitting, formatting kept, HTML or XML tags, a context, a glossary and a model), <c>languages</c>
    /// (the source or target languages), <c>usage</c> and <c>glossaries</c>. A translation is the text, the language DeepL
    /// detected and the text it came from. Every language, option and glossary held to its shape before anything is sent. A
    /// throttled call waits as DeepL says; a failure is thrown with its status and DeepL's word.
    /// </remarks>
    public sealed class DeepLConnector : IConnector, IConnectorExtractor
    {
        public const string TranslateOperation = "translate";

        public const string LanguagesOperation = "languages";

        public const string UsageOperation = "usage";

        public const string GlossariesOperation = "glossaries";

        public const string FreeEndpoint = "https://api-free.deepl.com";

        public const string ProEndpoint = "https://api.deepl.com";

        // DeepL's own limit of texts in one call.
        private const int Batch = 50;

        private static readonly Regex Language = new("^[A-Za-z]{2,3}(-[A-Za-z]{2,4})?$", RegexOptions.CultureInvariant);

        private static readonly Regex GlossaryId = new("^[0-9a-fA-F-]{8,64}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { TranslateOperation, LanguagesOperation, UsageOperation, GlossariesOperation };

        public string ConnectorType => "deepl";

        public string DisplayName => "DeepL";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        // Left empty, the address follows the key: its Free API for a key ending in :fx.
        public string EndpointExample => ProEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://www.deepl.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.deepl.com/docs/api-reference/translate");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TranslateOperation, "Translate", "A text, or a list of texts, translated - n8n's DeepL Translate.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    Field("text", "Text", "The text to translate.", ConnectorConfigFieldType.Multiline),
                    Field("texts", "Texts", "Or a JSON list of texts - [\"Hello\", \"Goodbye\"] - each translated.", ConnectorConfigFieldType.Multiline),
                    Field("target", "Into", "The language to translate into - DE, EN-GB, PT-BR.", required: true),
                    Field("source", "From", "The language it is in - EN - DeepL's guess when left empty."),
                    Field("formality", "Formality", "more, less, prefer_more, prefer_less or default."),
                    Field("split_sentences", "Split sentences", "1 (the default: on punctuation and newlines), nonewlines or 0."),
                    Field("preserve_formatting", "Keep formatting", "true: punctuation and case left as they are.", ConnectorConfigFieldType.Boolean),
                    Field("tag_handling", "Tags", "html or xml - the text's tags kept."),
                    Field("context", "Context", "Text that helps the translation and is not itself translated.", ConnectorConfigFieldType.Multiline),
                    Field("glossary", "Glossary", "A glossary's id - its pair of languages the translation's."),
                    Field("model", "Model", "quality_optimized, prefer_quality_optimized or latency_optimized."),
                }),
            },
            new ConnectorOperation(LanguagesOperation, "Get languages", "The languages DeepL translates from or into.")
            {
                Parameters = new ConnectorConfigSchema(new[] { Field("type", "Which", "target (the default) or source.") }),
            },
            new ConnectorOperation(UsageOperation, "Get usage", "How many characters the plan has used this period, and its limit.") { Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()) },
            new ConnectorOperation(GlossariesOperation, "Get glossaries", "The account's glossaries.") { Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var usage = await session.SendAsync(HttpMethod.Get, "/usage", null, cancellationToken) as JsonObject;
                var used = Number(usage?["character_count"]);
                var limit = Number(usage?["character_limit"]);
                return ConnectionTestResult.Ok(string.Format(CultureInfo.InvariantCulture, "DeepL API {0}: {1:N0} of {2:N0} characters used this period.", session.Free ? "Free" : "Pro", used ?? 0, limit ?? 0));
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? TranslateOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"DeepL has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            switch (operation)
            {
                case TranslateOperation:
                    {
                        // Everything held to its shape before anything is sent.
                        var texts = new List<string>();
                        if (p.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text))
                        {
                            texts.Add(text);
                        }

                        if (Values.Get(p, "texts") is { } many)
                        {
                            var list = Parse(many) as JsonArray ?? throw new InvalidOperationException("The texts are not a JSON list - [\"Hello\", \"Goodbye\"].");
                            texts.AddRange(list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var one) ? one : throw new InvalidOperationException("The texts are not a JSON list of texts - [\"Hello\", \"Goodbye\"].")));
                        }

                        if (texts.Count == 0)
                        {
                            throw new InvalidOperationException("Give the text to translate - or a list of texts.");
                        }

                        var options = new JsonObject
                        {
                            ["target_lang"] = Values.Get(p, "target") is { } target
                                ? Language.IsMatch(target) ? target.ToUpperInvariant() : throw new InvalidOperationException($"'{target}' is not a language's code - DE, EN-GB, PT-BR.")
                                : throw new InvalidOperationException("Give the language to translate into - DE, EN-GB."),
                        };

                        // DeepL reads a text's language without its variant - EN, never EN-GB.
                        if (Values.Get(p, "source") is { } source)
                        {
                            options["source_lang"] = Language.IsMatch(source) ? source.Split('-')[0].ToUpperInvariant() : throw new InvalidOperationException($"'{source}' is not a language's code - EN, DE.");
                        }

                        foreach (var (name, allowed) in new[]
                        {
                            ("formality", new[] { "more", "less", "prefer_more", "prefer_less", "default" }),
                            ("split_sentences", new[] { "0", "1", "nonewlines" }),
                            ("tag_handling", new[] { "html", "xml" }),
                            ("model", new[] { "quality_optimized", "prefer_quality_optimized", "latency_optimized" }),
                        })
                        {
                            if (Values.Get(p, name) is { } given)
                            {
                                options[name == "model" ? "model_type" : name] = allowed.Contains(given.ToLowerInvariant()) ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not {string.Join(", ", allowed[..^1])} or {allowed[^1]} ({name}).");
                            }
                        }

                        if (Values.Get(p, "preserve_formatting") is { } keep)
                        {
                            options["preserve_formatting"] = keep.ToLowerInvariant() is "true" or "false" ? keep.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{keep}' is not true or false (preserve_formatting).");
                        }

                        if (Values.Get(p, "context") is { } said)
                        {
                            options["context"] = said;
                        }

                        if (Values.Get(p, "glossary") is { } glossary)
                        {
                            options["glossary_id"] = GlossaryId.IsMatch(glossary) ? glossary : throw new InvalidOperationException($"'{glossary}' is not a glossary's id.");
                            if (!options.ContainsKey("source_lang"))
                            {
                                throw new InvalidOperationException("A glossary needs the language the text is in - give source.");
                            }
                        }

                        for (var from = 0; from < texts.Count; from += Batch)
                        {
                            var chunk = texts.Skip(from).Take(Batch).ToList();
                            var body = options.DeepClone().AsObject();
                            body["text"] = new JsonArray(chunk.Select(one => (JsonNode?)JsonValue.Create(one)).ToArray());
                            var answer = await session.SendAsync(HttpMethod.Post, "/translate", body, cancellationToken) as JsonObject;
                            var translations = (answer?["translations"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            if (translations.Count != chunk.Count)
                            {
                                throw new InvalidOperationException($"DeepL answered {translations.Count} translation{(translations.Count == 1 ? "" : "s")} for {chunk.Count} text{(chunk.Count == 1 ? "" : "s")}.");
                            }

                            for (var i = 0; i < chunk.Count; i++)
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["text"] = Text(translations[i]["text"]),
                                    ["detected_source_language"] = Text(translations[i]["detected_source_language"]),
                                    ["original"] = chunk[i],
                                });
                            }
                        }

                        yield break;
                    }

                case LanguagesOperation:
                    {
                        var type = Values.Get(p, "type")?.ToLowerInvariant() is { } which ? which is "target" or "source" ? which : throw new InvalidOperationException($"'{Values.Get(p, "type")}' is not target or source (type).") : "target";
                        foreach (var language in (await session.SendAsync(HttpMethod.Get, "/languages?type=" + type, null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["language"] = Text(language["language"]),
                                ["name"] = Text(language["name"]),
                                ["supports_formality"] = language["supports_formality"] is JsonValue formal && formal.TryGetValue<bool>(out var f) ? f : null,
                            });
                        }

                        yield break;
                    }

                case UsageOperation:
                    {
                        var usage = await session.SendAsync(HttpMethod.Get, "/usage", null, cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(usage.ToDictionary(pair => pair.Key, pair => (object?)(Number(pair.Value) is { } number ? number : pair.Value?.ToJsonString(Wire)), StringComparer.Ordinal));
                        yield break;
                    }

                default:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, "/glossaries", null, cancellationToken) as JsonObject;
                        foreach (var glossary in (answer?["glossaries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["glossary_id"] = Text(glossary["glossary_id"]),
                                ["name"] = Text(glossary["name"]),
                                ["ready"] = glossary["ready"] is JsonValue ready && ready.TryGetValue<bool>(out var r) ? r : null,
                                ["source_lang"] = Text(glossary["source_lang"]),
                                ["target_lang"] = Text(glossary["target_lang"]),
                                ["entry_count"] = Number(glossary["entry_count"]),
                                ["creation_time"] = Text(glossary["creation_time"]),
                            });
                        }

                        yield break;
                    }
            }
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: DeepL's API v2 - Free for a key ending in :fx, Pro otherwise, or the address given - the key in
        // DeepL's own header; a throttled call waiting as DeepL says; a failure thrown with its status and DeepL's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string key;

            private Session(string api, string key, bool free, HttpClient client)
            {
                this.api = api;
                this.key = key;
                Free = free;
                Client = client;
            }

            public HttpClient Client { get; }

            public bool Free { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The DeepL connection has no authentication key.");
                var free = key.EndsWith(":fx", StringComparison.Ordinal);
                var baseUrl = Values.BaseUrl(endpoint, free ? FreeEndpoint : ProEndpoint);
                return new Session(baseUrl.EndsWith("/v2", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/v2", key, free, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
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
                    // A page that is not DeepL's - a sign-in page, a proxy's - read as no translation would hide that it never was.
                    throw new InvalidOperationException($"DeepL answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 403 a key DeepL refuses - or a Free key on the Pro API; 456 the plan's characters spent.
                    var said = Text((json as JsonObject)?["message"]);
                    var detail = Text((json as JsonObject)?["detail"]);
                    var word = said is null ? null : detail is null ? said : said + " - " + detail;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(word is null ? (int)response.StatusCode == 456 ? ": the plan's characters are spent." : "." : ": " + (word.Length > 300 ? word[..300] : word))}");
                }

                return json;
            }
        }
    }
}
