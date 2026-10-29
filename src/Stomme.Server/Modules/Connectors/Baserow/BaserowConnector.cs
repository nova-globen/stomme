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

namespace Stomme.Modules.Connectors.Baserow
{
    /// <summary>
    /// Baserow (n8n's Baserow node: a table's rows) over its REST API - Baserow's cloud at <c>https://api.baserow.io</c>,
    /// or a self-hosted Baserow's own address - with a database token (<c>Authorization: Token ...</c>), over the host's egress.
    /// A test counts the tables the token sees.
    /// </summary>
    /// <remarks>
    /// <para><c>rows</c> - a table's rows filtered (Baserow's own filter types, AND or OR), searched, sorted, some fields, a view's,
    /// page by page up to <c>max</c>; <c>get_row</c>; <c>create_row</c>, <c>update_row</c> and <c>delete_row</c>; <c>create_rows</c>,
    /// <c>update_rows</c> and <c>delete_rows</c> - up to 200 at once, Baserow's batch; <c>tables</c> - every table the token
    /// sees; <c>fields</c> - a table's fields. Rows come back by their fields' names (<c>user_field_names</c>).</para>
    /// <para>A field is named by its name, or by its id as n8n's node names it (<c>field_123</c>, a filter's number): ids are
    /// mapped to names - and a filter's names to ids, as Baserow filters by id - through the table's fields, read once and only
    /// when needed, an unknown one refused. Every value held to its shape before anything is sent; Baserow's error and detail
    /// said with its status.</para>
    /// </remarks>
    public sealed class BaserowConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.baserow.io";

        public const string RowsOperation = "rows";

        public const string GetRowOperation = "get_row";

        public const string CreateRowOperation = "create_row";

        public const string UpdateRowOperation = "update_row";

        public const string DeleteRowOperation = "delete_row";

        public const string CreateRowsOperation = "create_rows";

        public const string UpdateRowsOperation = "update_rows";

        public const string DeleteRowsOperation = "delete_rows";

        public const string TablesOperation = "tables";

        public const string FieldsOperation = "fields";

        public const int DefaultMax = 1000;

        /// <summary>Baserow's largest page and largest batch.</summary>
        public const int PageSize = 200;

        private static readonly Regex Id = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldIdName = new("^field_([0-9]{1,18})$", RegexOptions.CultureInvariant);

