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

namespace Stomme.Modules.Connectors.Grist
{
    /// <summary>
    /// Grist (BL-229, n8n's Grist node: a table's rows created, read, updated, created or updated, and deleted): a Grist - its
    /// cloud, a team site or one of your own - over its REST API (<c>/api/docs/{doc}/tables/{table}/records</c>), an API key as a
    /// bearer token, over the host's egress. A test names how many sites the key sees.
    /// </summary>
    /// <remarks>
    /// <c>records</c> (a filter, a sort, up to <c>max</c>), <c>get_record</c>, <c>create_record</c> (one, or a list),
    /// <c>update_record</c>, <c>upsert_record</c> (the record matching some fields updated, or one created - n8n's Create or
    /// Update), <c>delete_records</c>, <c>sql</c> (a SELECT with its arguments - Grist runs nothing else); <c>docs</c>,
    /// <c>tables</c> and <c>columns</c>. Every document, table, column and record id held to its shape before anything is sent.
    /// A throttled call waits as Grist says; a failure is thrown with its status and Grist's word.
    /// </remarks>
    public sealed class GristConnector : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string GetRecordOperation = "get_record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string UpsertRecordOperation = "upsert_record";

        public const string DeleteRecordsOperation = "delete_records";

        public const string SqlOperation = "sql";

        public const string DocsOperation = "docs";

        public const string TablesOperation = "tables";

        public const string ColumnsOperation = "columns";

        public const string DefaultEndpoint = "https://docs.getgrist.com";

        public const int DefaultMax = 1000;

        private const int MostAtOnce = 500;

        private static readonly Regex Document = new("^[A-Za-z0-9_~-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex Name = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex SortItem = new("^-?[A-Za-z0-9_]{1,128}(:(orderByChoice|naturalSort|emptyLast))*$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RecordsOperation, GetRecordOperation, CreateRecordOperation, UpdateRecordOperation, UpsertRecordOperation, DeleteRecordsOperation, SqlOperation, DocsOperation, TablesOperation, ColumnsOperation };

        public string ConnectorType => "grist";

        public string DisplayName => "Grist";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.getgrist.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://support.getgrist.com/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField DocField = new("doc", "Document", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - fSLRDYaF3BWXAE6kNXF4bS, from its settings or Get documents.");

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - Orders, as Raw Data shows it.");

