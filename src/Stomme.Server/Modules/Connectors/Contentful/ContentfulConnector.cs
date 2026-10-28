#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Contentful
{
    /// <summary>
    /// Contentful (BL-229, n8n's Contentful node: a space's entries, assets, content types and locales read): Contentful's Content
    /// Delivery API, or its Preview API for drafts, with an access token as a bearer token, over the host's egress. A test names
    /// the space and how many locales its environment has.
    /// </summary>
    /// <remarks>
    /// <c>entries</c> and <c>assets</c> (Contentful's search parameters as JSON, the fields to select, an order, a full-text
    /// query, a locale - page by page up to <c>max</c>), <c>entry</c>, <c>asset</c>, <c>content_types</c>,
    /// <c>content_type</c>, <c>locales</c> and <c>space</c>. An entry or an asset is its id, its content type, its dates and
    /// locale, then its fields - a field of the same name wins - a link, a rich text or a list as its JSON; raw, its sys, fields
    /// and metadata as their JSON. Every id, filter, order and locale held to its shape before anything is sent. A throttled
    /// call waits as Contentful says; a failure is thrown with its status, Contentful's error id and its word.
    /// </remarks>
    public sealed class ContentfulConnector : IConnector, IConnectorExtractor
    {
        public const string EntriesOperation = "entries";

        public const string EntryOperation = "entry";

        public const string AssetsOperation = "assets";

        public const string AssetOperation = "asset";

        public const string ContentTypesOperation = "content_types";

        public const string ContentTypeOperation = "content_type";

        public const string LocalesOperation = "locales";

        public const string SpaceOperation = "space";

        public const string DeliveryEndpoint = "https://cdn.contentful.com";

        public const string PreviewEndpoint = "https://preview.contentful.com";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex SpaceId = new("^[a-z0-9]{1,64}$", RegexOptions.CultureInvariant);

        // An entry's, an asset's, a content type's or an environment's id.
        private static readonly Regex Id = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);

        // A search parameter: a sys, fields or metadata path with an operator - fields.tags[in] - or a link or MIME type search.
        private static readonly Regex FilterKey = new("^((sys|fields|metadata)(\\.[A-Za-z0-9_]{1,64}){1,6}(\\[[a-z]{2,10}\\])?|links_to_entry|links_to_asset|mimetype_group)$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldList = new("^[A-Za-z0-9_.]{1,200}(,[A-Za-z0-9_.]{1,200})*$", RegexOptions.CultureInvariant);

        private static readonly Regex OrderList = new("^-?[A-Za-z0-9_.]{1,200}(,-?[A-Za-z0-9_.]{1,200})*$", RegexOptions.CultureInvariant);

        private static readonly Regex Locale = new("^(\\*|[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { EntriesOperation, EntryOperation, AssetsOperation, AssetOperation, ContentTypesOperation, ContentTypeOperation, LocalesOperation, SpaceOperation };

        public string ConnectorType => "contentful";

        public string DisplayName => "Contentful";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => DeliveryEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.contentful.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.contentful.com/developers/docs/references/content-delivery-api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("space", "Space", ConnectorConfigFieldType.Text, Required: true, Description: "The space's id - Settings > General settings."),
            new ConnectorConfigField("environment", "Environment", ConnectorConfigFieldType.Text, Required: false, Description: "The environment's id - master unless said.") { Default = "master" },
            new ConnectorConfigField("api", "API", ConnectorConfigFieldType.Select, Required: false, Description: "delivery: published content, with a Content Delivery API token - the default. preview: drafts too, with a Content Preview API token.")
            {
                Default = "delivery",
                Options = new[] { new ConnectorConfigFieldOption("delivery"), new ConnectorConfigFieldOption("preview") },
            },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField EnvironmentField = Field("environment", "Environment", "The environment's id - the connection's when left empty.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField LocaleField = Field("locale", "Locale", "A locale's code - sv-SE - or * for every locale's.");

        private static readonly ConnectorConfigField RawField = Field("raw", "Raw", "true: its sys, fields and metadata as Contentful gives them.", ConnectorConfigFieldType.Boolean);

        private static ConnectorConfigField[] Search(bool entries) => new[]
        {
            Field("filters", "Filters", "Contentful's search parameters as JSON - {\"fields.title\": \"Hello\", \"fields.tags[in]\": \"a,b\"}" + (entries ? " - a field's needs the content type." : "."), ConnectorConfigFieldType.Multiline),
            Field("select", "Fields", "Comma-separated paths to read - sys.id,fields.title."),
            Field("order", "Order", "Comma-separated paths, - for descending - -sys.createdAt."),
            Field("query", "Search", "Full-text search across the text fields."),
            LocaleField,
            RawField,
            MaxField,
            EnvironmentField,
        };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(EntriesOperation, "Get entries", "A space's entries - n8n's Contentful Entry Get Many.")
            {
                Parameters = Schema(new[] { Field("content_type", "Content type", "The content type's id - blogPost.") }.Concat(Search(true)).ToArray()),
            },
            new ConnectorOperation(EntryOperation, "Get an entry", "n8n's Contentful Entry Get.") { Parameters = Schema(Field("id", "Entry", "Its id.", required: true), LocaleField, RawField, EnvironmentField) },
            new ConnectorOperation(AssetsOperation, "Get assets", "A space's assets - n8n's Contentful Asset Get Many.") { Parameters = Schema(Search(false)) },
            new ConnectorOperation(AssetOperation, "Get an asset", "n8n's Contentful Asset Get.") { Parameters = Schema(Field("id", "Asset", "Its id.", required: true), LocaleField, RawField, EnvironmentField) },
            new ConnectorOperation(ContentTypesOperation, "Get content types", "A space's content types.") { Parameters = Schema(MaxField, EnvironmentField) },
            new ConnectorOperation(ContentTypeOperation, "Get a content type", "n8n's Contentful Content Type Get.") { Parameters = Schema(Field("id", "Content type", "Its id - blogPost.", required: true), EnvironmentField) },
            new ConnectorOperation(LocalesOperation, "Get locales", "An environment's locales - n8n's Contentful Locale Get Many.") { Parameters = Schema(MaxField, EnvironmentField) },
            new ConnectorOperation(SpaceOperation, "Get the space", "n8n's Contentful Space Get.") { Parameters = Schema() },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
                using var client = session.Client;
                var space = await session.SendAsync($"/spaces/{session.Space}", cancellationToken);
                var locales = (await session.SendAsync(session.In(session.Environment, "/locales"), cancellationToken))?["items"] as JsonArray;
                var count = locales?.Count ?? 0;
                return ConnectionTestResult.Ok($"Contentful's {session.Api} API: the space '{Text(space?["name"]) ?? session.Space}' answers - {count} locale{(count == 1 ? "" : "s")} in {session.Environment}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? EntriesOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Contentful has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var environment = Values.Get(p, "environment") is { } named ? Id.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not an environment's id.") : session.Environment;
            var id = operation is EntryOperation or AssetOperation or ContentTypeOperation
                ? Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id - letters, digits, _, - and .") : throw new InvalidOperationException("Give the id.")
                : null;
            var raw = Values.GetBool(p, "raw", false);

            var query = new List<string>();
            if (operation == EntriesOperation && Values.Get(p, "content_type") is { } type)
            {
                query.Add("content_type=" + (Id.IsMatch(type) ? type : throw new InvalidOperationException($"'{type}' is not a content type's id - blogPost.")));
            }

            if (operation is EntriesOperation or AssetsOperation)
            {
                if (Values.Get(p, "filters") is { } filters)
                {
                    foreach (var (key, value) in Parse(filters) as JsonObject ?? throw new InvalidOperationException("The filters are not a JSON object - {\"fields.title\": \"Hello\"}."))
                    {
                        var text = value switch
                        {
                            JsonValue scalar when scalar.TryGetValue<string>(out var s) => s,
                            JsonValue scalar => scalar.ToJsonString(),
                            JsonArray list when list.All(item => item is JsonValue) => string.Join(",", list.Select(item => item is JsonValue listed && listed.TryGetValue<string>(out var word) ? word : item!.ToJsonString())),
                            _ => throw new InvalidOperationException($"'{key}' in the filters is not a text, a number, a flag or a list of them."),
                        };
                        query.Add((FilterKey.IsMatch(key) ? key : throw new InvalidOperationException($"'{key}' is not a search parameter - fields.title, sys.id[in], metadata.tags.sys.id[all].")) + "=" + Uri.EscapeDataString(text));
                    }
                }

                if (Values.Get(p, "select") is { } select)
                {
                    var clean = select.Replace(" ", "", StringComparison.Ordinal);
                    query.Add("select=" + (FieldList.IsMatch(clean) ? clean : throw new InvalidOperationException($"'{select}' is not a list of fields - sys.id,fields.title.")));
                }

                if (Values.Get(p, "order") is { } order)
                {
                    var clean = order.Replace(" ", "", StringComparison.Ordinal);
                    query.Add("order=" + (OrderList.IsMatch(clean) ? clean : throw new InvalidOperationException($"'{order}' is not an order - -sys.createdAt,fields.title.")));
                }

                if (Values.Get(p, "query") is { } search)
                {
                    query.Add("query=" + Uri.EscapeDataString(search));
                }
            }

            if (operation is EntriesOperation or EntryOperation or AssetsOperation or AssetOperation && Values.Get(p, "locale") is { } locale)
            {
                query.Add("locale=" + (Locale.IsMatch(locale) ? Uri.EscapeDataString(locale) : throw new InvalidOperationException($"'{locale}' is not a locale's code - sv-SE - or *.")));
            }

            using var client = session.Client;
            switch (operation)
            {
                case SpaceOperation:
                    {
                        var space = await session.SendAsync($"/spaces/{session.Space}", cancellationToken) as JsonObject ?? new JsonObject();
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(space["sys"]?["id"]), ["name"] = Text(space["name"]), ["locales"] = space["locales"]?.ToJsonString(Wire) });
                        yield break;
                    }

                case EntryOperation:
                case AssetOperation:
                case ContentTypeOperation:
                    {
                        var path = operation switch { EntryOperation => "/entries/", AssetOperation => "/assets/", _ => "/content_types/" } + id;
                        var item = await session.SendAsync(session.In(environment, path) + (query.Count == 0 ? "" : "?" + string.Join("&", query)), cancellationToken) as JsonObject ?? new JsonObject();
                        yield return operation == ContentTypeOperation ? TypeRecord(item) : Record(item, raw);
                        yield break;
                    }

                default:
                    {
                        // Contentful pages by skip and limit: the limit is fixed at the start, Contentful's total the end.
                        var path = operation switch { EntriesOperation => "/entries", AssetsOperation => "/assets", ContentTypesOperation => "/content_types", _ => "/locales" };
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        while (true)
                        {
                            var answer = await session.SendAsync(session.In(environment, path) + "?" + string.Join("&", query.Append($"skip={read}").Append($"limit={size}")), cancellationToken);
                            var items = (answer?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return operation switch { ContentTypesOperation => TypeRecord(item), LocalesOperation => LocaleRecord(item), _ => Record(item, raw) };
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (items.Count == 0 || answer?["total"] is not JsonValue total || !total.TryGetValue<long>(out var all) || read >= all)
                            {
                                yield break;
                            }
                        }
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

        // An entry or an asset: its id, its content type, its dates and locale, then its fields - a field of the same name wins;
        // raw, its sys, fields and metadata as their JSON.
        private static ConnectorRecord Record(JsonObject item, bool raw)
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (raw)
            {
                foreach (var (name, value) in item)
                {
                    record[name] = value?.ToJsonString(Wire);
                }

                return new ConnectorRecord(record);
            }

            var sys = item["sys"];
            record["id"] = Text(sys?["id"]);
            if (Text(sys?["contentType"]?["sys"]?["id"]) is { } type)
            {
                record["content_type"] = type;
            }

            record["created_at"] = Text(sys?["createdAt"]);
            record["updated_at"] = Text(sys?["updatedAt"]);
            if (Text(sys?["locale"]) is { } locale)
            {
                record["locale"] = locale;
            }

            foreach (var (name, value) in item["fields"] as JsonObject ?? new JsonObject())
            {
                record[name] = Value(value);
            }

            return new ConnectorRecord(record);
        }

        private static ConnectorRecord TypeRecord(JsonObject item) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(item["sys"]?["id"]),
            ["name"] = Text(item["name"]),
            ["description"] = Text(item["description"]),
            ["display_field"] = Text(item["displayField"]),
            ["fields"] = item["fields"]?.ToJsonString(Wire),
        });

        private static ConnectorRecord LocaleRecord(JsonObject item) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = Text(item["code"]),
            ["name"] = Text(item["name"]),
            ["default"] = item["default"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b,
            ["fallback_code"] = Text(item["fallbackCode"]),
        });

        private static object? Value(JsonNode? value) => value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            _ => value.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Contentful's Delivery or Preview API, the token as a bearer token; a throttled call waiting as
        // Contentful says; a failure thrown with its status, Contentful's error id and its word.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, string space, string environment, string api, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Space = space;
                Environment = environment;
                Api = api;
                Client = client;
            }

            public HttpClient Client { get; }

            public string Space { get; }

            public string Environment { get; }

            public string Api { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.Get(settings, "api")?.ToLowerInvariant() switch
                {
                    null or "delivery" => "delivery",
                    "preview" => "preview",
                    var other => throw new InvalidOperationException($"'{other}' is not one of Contentful's APIs this connector reads - delivery or preview."),
                };
                var space = Values.Get(settings, "space") is { } named ? SpaceId.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a space's id - lowercase letters and digits.") : throw new InvalidOperationException("The Contentful connection has no space.");
                var environment = Values.Get(settings, "environment") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an environment's id.") : "master";
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Contentful connection has no access token.");
                return new Session(Values.BaseUrl(endpoint, api == "preview" ? PreviewEndpoint : DeliveryEndpoint), token, space, environment, api, egress.CreateHttpClient(timeout));
            }

            public string In(string environment, string path) => $"/spaces/{Space}/environments/{environment}{path}";

            public async Task<JsonNode?> SendAsync(string path, CancellationToken cancellationToken)
            {
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                    // A page that is not Contentful's - a sign-in page, a proxy's - read as no entries would hide that it never was.
                    throw new InvalidOperationException($"Contentful answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = Text(json?["sys"]?["id"]);
                    var said = Text(json?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(error is null ? "" : $" ({error})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
