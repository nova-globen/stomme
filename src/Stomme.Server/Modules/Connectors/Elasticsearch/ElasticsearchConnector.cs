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

namespace Stomme.Modules.Connectors.Elasticsearch
{
    /// <summary>
    /// Elasticsearch (BL-229, n8n's Elasticsearch node: an index's documents searched, got, created, updated and deleted, its
    /// indices kept): an Elasticsearch's - or an OpenSearch's - REST API with a user's name and password, or an API key in the
    /// password's place, over the host's egress. A test names the cluster and its version.
    /// </summary>
    /// <remarks>
    /// <c>search</c> (a search body as JSON, a sort, the source fields to include or exclude, a routing - page by page by from
    /// and size up to <c>max</c>, within Elasticsearch's window of 10,000), <c>count</c>, <c>get</c>, <c>create</c> (with its id
    /// or Elasticsearch's), <c>update</c> (the fields given), <c>delete</c>; <c>indices</c>, <c>index</c>,
    /// <c>create_index</c> (its settings, mappings and aliases) and <c>delete_index</c>. A document is its id and index, then
    /// its source's fields - an object or a list as its JSON; raw, the hit as Elasticsearch gives it. Every index, id, sort,
    /// routing and pipeline held to its shape before anything is sent. A throttled call waits as Elasticsearch says; a
    /// failure is thrown with its status, Elasticsearch's error type and its reason.
    /// </remarks>
    public sealed class ElasticsearchConnector : IConnector, IConnectorExtractor
    {
        public const string SearchOperation = "search";

        public const string CountOperation = "count";

        public const string GetOperation = "get";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        public const string DeleteOperation = "delete";

        public const string IndicesOperation = "indices";

        public const string IndexOperation = "index";

        public const string CreateIndexOperation = "create_index";

        public const string DeleteIndexOperation = "delete_index";

        public const int DefaultMax = 1000;

        // Elasticsearch's own index.max_result_window: from and size page no further by default.
        public const int Window = 10000;

        private const int PageSize = 100;

        // An index, an alias or a data stream: lowercase, never a leading -, _ or +.
        private static readonly Regex IndexName = new("^[a-z0-9][a-z0-9._-]{0,254}$", RegexOptions.CultureInvariant);

        // What a search or a count may target: indices, patterns and lists of them.
        private static readonly Regex Target = new("^[a-z0-9*][a-z0-9._*-]{0,254}(,[a-z0-9*][a-z0-9._*-]{0,254})*$", RegexOptions.CultureInvariant);