        private static readonly ConnectorConfigField IdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "Its row id - 42.");

        private static ConnectorConfigField Fields(bool required) => new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: required, Description: "A JSON object of column ids and values - {\"Name\": \"Ada\", \"Total\": 99.5}; a list of them for many.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "A table's records - filtered, sorted - n8n's Grist Get Many Rows.")
            {
                Parameters = Schema(
                    DocField, TableField,
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "A JSON object of columns and the values they may hold - {\"Status\": [\"Paid\", \"Sent\"]}."),
                    new ConnectorConfigField("sort", "Sort", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated columns, - before one for descending - Status,-Total."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(GetRecordOperation, "Get a record", "One record by its row id.") { Parameters = Schema(DocField, TableField, IdField) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "A record, or a list of them - n8n's Grist Create Row.") { Parameters = Schema(DocField, TableField, Fields(required: true)) },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Changes the fields given - n8n's Grist Update Row.") { Parameters = Schema(DocField, TableField, IdField, Fields(required: true)) },
            new ConnectorOperation(UpsertRecordOperation, "Create or update a record", "The record matching some fields updated, or one created with them - n8n's Grist Create or Update.")
            {
                Parameters = Schema(
                    DocField, TableField,
                    new ConnectorConfigField("match", "Match", ConnectorConfigFieldType.Text, Required: true, Description: "A JSON object of the fields a record is found by - {\"Email\": \"ada@example.com\"}."),
                    Fields(required: false),
                    new ConnectorConfigField("on_many", "On many", ConnectorConfigFieldType.Text, Required: false, Description: "When more than one matches: first (updated, the default), all, or none.")),
            },
            new ConnectorOperation(DeleteRecordsOperation, "Delete records", "Deletes records by their row ids - n8n's Grist Delete Row.")
            {
                Parameters = Schema(DocField, TableField, new ConnectorConfigField("ids", "Records", ConnectorConfigFieldType.Text, Required: true, Description: "Comma-separated row ids - 4,7.")),
            },
            new ConnectorOperation(SqlOperation, "Query with SQL", "A SELECT over the document's tables - Grist runs nothing that changes them.")
            {
                Parameters = Schema(
                    DocField,
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Text, Required: true, Description: "One SELECT - select Name, Total from Orders where Status = ?."),
                    new ConnectorConfigField("args", "Arguments", ConnectorConfigFieldType.Text, Required: false, Description: "A JSON list of the ?'s values - [\"Paid\"]."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(DocsOperation, "Get documents", "The documents of the site the address names - each its id, name and workspace."),
            new ConnectorOperation(TablesOperation, "Get tables", "A document's tables.") { Parameters = Schema(DocField) },
            new ConnectorOperation(ColumnsOperation, "Get columns", "A table's columns - each its id, label and type.") { Parameters = Schema(DocField, TableField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var sites = await session.SendAsync(HttpMethod.Get, "/api/orgs", null, cancellationToken) as JsonArray ?? new JsonArray();
                return sites.Count == 0
                    ? ConnectionTestResult.Fail("Grist: the key sees no site.")
                    : ConnectionTestResult.Ok($"Grist: {(sites.Count == 1 ? "one site" : $"{sites.Count} sites")} seen.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? RecordsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Grist has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string? doc = null;
            if (operation != DocsOperation)
            {
                doc = Values.Get(p, "doc") is { } named ? Document.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a document's id - letters, digits, _, - and ~.") : throw new InvalidOperationException("Give the document - its id.");
            }

            string? table = null;
            if (operation is not (DocsOperation or TablesOperation or SqlOperation))
            {
                table = Values.Get(p, "table") is { } named ? Name.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's id - letters, digits and _.") : throw new InvalidOperationException("Give the table - its id.");
            }

            long? id = null;
            if (operation is GetRecordOperation or UpdateRecordOperation)
            {
                id = Values.Get(p, "id") is { } given ? RowId(given) : throw new InvalidOperationException("Give the record - its row id.");
            }

            var ids = new List<long>();
            if (operation == DeleteRecordsOperation)
            {
                ids = (Values.Get(p, "ids") ?? throw new InvalidOperationException("Give the records - their row ids.")).Split(',', StringSplitOptions.TrimEntries).Select(RowId).Distinct().ToList();
                if (ids.Count > MostAtOnce)
                {
                    throw new InvalidOperationException($"Give up to {MostAtOnce} records at once.");
                }
            }

            JsonObject? filter = null;
            if (operation == RecordsOperation && Values.Get(p, "filter") is { } wanted)
            {
                filter = Parse(wanted) as JsonObject ?? throw new InvalidOperationException("The filter is not a JSON object - {\"Status\": [\"Paid\", \"Sent\"]}.");
                foreach (var (column, allowed) in filter.ToList())
                {
                    if (!Name.IsMatch(column))
                    {
                        throw new InvalidOperationException($"'{column}' is not a column's id (filter).");
                    }

                    if (allowed is not JsonArray)
                    {
                        // One value is a list of one.
                        filter[column] = new JsonArray(allowed?.DeepClone());
                    }
                }
            }

            string? sort = null;
            if (operation == RecordsOperation && Values.Get(p, "sort") is { } order)
            {
                var columns = order.Split(',', StringSplitOptions.TrimEntries);
                sort = columns.All(SortItem.IsMatch) ? string.Join(",", columns) : throw new InvalidOperationException($"'{order}' is not a list of columns - Status,-Total.");
            }

            var records = new List<JsonObject>();
            if (operation is CreateRecordOperation or UpdateRecordOperation or UpsertRecordOperation)
            {
                var fields = Values.Get(p, "fields") ?? (operation == UpsertRecordOperation ? "{}" : throw new InvalidOperationException("Give the fields - a JSON object."));
                records = Parse(fields) switch
                {
                    JsonObject one => new List<JsonObject> { one },
                    JsonArray many when operation == CreateRecordOperation && many.Count is > 0 and <= MostAtOnce && many.All(item => item is JsonObject) => many.OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()).ToList(),
                    JsonArray => throw new InvalidOperationException(operation == CreateRecordOperation ? $"Give one to {MostAtOnce} records - JSON objects." : "Give one record's fields - a JSON object."),
                    _ => throw new InvalidOperationException("The fields are not a JSON object - {\"Name\": \"Ada\"}."),
                };
            }

            JsonObject? match = null;
            string? onMany = null;
            if (operation == UpsertRecordOperation)
            {
                match = Parse(Values.Get(p, "match") ?? throw new InvalidOperationException("Give the match - a JSON object of the fields a record is found by.")) is JsonObject { Count: > 0 } found
                    ? found
                    : throw new InvalidOperationException("The match is not a JSON object of fields - {\"Email\": \"ada@example.com\"}.");
                onMany = Values.Get(p, "on_many") is { } many
                    ? many.ToLowerInvariant() is "first" or "all" or "none" ? many.ToLowerInvariant() : throw new InvalidOperationException($"'{many}' is not first, all or none (on many).")
                    : null;
            }

            JsonObject? sql = null;
            if (operation == SqlOperation)
            {
                sql = new JsonObject { ["sql"] = Values.Get(p, "query") ?? throw new InvalidOperationException("Give the query - one SELECT.") };
                if (Values.Get(p, "args") is { } arguments)
                {
                    sql["args"] = Parse(arguments) is JsonArray list && list.All(item => item is JsonValue value && (value.TryGetValue<string>(out _) || value.TryGetValue<double>(out _)))
                        ? list
                        : throw new InvalidOperationException("The arguments are not a JSON list of texts and numbers - [\"Paid\", 100].");
                }
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            var path = $"/api/docs/{doc}/tables/{table}/records";
            switch (operation)
            {
                case RecordsOperation:
                case GetRecordOperation:
                    {
                        var query = new List<string>();
                        var only = operation == GetRecordOperation ? new JsonObject { ["id"] = new JsonArray(id) } : filter;
                        if (only is { Count: > 0 })
                        {
                            query.Add("filter=" + Uri.EscapeDataString(only.ToJsonString(Wire)));
                        }

                        if (sort is not null)
                        {
                            query.Add("sort=" + Uri.EscapeDataString(sort));
                        }

                        if (operation == RecordsOperation)
                        {
                            query.Add("limit=" + max.ToString(CultureInfo.InvariantCulture));
                        }

                        var found = Rows(await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query), null, cancellationToken)).Take(max).ToList();
                        if (operation == GetRecordOperation && found.Count == 0)
                        {
                            throw new InvalidOperationException($"{table} has no record {id}.");
                        }

                        foreach (var row in found)
                        {
                            yield return Record(row);
                        }

                        yield break;
                    }

                case CreateRecordOperation:
                    {
                        // Grist answers with the new records' ids, in the order they were sent.
                        var answer = await session.SendAsync(HttpMethod.Post, path, new JsonObject { ["records"] = new JsonArray(records.Select(record => (JsonNode)new JsonObject { ["fields"] = record.DeepClone() }).ToArray()) }, cancellationToken);
                        var made = Rows(answer).ToList();
                        for (var i = 0; i < records.Count; i++)
                        {
                            yield return Record(new JsonObject { ["id"] = i < made.Count ? made[i]["id"]?.DeepClone() : null, ["fields"] = records[i].DeepClone() });
                        }

                        yield break;
                    }

                case UpdateRecordOperation:
                    await session.SendAsync(HttpMethod.Patch, path, new JsonObject { ["records"] = new JsonArray(new JsonObject { ["id"] = id, ["fields"] = records[0].DeepClone() }) }, cancellationToken);
                    yield return Record(new JsonObject { ["id"] = id, ["fields"] = records[0].DeepClone() });
                    yield break;
                case UpsertRecordOperation:
                    {
                        await session.SendAsync(HttpMethod.Put, path + (onMany is null or "first" ? "" : "?onmany=" + onMany), new JsonObject { ["records"] = new JsonArray(new JsonObject { ["require"] = match!.DeepClone(), ["fields"] = records[0].DeepClone() }) }, cancellationToken);

                        // The record as it was matched and written.
                        var written = (JsonObject)match!.DeepClone();
                        foreach (var (name, value) in records[0])
                        {
                            written[name] = value?.DeepClone();
                        }

                        yield return Record(new JsonObject { ["fields"] = written });
                        yield break;
                    }

                case DeleteRecordsOperation:
                    await session.SendAsync(HttpMethod.Post, path + "/delete", new JsonArray(ids.Select(row => (JsonNode)row).ToArray()), cancellationToken);
                    foreach (var row in ids)
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["id"] = row, ["deleted"] = true });
                    }

                    yield break;
                case SqlOperation:
                    foreach (var row in Rows(await session.SendAsync(HttpMethod.Post, $"/api/docs/{doc}/sql", sql, cancellationToken)).Take(max))
                    {
                        yield return Record(row);
                    }

                    yield break;
                case DocsOperation:
                    {
                        // The site's workspaces, each with its documents.
                        var workspaces = await session.SendAsync(HttpMethod.Get, "/api/orgs/current/workspaces", null, cancellationToken) as JsonArray ?? new JsonArray();
                        foreach (var workspace in workspaces.OfType<JsonObject>())
                        {
                            foreach (var item in (workspace["docs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]), ["name"] = Text(item["name"]), ["workspace"] = Text(workspace["name"]) });
                            }
                        }

                        yield break;
                    }

                case TablesOperation:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, $"/api/docs/{doc}/tables", null, cancellationToken))?["tables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]) });
                    }

                    yield break;
                default:
                    foreach (var item in ((await session.SendAsync(HttpMethod.Get, $"/api/docs/{doc}/tables/{table}/columns", null, cancellationToken))?["columns"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]), ["label"] = Text(item["fields"]?["label"]), ["type"] = Text(item["fields"]?["type"]) });
                    }

                    yield break;
            }
        }

        private static long RowId(string text) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var row) && row > 0 ? row : throw new InvalidOperationException($"'{text}' is not a record's row id - a number above 0.");

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

        private static IEnumerable<JsonObject> Rows(JsonNode? answer) => (answer?["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        // A record: its row id, then its fields by their column ids - a list (Grist's ["L", ...]) or an object as its JSON.
        private static ConnectorRecord Record(JsonObject row)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row["id"] is JsonValue id && id.TryGetValue<long>(out var number))
            {
                fields["id"] = number;
            }

            foreach (var (name, value) in row["fields"] as JsonObject ?? new JsonObject())
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

        // One connection's calls: the Grist's address, the key as a bearer token; a throttled call waiting as Grist says; a
        // failure thrown with its status and Grist's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string key;

            private Session(string api, string key, HttpClient client)
            {
                this.api = api;
                this.key = key;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var key = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Grist connection has no API key.");
                return new Session(api, key, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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
                    // A page that is not Grist's - a sign-in page, a proxy's - read as no records would hide that it never was.
                    throw new InvalidOperationException($"Grist answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["error"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