        private static readonly Regex FilterType = new("^[a-z_]{1,60}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RowsOperation, GetRowOperation, CreateRowOperation, UpdateRowOperation, DeleteRowOperation, CreateRowsOperation, UpdateRowsOperation, DeleteRowsOperation, TablesOperation, FieldsOperation };

        public string ConnectorType => "baserow";

        public string DisplayName => "Baserow";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string> CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-key"] = "Database token (Baserow > Settings > Database tokens)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://baserow.io");

        public Uri ApiDocumentationUrl { get; } = new("https://baserow.io/api-docs");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static readonly ConnectorConfigField TableField = Field("table", "Table", "Its id - 4210, from its API docs or Get tables.", required: true);

        private static readonly ConnectorConfigField RowField = Field("row", "Row", "Its id - 7.", required: true);

        private static ConnectorConfigField FieldsField(bool required) => Field("fields", "Fields", "A JSON object of field names - or ids, field_123 - and values: {\"Name\": \"Ada\", \"Total\": 99.5}.", required, ConnectorConfigFieldType.Multiline);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RowsOperation, "Get rows", "A table's rows - filtered, searched, sorted, some fields - n8n's Baserow Get Many.")
            {
                Parameters = Schema(
                    TableField,
                    Field("filters", "Filters", "A JSON list of conditions - [{\"field\": \"Status\", \"type\": \"equal\", \"value\": \"Paid\"}] - Baserow's filter types: equal, contains, higher_than, date_is, empty...", type: ConnectorConfigFieldType.Multiline),
                    Field("filter_type", "Match", "AND (every condition, the default) or OR (any).") with { Default = "AND" },
                    Field("search", "Search", "Rows whose text contains this."),
                    Field("order_by", "Sort", "Comma-separated fields, - before one for descending - Status,-Total."),
                    Field("include", "Fields", "Comma-separated fields to read - all when left empty."),
                    Field("view", "View", "A view's id - its filters and sort apply."),
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(GetRowOperation, "Get a row", "One row by its id - n8n's Baserow Get.") { Parameters = Schema(TableField, RowField) },
            new ConnectorOperation(CreateRowOperation, "Create a row", "A row of the fields given - n8n's Baserow Create.") { Parameters = Schema(TableField, FieldsField(required: true)) },
            new ConnectorOperation(UpdateRowOperation, "Update a row", "Changes the fields given - n8n's Baserow Update.") { Parameters = Schema(TableField, RowField, FieldsField(required: true)) },
            new ConnectorOperation(DeleteRowOperation, "Delete a row", "Deletes a row by its id - n8n's Baserow Delete.") { Parameters = Schema(TableField, RowField) },
            new ConnectorOperation(CreateRowsOperation, "Create rows", "Up to 200 rows at once - n8n's Baserow Batch Create.")
            {
                Parameters = Schema(TableField, Field("rows", "Rows", "A JSON list of rows' fields - [{\"Name\": \"Ada\"}, {\"Name\": \"Bo\"}].", required: true, ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateRowsOperation, "Update rows", "Up to 200 rows at once, each by its id - n8n's Baserow Batch Update.")
            {
                Parameters = Schema(TableField, Field("rows", "Rows", "A JSON list of rows' fields, each with its id - [{\"id\": 7, \"Status\": \"Paid\"}].", required: true, ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(DeleteRowsOperation, "Delete rows", "Up to 200 rows at once, by their ids - n8n's Baserow Batch Delete.")
            {
                Parameters = Schema(TableField, Field("rows", "Rows", "Their ids, comma-separated - 7, 8, 12.", required: true)),
            },
            new ConnectorOperation(TablesOperation, "Get tables", "Every table the token sees - its id, name and database."),
            new ConnectorOperation(FieldsOperation, "Get fields", "A table's fields - each its id, name and type.") { Parameters = Schema(TableField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var tables = await session.SendAsync(HttpMethod.Get, "/api/database/tables/all-tables/", null, cancellationToken) as JsonArray ?? new JsonArray();
                return ConnectionTestResult.Ok($"Baserow: {(tables.Count == 1 ? "one table" : $"{tables.Count} tables")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? RowsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Baserow has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var table = operation == TablesOperation ? null
                : Values.Get(p, "table") is { } named ? Id.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's id - digits.") : throw new InvalidOperationException("Give the table - its id.");
            var row = operation is GetRowOperation or UpdateRowOperation or DeleteRowOperation
                ? Values.Get(p, "row") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a row's id - digits.") : throw new InvalidOperationException("Give the row - its id.")
                : null;

            var filters = operation == RowsOperation ? Conditions(Values.Get(p, "filters")) : new List<(JsonNode Field, string Type, string Value)>();
            var filterType = Values.GetOrDefault(p, "filter_type", "AND").ToUpperInvariant() is ("AND" or "OR") and var matched ? matched : throw new InvalidOperationException($"'{Values.Get(p, "filter_type")}' is not AND or OR (filter_type).");
            var order = operation == RowsOperation ? Names(Values.Get(p, "order_by"), "order_by", descending: true) : new List<(string Name, bool Descending)>();
            var include = operation == RowsOperation ? Names(Values.Get(p, "include"), "include", descending: false) : new List<(string Name, bool Descending)>();
            var view = operation == RowsOperation && Values.Get(p, "view") is { } v ? Id.IsMatch(v) ? v : throw new InvalidOperationException($"'{v}' is not a view's id - digits.") : null;
            var search = operation == RowsOperation ? Values.Get(p, "search") : null;
            if (search is not null && (search.Length > 1000 || search.Any(char.IsControl)))
            {
                throw new InvalidOperationException("The search is one line of up to 1,000 characters.");
            }

            JsonObject? fields = operation is CreateRowOperation or UpdateRowOperation
                ? Parse(Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object."), "fields") as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object - {\"Name\": \"Ada\"}.")
                : null;
            List<JsonObject>? rows = null;
            List<long>? ids = null;
            if (operation is CreateRowsOperation or UpdateRowsOperation)
            {
                rows = Parse(Values.Get(p, "rows") ?? throw new InvalidOperationException("Give the rows - a JSON list of their fields."), "rows") is JsonArray list && list.Count is > 0 and <= PageSize && list.All(item => item is JsonObject)
                    ? list.OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()).ToList()
                    : throw new InvalidOperationException($"Give one to {PageSize} rows - a JSON list of objects.");
                if (operation == UpdateRowsOperation && rows.Any(item => item["id"] is not JsonValue key || !Id.IsMatch(key.ToJsonString().Trim('"'))))
                {
                    throw new InvalidOperationException("Each row to update needs its id - {\"id\": 7, ...}.");
                }
            }
            else if (operation == DeleteRowsOperation)
            {
                var listed = (Values.Get(p, "rows") ?? throw new InvalidOperationException("Give the rows - their ids.")).Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                ids = listed.Length is > 0 and <= PageSize && listed.All(Id.IsMatch)
                    ? listed.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToList()
                    : throw new InvalidOperationException($"Give one to {PageSize} rows' ids - digits, comma-separated.");
            }

            using var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            var path = "/api/database/rows/table/" + table + "/";
            switch (operation)
            {
                case TablesOperation:
                    foreach (var item in (await session.SendAsync(HttpMethod.Get, "/api/database/tables/all-tables/", null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;

                case FieldsOperation:
                    foreach (var item in (await session.SendAsync(HttpMethod.Get, "/api/database/fields/table/" + table + "/", null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                    }

                    yield break;

                case RowsOperation:
                    {
                        // A field named by its id read by its name, and a filter's field by its id: the table's fields, once, when needed.
                        var lookup = new FieldLookup(session, table!);
                        var query = new List<string> { "user_field_names=true" };
                        if (search is not null)
                        {
                            query.Add("search=" + Uri.EscapeDataString(search));
                        }

                        if (order.Count > 0)
                        {
                            var sorted = new List<string>();
                            foreach (var (name, descending) in order)
                            {
                                sorted.Add((descending ? "-" : "") + Quoted(await lookup.NameAsync(name, cancellationToken)));
                            }

                            query.Add("order_by=" + Uri.EscapeDataString(string.Join(",", sorted)));
                        }

                        if (include.Count > 0)
                        {
                            var chosen = new List<string>();
                            foreach (var (name, _) in include)
                            {
                                chosen.Add(Quoted(await lookup.NameAsync(name, cancellationToken)));
                            }

                            query.Add("include=" + Uri.EscapeDataString(string.Join(",", chosen)));
                        }

                        if (view is not null)
                        {
                            query.Add("view_id=" + view);
                        }

                        if (filters.Count > 0)
                        {
                            query.Add("filter_type=" + filterType);
                            foreach (var (field, type, value) in filters)
                            {
                                query.Add($"filter__field_{await lookup.IdAsync(field, cancellationToken)}__{type}=" + Uri.EscapeDataString(value));
                            }
                        }

                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"size={size}").Append($"page={page}")), null, cancellationToken);
                            var results = (answer?["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in results)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            // Baserow names its next page while there is one; an empty page ends it whatever it names.
                            if (results.Count == 0 || answer?["next"] is null)
                            {
                                yield break;
                            }
                        }
                    }

                case GetRowOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, path + row + "/?user_field_names=true", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;

                case CreateRowOperation:
                case UpdateRowOperation:
                    {
                        var body = await new FieldLookup(session, table!).NamedAsync(fields!, cancellationToken);
                        var method = operation == CreateRowOperation ? HttpMethod.Post : HttpMethod.Patch;
                        yield return Record(await session.SendAsync(method, path + (row is null ? "" : row + "/") + "?user_field_names=true", body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case DeleteRowOperation:
                    await session.SendAsync(HttpMethod.Delete, path + row + "/", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["id"] = long.Parse(row!, CultureInfo.InvariantCulture), ["deleted"] = true });
                    yield break;

                case CreateRowsOperation:
                case UpdateRowsOperation:
                    {
                        var lookup = new FieldLookup(session, table!);
                        var items = new JsonArray();
                        foreach (var item in rows!)
                        {
                            items.Add(await lookup.NamedAsync(item, cancellationToken));
                        }

                        var method = operation == CreateRowsOperation ? HttpMethod.Post : HttpMethod.Patch;
                        var answer = await session.SendAsync(method, path + "batch/?user_field_names=true", new JsonObject { ["items"] = items }, cancellationToken);
                        foreach (var item in (answer?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return Record(item);
                        }

                        yield break;
                    }

                case DeleteRowsOperation:
                    await session.SendAsync(HttpMethod.Post, path + "batch-delete/", new JsonObject { ["items"] = new JsonArray(ids!.Select(id => (JsonNode)id).ToArray()) }, cancellationToken);
                    foreach (var id in ids!)
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["id"] = id, ["deleted"] = true });
                    }

                    yield break;
            }
        }

        // A filter list: each condition's field (a name, or an id - a number or field_123), Baserow's filter type and its value.
        private static List<(JsonNode Field, string Type, string Value)> Conditions(string? given)
        {
            var conditions = new List<(JsonNode Field, string Type, string Value)>();
            if (given is null)
            {
                return conditions;
            }

            if (Parse(given, "filters") is not JsonArray list || list.Count > 100)
            {
                throw new InvalidOperationException("The filters are not a JSON list of up to 100 conditions - [{\"field\": \"Status\", \"type\": \"equal\", \"value\": \"Paid\"}].");
            }

            foreach (var item in list)
            {
                if (item is not JsonObject condition || condition["field"] is not JsonValue field || (Text(field) is not { Length: > 0 } && !(field.TryGetValue<long>(out var number) && number >= 0)))
                {
                    throw new InvalidOperationException("Each filter names its field - {\"field\": \"Status\", \"type\": \"equal\", \"value\": \"Paid\"}.");
                }

                var type = Text(condition["type"]) ?? "equal";
                if (!FilterType.IsMatch(type))
                {
                    throw new InvalidOperationException($"'{type}' is not one of Baserow's filter types - equal, contains, higher_than...");
                }

                var value = condition["value"] switch
                {
                    null => "",
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue other => other.ToJsonString(),
                    _ => throw new InvalidOperationException("A filter's value is one value - text, a number or true/false."),
                };
                conditions.Add((field.DeepClone(), type, value));
            }

            return conditions;
        }

        // A comma-separated list of fields - a name with a comma in double quotes, as Baserow writes one - each perhaps with - before it.
        private static List<(string Name, bool Descending)> Names(string? given, string what, bool descending)
        {
            var names = new List<(string Name, bool Descending)>();
            if (given is null)
            {
                return names;
            }

            var current = new StringBuilder();
            var quoted = false;
            for (var i = 0; i <= given.Length; i++)
            {
                if (i == given.Length || (given[i] == ',' && !quoted))
                {
                    var name = current.ToString().Trim();
                    current.Clear();
                    var down = descending && name.StartsWith('-');
                    name = (down ? name[1..] : name).Trim();
                    if (name.Length > 1 && name.StartsWith('"') && name.EndsWith('"'))
                    {
                        name = name[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
                    }

                    if (name.Length == 0 || name.Length > 255 || name.Any(char.IsControl))
                    {
                        throw new InvalidOperationException($"'{given}' is not a list of fields - Status,-Total ({what}).");
                    }

                    names.Add((name, down));
                    continue;
                }

                if (given[i] == '"' && (i == 0 || given[i - 1] != '\\'))
                {
                    quoted = !quoted;
                }

                current.Append(given[i]);
            }

            return names;
        }

        // A field's name as Baserow reads one in a list: in double quotes - its own escaped - when it holds a comma or a quote.
        private static string Quoted(string name) => name.Contains(',', StringComparison.Ordinal) || name.Contains('"', StringComparison.Ordinal) ? "\"" + name.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : name;

        private static JsonNode? Parse(string text, string what)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {what} are not JSON.");
            }
        }

        // A row, a table or a field: its fields by their names - a list or an object (a link, a file, a choice) as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
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

        /// <summary>A table's fields, read once when a field is named by its id (or a filter by its name): each name for its id and back.</summary>
        private sealed class FieldLookup(Session session, string table)
        {
            private List<(long Id, string Name)>? fields;

            /// <summary>A field's name: a name as it is, an id (<c>field_123</c>) as its field's name.</summary>
            public async Task<string> NameAsync(string given, CancellationToken cancellationToken)
            {
                if (FieldIdName.Match(given) is not { Success: true } id)
                {
                    return given;
                }

                var number = long.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture);
                return (await FieldsAsync(cancellationToken)).FirstOrDefault(field => field.Id == number).Name ?? throw new InvalidOperationException($"'{given}' is not a field of table {table}.");
            }

            /// <summary>A field's id, as Baserow filters by one: a number or <c>field_123</c> as it is, a name as its field's id.</summary>
            public async Task<long> IdAsync(JsonNode given, CancellationToken cancellationToken)
            {
                if (given is JsonValue number && number.TryGetValue<long>(out var id))
                {
                    return id;
                }

                var text = Text(given)!;
                if (FieldIdName.Match(text) is { Success: true } named)
                {
                    return long.Parse(named.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                var found = (await FieldsAsync(cancellationToken)).Where(field => field.Name == text).ToList();
                return found.Count == 1 ? found[0].Id : throw new InvalidOperationException($"'{text}' is not a field of table {table}.");
            }

            /// <summary>A row's fields by their names: a key that is an id (<c>field_123</c>) written as its field's name.</summary>
            public async Task<JsonObject> NamedAsync(JsonObject row, CancellationToken cancellationToken)
            {
                var named = new JsonObject();
                foreach (var (key, value) in row)
                {
                    var name = key == "id" ? key : await NameAsync(key, cancellationToken);
                    if (named.ContainsKey(name))
                    {
                        throw new InvalidOperationException($"The field '{name}' is given twice.");
                    }

                    named[name] = value?.DeepClone();
                }

                return named;
            }

            private async Task<List<(long Id, string Name)>> FieldsAsync(CancellationToken cancellationToken) =>
                fields ??= (await session.SendAsync(HttpMethod.Get, "/api/database/fields/table/" + table + "/", null, cancellationToken) as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(field => field["id"] is JsonValue id && id.TryGetValue<long>(out _) && Text(field["name"]) is not null)
                    .Select(field => (field["id"]!.GetValue<long>(), Text(field["name"])!))
                    .ToList();
        }

        // One connection's calls: the Baserow's address, the database token; a throttled call waiting as Baserow says; a failure
        // thrown with its status, Baserow's error and its detail.
        private sealed class Session : IDisposable
        {
            private readonly string api;
            private readonly string token;
            private readonly HttpClient client;

            private Session(string api, string token, HttpClient client)
            {
                this.api = api;
                this.token = token;
                this.client = client;
            }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var uri = new Uri(api);
                if (uri.Query.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not a Baserow's address - {DefaultEndpoint}, or a self-hosted Baserow's own.");
                }

                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Baserow connection has no database token.");
                if (token.Any(c => c <= ' ' || c > '~'))
                {
                    throw new InvalidOperationException("The database token is one word of printable characters.");
                }

                return new Session(api.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? api[..^4] : api, token, egress.CreateHttpClient(timeout));
            }

            public void Dispose() => client.Dispose();

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
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
                    // A page that is not Baserow's - a sign-in page, a proxy's - read as no rows would hide that it never was.
                    throw new InvalidOperationException($"Baserow answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var code = Text(error?["error"]);
                    var detail = error?["detail"] switch
                    {
                        null => null,
                        JsonValue text when text.TryGetValue<string>(out var s) => s,
                        var other => other.ToJsonString(Wire),
                    };
                    var said = string.Join(" - ", new[] { code, detail }.Where(part => !string.IsNullOrEmpty(part)));
                    throw new InvalidOperationException($"Baserow answered HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] + "..." : said))}");
                }

                return json;
            }
        }
    }
}