        private static readonly Regex SortList = new("^[A-Za-z0-9_.@]{1,200}(:(asc|desc))?(,[A-Za-z0-9_.@]{1,200}(:(asc|desc))?)*$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldList = new("^[A-Za-z0-9_.*@]{1,200}(,[A-Za-z0-9_.*@]{1,200})*$", RegexOptions.CultureInvariant);

        private static readonly Regex Pipeline = new("^[A-Za-z0-9._-]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { SearchOperation, CountOperation, GetOperation, CreateOperation, UpdateOperation, DeleteOperation, IndicesOperation, IndexOperation, CreateIndexOperation, DeleteIndexOperation };

        public string ConnectorType => "elasticsearch";

        public string DisplayName => "Elasticsearch";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://search.example.com:9200";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.elastic.co/elasticsearch");

        public Uri ApiDocumentationUrl { get; } = new("https://www.elastic.co/docs/api/doc/elasticsearch/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField IndexField = Field("index", "Index", "The index - an alias or a data stream too.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Document", "The document's id.", required: true);

        private static readonly ConnectorConfigField FieldsField = Field("fields", "Fields", "A JSON object of fields - {\"title\": \"Hello\", \"views\": 3}.", ConnectorConfigFieldType.Multiline, required: true);

        private static readonly ConnectorConfigField RoutingField = Field("routing", "Routing", "The shard routing value, when the index uses one.");

        private static readonly ConnectorConfigField RefreshField = Field("refresh", "Refresh", "true, wait_for or false (the default) - when the change shows in a search.");

        private static readonly ConnectorConfigField RawField = Field("raw", "Raw", "true: each hit as Elasticsearch gives it.", ConnectorConfigFieldType.Boolean);

        private static readonly ConnectorConfigField IncludesField = Field("includes", "Source fields", "Comma-separated source fields to read - title,author.*.");

        private static readonly ConnectorConfigField ExcludesField = Field("excludes", "Leave out", "Comma-separated source fields to leave out.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search documents", "An index's documents found by a search - n8n's Elasticsearch Document Get Many.")
            {
                Parameters = Schema(
                    Field("index", "Index", "The index, a pattern - logs-* - or a comma-separated list.", required: true),
                    Field("query", "Search", "A search body as JSON - {\"query\": {\"match\": {\"title\": \"news\"}}} - every document when left empty.", ConnectorConfigFieldType.Multiline),
                    Field("sort", "Sort", "Comma-separated fields, :desc for descending - @timestamp:desc,title."),
                    IncludesField,
                    ExcludesField,
                    RoutingField,
                    RawField,
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most - 10,000 at the most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(CountOperation, "Count documents", "How many documents a search finds.")
            {
                Parameters = Schema(Field("index", "Index", "The index, a pattern or a list.", required: true), Field("query", "Search", "A search body as JSON - every document when left empty.", ConnectorConfigFieldType.Multiline), RoutingField),
            },
            new ConnectorOperation(GetOperation, "Get a document", "n8n's Elasticsearch Document Get.") { Parameters = Schema(IndexField, IdField, IncludesField, ExcludesField, RoutingField, RawField) },
            new ConnectorOperation(CreateOperation, "Create a document", "n8n's Elasticsearch Document Create.")
            {
                Parameters = Schema(IndexField, FieldsField, Field("id", "Document", "Its id - Elasticsearch's own when left empty."), RoutingField, Field("pipeline", "Pipeline", "An ingest pipeline to run it through."), RefreshField),
            },
            new ConnectorOperation(UpdateOperation, "Update a document", "Changes the fields given - n8n's Elasticsearch Document Update.") { Parameters = Schema(IndexField, IdField, FieldsField, RoutingField, RefreshField) },
            new ConnectorOperation(DeleteOperation, "Delete a document", "n8n's Elasticsearch Document Delete.") { Parameters = Schema(IndexField, IdField, RoutingField, RefreshField) },
            new ConnectorOperation(IndicesOperation, "Get indices", "The cluster's indices, their health and size - n8n's Elasticsearch Index Get Many.")
            {
                Parameters = Schema(new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(IndexOperation, "Get an index", "Its settings, mappings and aliases - n8n's Elasticsearch Index Get.") { Parameters = Schema(IndexField) },
            new ConnectorOperation(CreateIndexOperation, "Create an index", "n8n's Elasticsearch Index Create.")
            {
                Parameters = Schema(
                    IndexField,
                    Field("settings", "Settings", "Its settings as JSON - {\"number_of_shards\": 1}.", ConnectorConfigFieldType.Multiline),
                    Field("mappings", "Mappings", "Its mappings as JSON - {\"properties\": {\"title\": {\"type\": \"text\"}}}.", ConnectorConfigFieldType.Multiline),
                    Field("aliases", "Aliases", "Its aliases as JSON - {\"posts\": {}}.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(DeleteIndexOperation, "Delete an index", "n8n's Elasticsearch Index Delete.") { Parameters = Schema(IndexField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var root = await session.SendAsync(HttpMethod.Get, "/", null, cancellationToken) as JsonObject;
                var product = Text(root?["version"]?["distribution"]) == "opensearch" ? "OpenSearch" : "Elasticsearch";
                var version = Text(root?["version"]?["number"]);
                return ConnectionTestResult.Ok($"{product}{(version is null ? "" : " " + version)}: the cluster '{Text(root?["cluster_name"]) ?? "?"}' answers.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SearchOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Elasticsearch has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most
                ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? operation == SearchOperation && n > Window ? throw new InvalidOperationException($"'{most}' is more than Elasticsearch pages through by from and size - 10,000 at the most (max).") : n
                    : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).")
                : DefaultMax;
            string? index = null;
            if (operation != IndicesOperation)
            {
                var named = Values.Get(p, "index") ?? throw new InvalidOperationException("Give the index.");
                index = (operation is SearchOperation or CountOperation ? Target : IndexName).IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not an index's name - lowercase letters, digits, ., _ and -{(operation is SearchOperation or CountOperation ? ", * and a comma between several" : "")}.");
            }

            string? id = null;
            if (operation is GetOperation or UpdateOperation or DeleteOperation || (operation == CreateOperation && Values.Get(p, "id") is not null))
            {
                id = Values.Get(p, "id") is { } given ? given.Length <= 512 ? given : throw new InvalidOperationException("The document's id is longer than Elasticsearch's 512.") : throw new InvalidOperationException("Give the document's id.");
            }

            var query = new List<string>();
            // An index's own calls take no query - neither a routing nor a refresh reaches them.
            if (Values.Get(p, "routing") is { } routing)
            {
                query.Add("routing=" + Uri.EscapeDataString(routing));
            }

            if (operation is SearchOperation or GetOperation)
            {
                foreach (var (name, key) in new[] { ("includes", "_source_includes"), ("excludes", "_source_excludes") })
                {
                    if (Values.Get(p, name) is { } fields)
                    {
                        var clean = fields.Replace(" ", "", StringComparison.Ordinal);
                        query.Add(key + "=" + (FieldList.IsMatch(clean) ? clean : throw new InvalidOperationException($"'{fields}' is not a list of fields - title,author.* ({name}).")));
                    }
                }
            }

            if (operation == SearchOperation && Values.Get(p, "sort") is { } sort)
            {
                var clean = sort.Replace(" ", "", StringComparison.Ordinal);
                query.Add("sort=" + Uri.EscapeDataString(SortList.IsMatch(clean) ? clean : throw new InvalidOperationException($"'{sort}' is not a sort - @timestamp:desc,title.")));
            }

            if (operation == CreateOperation && Values.Get(p, "pipeline") is { } pipeline)
            {
                query.Add("pipeline=" + (Pipeline.IsMatch(pipeline) ? pipeline : throw new InvalidOperationException($"'{pipeline}' is not a pipeline's id.")));
            }

            if (operation is CreateOperation or UpdateOperation or DeleteOperation && Values.Get(p, "refresh") is { } refresh)
            {
                query.Add("refresh=" + (refresh.ToLowerInvariant() is "true" or "false" or "wait_for" ? refresh.ToLowerInvariant() : throw new InvalidOperationException($"'{refresh}' is not true, wait_for or false (refresh).")));
            }

            JsonObject? body = null;
            if (operation is SearchOperation or CountOperation && Values.Get(p, "query") is { } search)
            {
                body = Parse(search) as JsonObject ?? throw new InvalidOperationException("The search is not a JSON object - {\"query\": {\"match\": {\"title\": \"news\"}}}.");
                if (operation == SearchOperation && (body.ContainsKey("from") || body.ContainsKey("size") || body.ContainsKey("search_after")))
                {
                    throw new InvalidOperationException("The search pages itself - leave from, size and search_after out of it, and give max.");
                }
            }

            if (operation is CreateOperation or UpdateOperation)
            {
                var fields = Parse(Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object.")) as JsonObject;
                body = fields is { Count: > 0 } ? operation == UpdateOperation ? new JsonObject { ["doc"] = fields } : fields : throw new InvalidOperationException("The fields are not a JSON object with a field or more - {\"title\": \"Hello\"}.");
            }

            if (operation == CreateIndexOperation)
            {
                body = new JsonObject();
                foreach (var part in new[] { "settings", "mappings", "aliases" })
                {
                    if (Values.Get(p, part) is { } json)
                    {
                        body[part] = Parse(json) as JsonObject ?? throw new InvalidOperationException($"The {part} are not a JSON object.");
                    }
                }
            }

            var raw = Values.GetBool(p, "raw", false);
            var tail = query.Count == 0 ? "" : "?" + string.Join("&", query);
            var escapedId = id is null ? null : Uri.EscapeDataString(id);
            using var client = session.Client;
            switch (operation)
            {
                case SearchOperation:
                    {
                        // Elasticsearch pages by from and size: the size is fixed at the start, a short page or its exact total
                        // the end - and max, never above the window, keeps from and size inside it.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        while (true)
                        {
                            var page = body?.DeepClone().AsObject() ?? new JsonObject();
                            page["from"] = read;
                            page["size"] = size;
                            var answer = await session.SendAsync(HttpMethod.Post, $"/{index}/_search{tail}", page, cancellationToken);
                            var hits = (answer?["hits"]?["hits"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var hit in hits)
                            {
                                yield return Document(hit, raw);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (hits.Count < size || (Total(answer) is { } total && read >= total))
                            {
                                yield break;
                            }
                        }
                    }

                case CountOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Post, $"/{index}/_count{tail}", body ?? new JsonObject(), cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = index, ["count"] = answer?["count"] is JsonValue count && count.TryGetValue<long>(out var c) ? c : 0L });
                        yield break;
                    }

                case GetOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/{index}/_doc/{escapedId}{tail}", null, cancellationToken, missing: $"Elasticsearch has no document '{id}' in '{index}'.") as JsonObject ?? new JsonObject();
                        yield return Document(answer, raw);
                        yield break;
                    }

                case CreateOperation:
                case UpdateOperation:
                case DeleteOperation:
                    {
                        var (method, path) = operation switch
                        {
                            CreateOperation => escapedId is null ? (HttpMethod.Post, $"/{index}/_doc") : (HttpMethod.Put, $"/{index}/_doc/{escapedId}"),
                            UpdateOperation => (HttpMethod.Post, $"/{index}/_update/{escapedId}"),
                            _ => (HttpMethod.Delete, $"/{index}/_doc/{escapedId}"),
                        };
                        var answer = await session.SendAsync(method, path + tail, body, cancellationToken, missing: operation == CreateOperation ? null : $"Elasticsearch has no document '{id}' in '{index}'.");
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["_id"] = Text(answer?["_id"]),
                            ["_index"] = Text(answer?["_index"]),
                            ["_version"] = answer?["_version"] is JsonValue version && version.TryGetValue<long>(out var v) ? v : null,
                            ["result"] = Text(answer?["result"]),
                        });
                        yield break;
                    }

                case IndicesOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, "/_cat/indices?format=json&bytes=b&s=index", null, cancellationToken) as JsonArray ?? new JsonArray();
                        foreach (var item in answer.OfType<JsonObject>().Take(max))
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["index"] = Text(item["index"]),
                                ["health"] = Text(item["health"]),
                                ["status"] = Text(item["status"]),
                                ["documents"] = Number(item["docs.count"]),
                                ["deleted"] = Number(item["docs.deleted"]),
                                ["bytes"] = Number(item["store.size"]),
                                ["primaries"] = Number(item["pri"]),
                                ["replicas"] = Number(item["rep"]),
                            });
                        }

                        yield break;
                    }

                case IndexOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Get, $"/{index}", null, cancellationToken) as JsonObject ?? new JsonObject();
                        foreach (var (name, value) in answer)
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["index"] = name,
                                ["aliases"] = value?["aliases"]?.ToJsonString(Wire),
                                ["mappings"] = value?["mappings"]?.ToJsonString(Wire),
                                ["settings"] = value?["settings"]?.ToJsonString(Wire),
                            });
                        }

