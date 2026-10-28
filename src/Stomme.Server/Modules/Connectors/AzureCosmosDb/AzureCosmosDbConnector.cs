#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.AzureCosmosDb
{
    /// <summary>
    /// Azure Cosmos DB (BL-229, n8n's Azure Cosmos DB node: a database's containers and items, and its SQL) over the NoSQL API's
    /// REST interface, every request signed with the account's key (Cosmos DB's master-key token over the verb, the resource type,
    /// its link and the date), over the host's egress. A test names the database and its account.
    /// </summary>
    /// <remarks>
    /// <para><c>containers</c>, <c>container_get</c>, <c>container_create</c> (its partition key path, its throughput - manual RU/s or
    /// an autoscale most - and an indexing policy) and <c>container_delete</c>; <c>items</c> (read page by page by Cosmos DB's
    /// continuation, up to <c>max</c>), <c>item_get</c>, <c>item_create</c> (or an upsert), <c>item_update</c> (its fields merged
    /// over the stored item, written only if that item has not changed since - or the item replaced whole) and <c>item_delete</c>;
    /// <c>query</c> (a SQL query, over several lines from the step's Query box, its parameters bound - n8n's $1 as @Param1 - across
    /// partitions, page by page).</para>
    /// <para><b>A partition key</b>: an item's own is read from it by the container's partition key path; another operation's is the
    /// <c>partition</c> given - a JSON array as written (a number, a hierarchical key), any other text a string - or, where the
    /// container is partitioned by <c>/id</c>, the item's id. A record is an item's fields as Cosmos DB gives them, its system
    /// fields (<c>_rid</c>, <c>_etag</c> ...) left out unless <c>simple</c> is false, as n8n's Simplify does. Every id, a JSON object
    /// and a throughput held to its shape before anything is signed; Cosmos DB's code and message on a failure.</para>
    /// </remarks>
    public sealed class AzureCosmosDbConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private const string ApiVersion = "2018-12-31";

        private static readonly string[] Known =
        {
            "containers", "container_get", "container_create", "container_delete", "items", "item_get", "item_create", "item_update", "item_delete", "query",
        };

        private static readonly Regex Name = new("^[A-Za-z0-9_-]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly TimeProvider time;

        public AzureCosmosDbConnector()
            : this(null)
        {
        }

        internal AzureCosmosDbConnector(TimeProvider? time) => this.time = time ?? TimeProvider.System;

        public string ConnectorType => "azurecosmosdb";

        public string DisplayName => "Azure Cosmos DB";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://kafe.documents.azure.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-key"] = "Primary key (the account > Settings > Keys)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://azure.microsoft.com/products/cosmos-db/");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/rest/api/cosmos-db/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: true, Description: "The database's id - kafe."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ContainerField = Field("container", "Container", "The container's id - orders.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Item id", "The item's id - 1042.", required: true);

        private static readonly ConnectorConfigField PartitionField = Field("partition", "Partition key", "The item's partition key value - a JSON array as written ([2], [\"se\", 2]), any other text a string. Empty where the container is partitioned by /id: the item's id.");

        private static readonly ConnectorConfigField SimpleField = Field("simple", "Simplify", "false keeps Cosmos DB's system fields (_rid, _etag, _ts ...).", ConnectorConfigFieldType.Boolean) with { Default = "true" };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("containers", "Get containers", "The database's containers - n8n's Container Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation("container_get", "Get a container", "n8n's Container Get.") { Parameters = Schema(ContainerField) },
            new ConnectorOperation("container_create", "Create a container", "n8n's Container Create.")
            {
                Parameters = Schema(
                    ContainerField,
                    Field("partition_key", "Partition key path", "/till, or /tenant,/till for a hierarchical key.") with { Default = "/id" },
                    Field("throughput", "Manual throughput", "RU/s, 400 or more.", ConnectorConfigFieldType.Integer),
                    Field("max_throughput", "Autoscale most", "RU/s, 1000 or more - autoscale instead of a manual throughput.", ConnectorConfigFieldType.Integer),
                    Field("indexing_policy", "Indexing policy", "A JSON object - Cosmos DB's own when empty.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation("container_delete", "Delete a container", "n8n's Container Delete.") { Parameters = Schema(ContainerField) },
            new ConnectorOperation("items", "Get items", "A container's items - n8n's Item Get Many.") { Parameters = Schema(ContainerField, MaxField, SimpleField) },
            new ConnectorOperation("item_get", "Get an item", "n8n's Item Get.") { Parameters = Schema(ContainerField, IdField, PartitionField, SimpleField) },
            new ConnectorOperation("item_create", "Create an item", "n8n's Item Create.")
            {
                Parameters = Schema(
                    ContainerField,
                    Field("item", "Item", "A JSON object with its id - {\"id\": \"1042\", \"till\": \"2\", \"total\": 35.5}.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("upsert", "Upsert", "true: replace an item with its id where there is one.", ConnectorConfigFieldType.Boolean),
                    SimpleField),
            },
            new ConnectorOperation("item_update", "Update an item", "n8n's Item Update.")
            {
                Parameters = Schema(
                    ContainerField,
                    IdField,
                    Field("item", "Fields", "A JSON object of the fields that change - {\"total\": 40}.", ConnectorConfigFieldType.Multiline, required: true),
                    PartitionField,
                    Field("replace", "Replace whole", "true: the item becomes what is given; else its fields are merged over the stored item.", ConnectorConfigFieldType.Boolean),
                    SimpleField),
            },
            new ConnectorOperation("item_delete", "Delete an item", "n8n's Item Delete.") { Parameters = Schema(ContainerField, IdField, PartitionField) },
            new ConnectorOperation("query", "Execute a query", "A SQL query across the container's partitions - n8n's Item Execute Query; over several lines in the step's Query box.")
            {
                Parameters = Schema(
                    ContainerField,
                    Field("sql", "Query", "SELECT c.id, c.total FROM c WHERE c.total > @min", ConnectorConfigFieldType.Multiline),
                    Field("parameters", "Parameters", "A JSON object of its parameters - {\"@min\": 10}; n8n's $1 is @Param1.", ConnectorConfigFieldType.Multiline),
                    MaxField,
                    SimpleField),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
                using var client = session.Client;
                var account = await session.SendAsync(HttpMethod.Get, string.Empty, "", string.Empty, null, null, cancellationToken);
                var containers = await session.SendAsync(HttpMethod.Get, session.Database + "/colls", "colls", session.Database, null, null, cancellationToken);
                var count = (containers.Json?["DocumentCollections"] as JsonArray)?.Count ?? 0;
                return ConnectionTestResult.Ok($"Azure Cosmos DB: the database '{session.DatabaseId}' on {Text(account.Json?["id"]) ?? "the account"} - {count} container{(count == 1 ? "" : "s")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? "containers").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Azure Cosmos DB has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is signed.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var simple = Flag(p, "simple") ?? true;
            var container = operation == "containers" ? string.Empty : NameOf(Values.Get(p, "container"), "container");
            var id = operation is "item_get" or "item_update" or "item_delete" ? IdOf(Values.Get(p, "id")) : string.Empty;
            var partition = Values.Get(p, "partition") is { } given ? PartitionOf(given) : null;
            JsonObject? body = null;
            var headers = new List<(string Name, string Value)>();
            switch (operation)
            {
                case "container_create":
                    {
                        var paths = (Values.Get(p, "partition_key") ?? "/id").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (paths.Length is 0 or > 3 || paths.Any(path => !path.StartsWith('/') || path.Length == 1 || path.Any(char.IsWhiteSpace)))
                        {
                            throw new InvalidOperationException($"'{Values.Get(p, "partition_key")}' is not a partition key path - /till, or up to three as /tenant,/till (partition_key).");
                        }

                        body = new JsonObject
                        {
                            ["id"] = container,
                            ["partitionKey"] = new JsonObject { ["paths"] = new JsonArray(paths.Select(path => (JsonNode)path).ToArray()), ["kind"] = paths.Length == 1 ? "Hash" : "MultiHash", ["version"] = 2 },
                        };
                        if (ObjectOf(p, "indexing_policy") is { } policy)
                        {
                            body["indexingPolicy"] = policy;
                        }

                        var manual = Whole(p, "throughput", 400);
                        var autoscale = Whole(p, "max_throughput", 1000);
                        if (manual is not null && autoscale is not null)
                        {
                            throw new InvalidOperationException("Give a manual throughput or an autoscale most, not both.");
                        }

                        if (manual is { } ru)
                        {
                            headers.Add(("x-ms-offer-throughput", ru.ToString(CultureInfo.InvariantCulture)));
                        }

                        if (autoscale is { } top)
                        {
                            headers.Add(("x-ms-cosmos-offer-autopilot-settings", "{\"maxThroughput\":" + top.ToString(CultureInfo.InvariantCulture) + "}"));
                        }

                        break;
                    }

                case "item_create":
                    body = ObjectOf(p, "item") ?? throw new InvalidOperationException("Give the item - a JSON object with its id (item).");
                    IdOf(Text(body["id"]) ?? throw new InvalidOperationException("Give the item its id - {\"id\": \"1042\", ...} (item)."));
                    if (Flag(p, "upsert") == true)
                    {
                        headers.Add(("x-ms-documentdb-is-upsert", "True"));
                    }

                    break;
                case "item_update":
                    body = ObjectOf(p, "item") ?? throw new InvalidOperationException("Give the fields that change - a JSON object (item).");
                    if (Text(body["id"]) is { } other && other != id)
                    {
                        throw new InvalidOperationException($"The item's id is '{id}' - its fields cannot give it another ('{other}').");
                    }

                    break;
                case "query":
                    {
                        var sql = Values.Get(p, "sql") ?? (p.TryGetValue("query", out var box) && !string.IsNullOrWhiteSpace(box) ? box : null)
                            ?? throw new InvalidOperationException("Give the query - or write it in the step's Query box.");
                        body = new JsonObject { ["query"] = Regex.Replace(sql, "\\$(\\d+)", "@Param$1"), ["parameters"] = new JsonArray() };
                        foreach (var (name, value) in ObjectOf(p, "parameters") ?? new JsonObject())
                        {
                            var named = name.StartsWith('@') ? name : "@" + name;
                            if (!Regex.IsMatch(named, "^@[A-Za-z_][A-Za-z0-9_]{0,63}$"))
                            {
                                throw new InvalidOperationException($"'{name}' is not a parameter's name - @min (parameters).");
                            }

                            ((JsonArray)body["parameters"]!).Add(new JsonObject { ["name"] = named, ["value"] = value?.DeepClone() });
                        }

                        break;
                    }
            }

            var session = Session.Of(context.Endpoint, context.Settings, context.Credentials, context.Egress, context.Timeout, time);
            using var client = session.Client;
            var collection = session.Database + "/colls/" + container;
            switch (operation)
            {
                case "containers":
                    foreach (var item in Items((await session.SendAsync(HttpMethod.Get, session.Database + "/colls", "colls", session.Database, null, null, cancellationToken)).Json, "DocumentCollections").Take(max))
                    {
                        yield return Record(item, simple: false);
                    }

                    break;
                case "container_get":
                    yield return Record((await session.SendAsync(HttpMethod.Get, collection, "colls", collection, null, null, cancellationToken)).Json as JsonObject ?? new JsonObject(), simple: false);
                    break;
                case "container_create":
                    yield return Record((await session.SendAsync(HttpMethod.Post, session.Database + "/colls", "colls", session.Database, body, headers, cancellationToken)).Json as JsonObject ?? new JsonObject(), simple: false);
                    break;
                case "container_delete":
                    await session.SendAsync(HttpMethod.Delete, collection, "colls", collection, null, null, cancellationToken);
                    yield return Fields(("id", container), ("deleted", true));
                    break;
                case "items":
                case "query":
                    {
                        // Page by page by Cosmos DB's continuation, a page no larger than what is still wanted.
                        var read = 0;
                        string? continuation = null;
                        do
                        {
                            var page = new List<(string Name, string Value)>(headers) { ("x-ms-max-item-count", Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)) };
                            if (continuation is not null)
                            {
                                page.Add(("x-ms-continuation", continuation));
                            }

                            if (operation == "query")
                            {
                                page.Add(("x-ms-documentdb-isquery", "True"));
                                page.Add(("x-ms-documentdb-query-enablecrosspartition", "True"));
                            }

                            var answer = operation == "query"
                                ? await session.SendAsync(HttpMethod.Post, collection + "/docs", "docs", collection, body, page, cancellationToken, query: true)
                                : await session.SendAsync(HttpMethod.Get, collection + "/docs", "docs", collection, null, page, cancellationToken);
                            foreach (var item in Items(answer.Json, "Documents"))
                            {
                                yield return Record(item, simple);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            continuation = answer.Continuation;
                        }
                        while (continuation is not null);

                        break;
                    }

                case "item_create":
                    {
                        var key = PartitionFromItem(body!, await session.PartitionPathsAsync(collection, cancellationToken));
                        var created = await session.SendAsync(HttpMethod.Post, collection + "/docs", "docs", collection, body, headers.Append(("x-ms-documentdb-partitionkey", key)).ToList(), cancellationToken);
                        yield return Record(created.Json as JsonObject ?? new JsonObject(), simple);
                        break;
                    }

                default:
                    {
                        var document = collection + "/docs/" + id;
                        var key = partition ?? (await session.PartitionPathsAsync(collection, cancellationToken) is ["/id"] ? new JsonArray(id).ToJsonString(Wire)
                            : throw new InvalidOperationException("Give its partition key value (partition) - the container is not partitioned by /id."));
                        var keyed = new List<(string Name, string Value)> { ("x-ms-documentdb-partitionkey", key) };
                        switch (operation)
                        {
                            case "item_get":
                                yield return Record((await session.SendAsync(HttpMethod.Get, document, "docs", document, null, keyed, cancellationToken)).Json as JsonObject ?? new JsonObject(), simple);
                                break;
                            case "item_delete":
                                await session.SendAsync(HttpMethod.Delete, document, "docs", document, null, keyed, cancellationToken);
                                yield return Fields(("id", id), ("deleted", true));
                                break;
                            default:
                                {
                                    var written = body!;
                                    if (Flag(p, "replace") != true)
                                    {
                                        // Merged over the stored item, written only if it has not changed since it was read.
                                        var stored = await session.SendAsync(HttpMethod.Get, document, "docs", document, null, keyed, cancellationToken);
                                        var merged = stored.Json as JsonObject ?? new JsonObject();
                                        foreach (var (field, value) in body!)
                                        {
                                            merged[field] = value?.DeepClone();
                                        }

                                        written = merged;
                                        if (Text(merged["_etag"]) is { } etag)
                                        {
                                            keyed.Add(("If-Match", etag));
                                        }
                                    }

                                    written["id"] = id;
                                    yield return Record((await session.SendAsync(HttpMethod.Put, document, "docs", document, written, keyed, cancellationToken)).Json as JsonObject ?? new JsonObject(), simple);
                                    break;
                                }
                        }

                        break;
                    }
            }
        }

        /// <summary>
        /// Cosmos DB's master-key token: an HMAC-SHA256 under the account's key over the verb, the resource type and its link, the
        /// date, and an empty line - each but the link lower-cased - as type=master&amp;ver=1.0&amp;sig=..., URL-escaped.
        /// </summary>
        public static string Token(string key, string verb, string resourceType, string resourceLink, string date)
        {
            byte[] secret;
            try
            {
                secret = Convert.FromBase64String(key);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("The Azure Cosmos DB key is not the account's key - it is base64, from the account's Keys.");
            }

            var text = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceLink}\n{date.ToLowerInvariant()}\n\n";
            var signature = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
            return Uri.EscapeDataString("type=master&ver=1.0&sig=" + signature);
        }

        /// <summary>An item's partition key as the header carries it: its value at each of the container's paths, in a JSON array - a missing one Cosmos DB's undefined, {}.</summary>
        public static string PartitionFromItem(JsonObject item, IReadOnlyList<string> paths)
        {
            var values = new JsonArray();
            foreach (var path in paths)
            {
                JsonNode? node = item;
                var found = true;
                foreach (var segment in path.Trim('/').Split('/'))
                {
                    if (node is not JsonObject holder || !holder.TryGetPropertyValue(segment, out node))
                    {
                        found = false; // absent: Cosmos DB's undefined - unlike a null, which is a value
                        break;
                    }
                }

                values.Add(found ? node?.DeepClone() : new JsonObject());
            }

            return values.ToJsonString(Wire);
        }

        private static string NameOf(string? given, string what) =>
            given is null ? throw new InvalidOperationException($"Give the {what}'s id ({what}).")
            : Name.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a {what}'s id - letters, digits, - and _ ({what}).");

        // An item's id: up to 255 characters, none of / \ ? # nor a control character (Cosmos DB's own rule).
        private static string IdOf(string? given) =>
            given is null ? throw new InvalidOperationException("Give the item's id (id).")
            : given.Length is > 0 and <= 255 && !given.Any(c => c is '/' or '\\' or '?' or '#' || char.IsControl(c)) ? given
            : throw new InvalidOperationException($"'{given}' is not an item id - at most 255 characters, none of / \\ ? # (id).");

        // A partition key value: a JSON array as written; any other text a string.
        private static string PartitionOf(string given)
        {
            if (given.StartsWith('['))
            {
                try
                {
                    if (JsonNode.Parse(given) is JsonArray array && array.Count is > 0 and <= 3)
                    {
                        return array.ToJsonString(Wire);
                    }
                }
                catch (JsonException)
                {
                }

                throw new InvalidOperationException($"'{given}' is not a partition key - a JSON array of one to three values, or text (partition).");
            }

            return new JsonArray(given).ToJsonString(Wire);
        }

        private static bool? Flag(IReadOnlyDictionary<string, string> p, string name) => Values.Get(p, name) switch
        {
            null => null,
            var given when string.Equals(given, "true", StringComparison.OrdinalIgnoreCase) => true,
            var given when string.Equals(given, "false", StringComparison.OrdinalIgnoreCase) => false,
            var given => throw new InvalidOperationException($"'{given}' is not true or false ({name})."),
        };

        private static int? Whole(IReadOnlyDictionary<string, string> p, string name, int least) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= least ? value : throw new InvalidOperationException($"'{given}' is not a whole number of RU/s, {least} or more ({name}).")
                : null;

        private static JsonObject? ObjectOf(IReadOnlyDictionary<string, string> p, string name)
        {
            if (Values.Get(p, name) is not { } given)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(given) as JsonObject ?? throw new InvalidOperationException($"The {name} is not a JSON object ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} is not a JSON object ({name}).");
            }
        }

        private static IEnumerable<JsonObject> Items(JsonNode? answer, string list) => (answer?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        private static ConnectorRecord Record(JsonObject item, bool simple) =>
            new(item.Where(pair => !simple || !pair.Key.StartsWith('_')).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed record Answer(JsonNode? Json, string? Continuation);

        // One connection's calls: the account's REST interface, each request signed for its resource with the account's key; a
        // throttled call waiting as Cosmos DB says; a failure thrown with its status, Cosmos DB's code and its message.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string key;
            private readonly TimeProvider time;
            private IReadOnlyList<string>? paths;

            private Session(string baseUrl, string databaseId, string key, HttpClient http, TimeProvider time)
            {
                this.baseUrl = baseUrl;
                DatabaseId = databaseId;
                this.key = key;
                this.time = time;
                Client = http;
            }

            public HttpClient Client { get; }

            public string DatabaseId { get; }

            public string Database => "dbs/" + DatabaseId;

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, TimeProvider time)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the account's address - https://kafe.documents.azure.com.");
                }

                var database = Values.Get(settings, "database") ?? throw new InvalidOperationException("Give the connection its database (database).");
                return new Session(
                    Values.BaseUrl(endpoint, string.Empty),
                    Name.IsMatch(database) ? database : throw new InvalidOperationException($"'{database}' is not a database's id - letters, digits, - and _ (database)."),
                    Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Azure Cosmos DB connection has no key."),
                    egress.CreateHttpClient(timeout),
                    time);
            }

            /// <summary>The container's partition key paths, read once.</summary>
            public async Task<IReadOnlyList<string>> PartitionPathsAsync(string collection, CancellationToken cancellationToken)
            {
                if (paths is null)
                {
                    var definition = await SendAsync(HttpMethod.Get, collection, "colls", collection, null, null, cancellationToken);
                    paths = (definition.Json?["partitionKey"]?["paths"] as JsonArray ?? new JsonArray()).Select(path => Text(path)).OfType<string>().ToList();
                }

                return paths;
            }

            public async Task<Answer> SendAsync(HttpMethod method, string link, string resourceType, string resourceLink, JsonNode? body, IReadOnlyList<(string Name, string Value)>? headers, CancellationToken cancellationToken, bool query = false)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + "/" + string.Join("/", link.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)));
                    var date = time.GetUtcNow().UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
                    request.Headers.TryAddWithoutValidation("x-ms-date", date);
                    request.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
                    request.Headers.TryAddWithoutValidation("Authorization", Token(key, method.Method, resourceType, resourceLink, date));
                    foreach (var (name, value) in headers ?? Array.Empty<(string, string)>())
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8);
                        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(query ? "application/query+json" : "application/json");
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
                    throw new InvalidOperationException("Azure Cosmos DB answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var message = Text(json?["message"]) is { } said ? said.Split('\n')[0].Trim() : null;
                    message = message is { Length: > 300 } ? message[..300] : message;
                    throw new InvalidOperationException($"Azure Cosmos DB answered HTTP {(int)response.StatusCode}{(Text(json?["code"]) is { } code ? " " + code : string.Empty)}{(message is null ? "." : ": " + message)}"
                        + (response.StatusCode == HttpStatusCode.Unauthorized ? " - check the key." : string.Empty));
                }

                return new Answer(json, response.Headers.TryGetValues("x-ms-continuation", out var values) && values.FirstOrDefault() is { Length: > 0 } next ? next : null);
            }
        }
    }
}
