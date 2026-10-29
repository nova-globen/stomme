#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Strapi
{
    /// <summary>
    /// Strapi (BL-229, n8n's Strapi node: a content type's entries created, got, read, updated and deleted): a Strapi's REST API
    /// (<c>/api</c>), version 5 or 4, an API token as a bearer token, over the host's egress. A test names how many entries of a
    /// content type the token sees, or that it answers.
    /// </summary>
    /// <remarks>
    /// <c>entries</c> (Strapi's filters as JSON, a sort, a status or publication state, a locale, what to populate and the
    /// fields - page by page up to <c>max</c>), <c>get_entry</c>, <c>create_entry</c>, <c>update_entry</c>, <c>delete_entry</c>;
    /// a single type read and written whole. An entry is its id (Strapi 5's document id too) and its fields - Strapi 4's
    /// attributes taken out of their wrapper - a relation or a media as its JSON. Every content type, id and filter held to its
    /// shape before anything is sent. A throttled call waits as Strapi says; a failure is thrown with its status and Strapi's
    /// word.
    /// </remarks>
    public sealed class StrapiConnector : IConnector, IConnectorExtractor
    {
        public const string EntriesOperation = "entries";

        public const string GetEntryOperation = "get_entry";

        public const string CreateEntryOperation = "create_entry";

        public const string UpdateEntryOperation = "update_entry";

        public const string DeleteEntryOperation = "delete_entry";

        public const string SingleOperation = "single";

        public const string UpdateSingleOperation = "update_single";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        // A content type's plural or singular API id - articles, blog-posts.
        private static readonly Regex ApiId = new("^[a-z][a-z0-9-]{0,99}$", RegexOptions.CultureInvariant);

        // A numeric id (Strapi 4) or a document id (Strapi 5).
        private static readonly Regex EntryId = new("^[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldList = new("^[A-Za-z0-9_.*]{1,100}(,[A-Za-z0-9_.*]{1,100})*$", RegexOptions.CultureInvariant);

        private static readonly Regex SortList = new("^[A-Za-z0-9_.]{1,100}(:(asc|desc))?(,[A-Za-z0-9_.]{1,100}(:(asc|desc))?)*$", RegexOptions.CultureInvariant);

        private static readonly Regex Locale = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { EntriesOperation, GetEntryOperation, CreateEntryOperation, UpdateEntryOperation, DeleteEntryOperation, SingleOperation, UpdateSingleOperation };

        public string ConnectorType => "strapi";

        public string DisplayName => "Strapi";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => "https://cms.example.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://strapi.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.strapi.io/cms/api/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("version", "Version", ConnectorConfigFieldType.Text, Required: false, Description: "5 (the default) or 4 - Strapi's major version.") { Default = "5" },
        });

        private static readonly ConnectorConfigField TypeField = new("content_type", "Content type", ConnectorConfigFieldType.Text, Required: true, Description: "Its plural API id - articles.");

        private static readonly ConnectorConfigField IdField = new("id", "Entry", ConnectorConfigFieldType.Text, Required: true, Description: "Its document id (Strapi 5) or its id (Strapi 4).");

        private static ConnectorConfigField DataField(bool required) => new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: required, Description: "A JSON object of fields - {\"title\": \"Hello\", \"author\": 3}.");

        private static ConnectorConfigField Read(string name, string label, string description) => new(name, label, ConnectorConfigFieldType.Text, Required: false, Description: description);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(EntriesOperation, "Get entries", "A content type's entries - n8n's Strapi Get Many.")
            {
                Parameters = Schema(
                    TypeField,
                    Read("filters", "Filters", "Strapi's filters as JSON - {\"title\": {\"$contains\": \"news\"}}."),
                    Read("sort", "Sort", "Comma-separated fields, :desc for descending - publishedAt:desc,title."),
                    Read("status", "Status", "published (the default) or draft - Strapi 4's live or preview."),
                    Read("locale", "Locale", "A locale's code - sv, en-GB."),
                    Read("populate", "Populate", "Comma-separated relations to fill in - author,cover - or * for all."),
                    Read("columns", "Fields", "Comma-separated fields to read - all when left empty."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(GetEntryOperation, "Get an entry", "n8n's Strapi Get.") { Parameters = Schema(TypeField, IdField, Read("status", "Status", "published or draft."), Read("locale", "Locale", "A locale's code."), Read("populate", "Populate", "Relations to fill in, or *.")) },
            new ConnectorOperation(CreateEntryOperation, "Create an entry", "n8n's Strapi Create.") { Parameters = Schema(TypeField, DataField(true), Read("status", "Status", "draft to keep it unpublished (Strapi 5)."), Read("locale", "Locale", "A locale's code.")) },
            new ConnectorOperation(UpdateEntryOperation, "Update an entry", "Changes the fields given - n8n's Strapi Update.") { Parameters = Schema(TypeField, IdField, DataField(true), Read("locale", "Locale", "A locale's code.")) },
            new ConnectorOperation(DeleteEntryOperation, "Delete an entry", "n8n's Strapi Delete.") { Parameters = Schema(TypeField, IdField, Read("locale", "Locale", "A locale's code.")) },
            new ConnectorOperation(SingleOperation, "Get a single type", "A single type's content - its singular API id, homepage.")
            {
                Parameters = Schema(new ConnectorConfigField("content_type", "Single type", ConnectorConfigFieldType.Text, Required: true, Description: "Its singular API id - homepage."), Read("locale", "Locale", "A locale's code."), Read("populate", "Populate", "Relations to fill in, or *.")),
            },
            new ConnectorOperation(UpdateSingleOperation, "Update a single type", "A single type's content changed.")
            {
                Parameters = Schema(new ConnectorConfigField("content_type", "Single type", ConnectorConfigFieldType.Text, Required: true, Description: "Its singular API id - homepage."), DataField(true), Read("locale", "Locale", "A locale's code.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);
                using var client = session.Client;

                // A token Strapi does not know is refused with 401; one it knows but that may not read the locales, or a Strapi
                // without them, answers 403 or 404 - the token answers all the same.
                var locales = await session.SendAsync(HttpMethod.Get, "/i18n/locales", null, cancellationToken, refusedIsAnswer: true) as JsonArray;
                return ConnectionTestResult.Ok($"Strapi {session.Version}: the token answers{(locales is { Count: > 0 } ? $" - {locales.Count} locale{(locales.Count == 1 ? "" : "s")}" : "")}.");
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
                ?? throw new InvalidOperationException($"Strapi has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Settings, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var type = Values.Get(p, "content_type") is { } named ? ApiId.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a content type's API id - lowercase letters, digits and -.") : throw new InvalidOperationException("Give the content type - its API id.");
            var id = operation is GetEntryOperation or UpdateEntryOperation or DeleteEntryOperation
                ? Values.Get(p, "id") is { } given ? EntryId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an entry's id - letters and digits.") : throw new InvalidOperationException("Give the entry - its id.")
                : null;

            var query = new List<string>();
            if (operation == EntriesOperation && Values.Get(p, "filters") is { } filters)
            {
                var parsed = Parse(filters) as JsonObject ?? throw new InvalidOperationException("The filters are not a JSON object - {\"title\": {\"$contains\": \"news\"}}.");
                Flatten("filters", parsed, query);
            }

            if (operation == EntriesOperation && Values.Get(p, "sort") is { } sort)
            {
                query.Add("sort=" + Uri.EscapeDataString(SortList.IsMatch(sort.Replace(" ", "", StringComparison.Ordinal)) ? sort.Replace(" ", "", StringComparison.Ordinal) : throw new InvalidOperationException($"'{sort}' is not a sort - publishedAt:desc,title.")));
            }

            if (operation is EntriesOperation or GetEntryOperation or CreateEntryOperation && Values.Get(p, "status") is { } status)
            {
                var wanted = status.ToLowerInvariant() is "published" or "draft" ? status.ToLowerInvariant() : throw new InvalidOperationException($"'{status}' is not published or draft (status).");
                query.Add(session.Version == 5 ? "status=" + wanted : "publicationState=" + (wanted == "draft" ? "preview" : "live"));
            }

            if (Values.Get(p, "locale") is { } locale)
            {
                query.Add("locale=" + (Locale.IsMatch(locale) ? locale : throw new InvalidOperationException($"'{locale}' is not a locale's code - sv, en-GB.")));
            }

            if (operation is EntriesOperation or GetEntryOperation or SingleOperation && Values.Get(p, "populate") is { } populate)
            {
                query.Add("populate=" + Uri.EscapeDataString(FieldList.IsMatch(populate.Replace(" ", "", StringComparison.Ordinal)) ? populate.Replace(" ", "", StringComparison.Ordinal) : throw new InvalidOperationException($"'{populate}' is not a list of relations - author,cover - or *.")));
            }

            if (operation == EntriesOperation && Values.Get(p, "columns") is { } columns)
            {
                var list = columns.Split(',', StringSplitOptions.TrimEntries);
                for (var i = 0; i < list.Length; i++)
                {
                    query.Add($"fields[{i}]=" + (FieldList.IsMatch(list[i]) && !list[i].Contains('*', StringComparison.Ordinal) ? list[i] : throw new InvalidOperationException($"'{columns}' is not a list of fields - title,slug.")));
                }
            }

            JsonObject? body = null;
            if (operation is CreateEntryOperation or UpdateEntryOperation or UpdateSingleOperation)
            {
                body = new JsonObject
                {
                    ["data"] = Parse(Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object.")) is JsonObject { Count: > 0 } fields
                        ? fields
                        : throw new InvalidOperationException("The fields are not a JSON object with a field or more - {\"title\": \"Hello\"}."),
                };
            }

            using var client = session.Client;
            var tail = query.Count == 0 ? "" : "?" + string.Join("&", query);
            switch (operation)
            {
                case EntriesOperation:
                    {
                        // Strapi pages by page number, every page the same size: the size is fixed at the start.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, $"/{type}?" + string.Join("&", query.Append($"pagination[page]={page}").Append($"pagination[pageSize]={size}")), null, cancellationToken);
                            foreach (var item in (answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (answer?["meta"]?["pagination"]?["pageCount"] is not JsonValue count || !count.TryGetValue<long>(out var pages) || page >= pages)
                            {
                                yield break;
                            }
                        }
                    }

                case GetEntryOperation:
                case SingleOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/{type}{(id is null ? "" : "/" + id)}{tail}", null, cancellationToken);
                        yield return Record(answer?["data"] as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case CreateEntryOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Post, $"/{type}{tail}", body, cancellationToken))?["data"] as JsonObject ?? new JsonObject());
                    yield break;
                case UpdateEntryOperation:
                case UpdateSingleOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Put, $"/{type}{(id is null ? "" : "/" + id)}{tail}", body, cancellationToken))?["data"] as JsonObject ?? new JsonObject());
                    yield break;
                default:
                    await session.SendAsync(HttpMethod.Delete, $"/{type}/{id}{tail}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["content_type"] = type, ["id"] = id, ["deleted"] = true });
                    yield break;
            }
        }

        // Strapi's filters as its query string reads them: filters[title][$contains]=news, a list's items by their index - each key
        // held to letters, digits, _, . and a leading $, so nothing in it needs escaping.
        private static void Flatten(string prefix, JsonNode? node, List<string> query)
        {
            switch (node)
            {
                case JsonObject map:
                    foreach (var (key, value) in map)
                    {
                        if (!Regex.IsMatch(key, "^\\$?[A-Za-z0-9_.]{1,100}$", RegexOptions.CultureInvariant))
                        {
                            throw new InvalidOperationException($"'{key}' is not a field or an operator ($eq, $contains) in the filters.");
                        }

                        Flatten($"{prefix}[{key}]", value, query);
                    }

                    break;
                case JsonArray list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        Flatten($"{prefix}[{i}]", list[i], query);
                    }

                    break;
                default:
                    query.Add(prefix + "=" + Uri.EscapeDataString(node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "null"));
                    break;
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

        // An entry: its id, then its fields - Strapi 4's taken out of their attributes - a relation or a media as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item["attributes"] is JsonObject attributes ? attributes.Prepend(new KeyValuePair<string, JsonNode?>("id", item["id"])) : (IEnumerable<KeyValuePair<string, JsonNode?>>)item)
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the Strapi's REST API, the token as a bearer token; a throttled call waiting as Strapi says; a
        // failure thrown with its status and Strapi's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string token;

            private Session(string api, string token, int version, HttpClient client)
            {
                this.api = api;
                this.token = token;
                Version = version;
                Client = client;
            }

            public HttpClient Client { get; }

            public int Version { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, IConnectorEgress egress, TimeSpan timeout)
            {
                // The Strapi's address - its admin's pasted from the browser too, /admin... cut off - and its API under /api.
                if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
                {
                    throw new InvalidOperationException($"'{endpoint}' is not a Strapi's address - https://cms.example.com.");
                }

                var path = address.AbsolutePath;
                foreach (var marker in new[] { "/admin", "/api" })
                {
                    var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        path = path[..at];
                    }
                }

                var version = Values.Get(settings, "version") switch
                {
                    null or "5" or "v5" => 5,
                    "4" or "v4" => 4,
                    var other => throw new InvalidOperationException($"'{other}' is not a Strapi version this connector speaks - 5 or 4."),
                };
                var token = Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Strapi connection has no API token.");
                return new Session(address.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/') + "/api", token, version, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, bool refusedIsAnswer = false)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                    // A page that is not Strapi's - a sign-in page, a proxy's - read as no entries would hide that it never was.
                    throw new InvalidOperationException($"Strapi answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (refusedIsAnswer && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = Text(json?["error"]?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