                        yield break;
                    }

                case CreateIndexOperation:
                    {
                        var answer = await session.SendAsync(HttpMethod.Put, $"/{index}", body, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = index, ["acknowledged"] = answer?["acknowledged"] is JsonValue ack && ack.TryGetValue<bool>(out var a) && a });
                        yield break;
                    }

                default:
                    {
                        var answer = await session.SendAsync(HttpMethod.Delete, $"/{index}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = index, ["acknowledged"] = answer?["acknowledged"] is JsonValue ack && ack.TryGetValue<bool>(out var a) && a });
                        yield break;
                    }
            }
        }

        private static long? Total(JsonNode? answer) => answer?["hits"]?["total"] switch
        {
            JsonValue plain when plain.TryGetValue<long>(out var count) => count,
            JsonObject counted when counted["relation"] is JsonValue relation && relation.TryGetValue<string>(out var word) && word == "eq" && counted["value"] is JsonValue value && value.TryGetValue<long>(out var count) => count,
            _ => null,
        };

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

        // A document: its id and index, then its source's fields; raw, the hit as Elasticsearch gives it.
        private static ConnectorRecord Document(JsonObject hit, bool raw)
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (raw)
            {
                foreach (var (name, value) in hit)
                {
                    record[name] = Value(value);
                }

                return new ConnectorRecord(record);
            }

            record["_id"] = Text(hit["_id"]);
            record["_index"] = Text(hit["_index"]);
            foreach (var (name, value) in hit["_source"] as JsonObject ?? new JsonObject())
            {
                record[name] = Value(value);
            }

            return new ConnectorRecord(record);
        }

        private static object? Value(JsonNode? value) => value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            _ => value.ToJsonString(Wire),
        };

        // _cat's numbers are text.
        private static long? Number(JsonNode? node) => Text(node) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : node is JsonValue value && value.TryGetValue<long>(out var plain) ? plain : null;

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the cluster's REST API with a user's name and password, or an API key; a throttled call
        // waiting as Elasticsearch says; a failure thrown with its status, Elasticsearch's error type and its reason.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly AuthenticationHeaderValue authorization;

            private Session(string baseUrl, AuthenticationHeaderValue authorization, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.authorization = authorization;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the cluster's address - https://search.example.com:9200.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                var password = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Elasticsearch connection has no password - or API key.");

                // No user: the password is an API key, its encoded form as Elasticsearch gives it.
                var authorization = Values.Get(credentials, "username") is { } user
                    ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)))
                    : new AuthenticationHeaderValue("ApiKey", password);
                return new Session(baseUrl, authorization, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, string? missing = null)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = authorization;
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
                    // A page that is not the cluster's - a sign-in page, a proxy's - read as no documents would hide that it never was.
                    throw new InvalidOperationException($"Elasticsearch answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A document that is not there answers 404 with no error - only its found or its result says so.
                    var error = (json as JsonObject)?["error"];
                    if (missing is not null && response.StatusCode == HttpStatusCode.NotFound && error is null)
                    {
                        throw new InvalidOperationException(missing);
                    }

                    // An error is an object with a type and a reason - or, from a proxy or a security layer, a bare word.
                    var type = error is JsonObject typed ? Text(typed["type"]) : null;
                    var said = error is JsonObject reasoned ? Text(reasoned["reason"]) : Text(error);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : $" ({type})")}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
