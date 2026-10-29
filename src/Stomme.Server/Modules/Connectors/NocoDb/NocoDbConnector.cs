#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

namespace Stomme.Modules.Connectors.NocoDb
{
    /// <summary>
    /// NocoDB (n8n's NocoDB node: a table's rows created, got, read many, updated, upserted, counted and deleted): a NocoDB
    /// - its cloud or one of your own - over its v2 data API (<c>/api/v2/tables/{table}/records</c>, which NocoDB's cloud and every
    /// self-hosted version since 0.204 serve), an API token in the <c>xc-token</c> header, over the host's egress. A test names
    /// how many bases the token sees.
    /// </summary>
    /// <remarks>
    /// <c>records</c> (a where formula, a sort, some fields, a view - page by page up to <c>max</c>), <c>get_record</c>,
    /// <c>create_record</c> (one, or a list), <c>update_record</c> (by its id, or its primary key among its fields),
    /// <c>upsert_record</c> (an id updates, none creates - as n8n's Create or Update does), <c>delete_record</c>,
    /// <c>count_records</c>; <c>bases</c> and <c>tables</c>. A table's primary key is <c>Id</c> unless the connection says
    /// otherwise. Every id, formula and field held to its shape before anything is sent. A throttled call waits as NocoDB says;
    /// a failure is thrown with its status and NocoDB's word.
    /// </remarks>
    public sealed class NocoDbConnector : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string GetRecordOperation = "get_record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string UpsertRecordOperation = "upsert_record";

        public const string DeleteRecordOperation = "delete_record";

        public const string CountRecordsOperation = "count_records";

        public const string BasesOperation = "bases";

        public const string TablesOperation = "tables";

        public const string DefaultEndpoint = "https://app.nocodb.com";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[^\\x00-\\x1F,]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RecordsOperation, GetRecordOperation, CreateRecordOperation, UpdateRecordOperation, UpsertRecordOperation, DeleteRecordOperation, CountRecordsOperation, BasesOperation, TablesOperation };

        public string ConnectorType => "nocodb";

        public string DisplayName => "NocoDB";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://nocodb.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://nocodb.com/docs/apis-and-mcp/rest-apis");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("primary_key", "Primary key", ConnectorConfigFieldType.Text, Required: false, Description: "The tables' primary key field - Id, NocoDB's own, when left empty.") { Default = "Id" },
        });

        private static readonly ConnectorConfigField TableField = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - md_0a1b2c3d4e5f6g, from its details or Get tables.");

        private static readonly ConnectorConfigField IdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "Its primary key's value - 42.");

        private static readonly ConnectorConfigField WhereField = new("where", "Where", ConnectorConfigFieldType.Text, Required: false, Description: "NocoDB's filter formula - (Status,eq,Paid)~and(Total,gt,100).");

        private static readonly ConnectorConfigField ViewField = new("view", "View", ConnectorConfigFieldType.Text, Required: false, Description: "A view's id - its filters and sort apply.");

        private static ConnectorConfigField Fields(bool required) => new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: required, Description: "A JSON object of field names and values - {\"Name\": \"Ada\", \"Total\": 99.5}; a list of them for many.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "A table's records - filtered, sorted, some fields - n8n's NocoDB Search and Get Many.")
            {
                Parameters = Schema(
                    TableField, WhereField,
                    new ConnectorConfigField("sort", "Sort", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated fields, - before one for descending - Status,-Total."),
                    new ConnectorConfigField("columns", "Columns", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated fields to read - all when left empty."),
                    ViewField,
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(GetRecordOperation, "Get a record", "One record by its primary key - n8n's NocoDB Get.")
            {
                Parameters = Schema(TableField, IdField, new ConnectorConfigField("columns", "Columns", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated fields to read.")),
            },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "A record, or a list of them - n8n's NocoDB Create.") { Parameters = Schema(TableField, Fields(required: true)) },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Changes the fields given - n8n's NocoDB Update.")
            {
                Parameters = Schema(TableField, new ConnectorConfigField("id", "Record", ConnectorConfigFieldType.Text, Required: false, Description: "Its primary key's value - or give it among the fields."), Fields(required: true)),
            },
            new ConnectorOperation(UpsertRecordOperation, "Create or update a record", "With an id the record is updated, without one it is created - n8n's NocoDB Create or Update.")
            {
                Parameters = Schema(TableField, new ConnectorConfigField("id", "Record", ConnectorConfigFieldType.Text, Required: false, Description: "Its primary key's value - empty creates."), Fields(required: true)),
            },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "Deletes a record by its primary key - n8n's NocoDB Delete.") { Parameters = Schema(TableField, IdField) },
            new ConnectorOperation(CountRecordsOperation, "Count records", "How many records a table holds, filtered - n8n's NocoDB Count.") { Parameters = Schema(TableField, WhereField, ViewField) },
            new ConnectorOperation(BasesOperation, "Get bases", "The bases the token sees."),
            new ConnectorOperation(TablesOperation, "Get tables", "A base's tables - each its id and title.")
            {
                Parameters = Schema(new ConnectorConfigField("base", "Base", ConnectorConfigFieldType.Text, Required: true, Description: "Its id - p979g1063032uw4, from Get bases.")),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var bases = (await session.SendAsync(HttpMethod.Get, "/api/v2/meta/bases/", null, cancellationToken))?["list"] as JsonArray ?? new JsonArray();
                return ConnectionTestResult.Ok($"NocoDB: {(bases.Count == 1 ? "one base" : $"{bases.Count} bases")} seen.");
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
                ?? throw new InvalidOperationException($"NocoDB has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var key = Values.Get(context.Settings, "primary_key") ?? "Id";
            if (!FieldName.IsMatch(key))
            {
                throw new InvalidOperationException($"'{key}' is not a field's name (primary key).");
            }

            string? table = null;
            if (operation is not (BasesOperation or TablesOperation))
            {
                table = Values.Get(p, "table") is { } named ? Identifier.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's id - letters, digits, _ and -.") : throw new InvalidOperationException("Give the table - its id.");
            }

            var id = Values.Get(p, "id") is { } given ? Identifier.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's id - letters, digits, _ and -.") : null;
            if (id is null && operation is GetRecordOperation or DeleteRecordOperation)
            {
                throw new InvalidOperationException("Give the record - its primary key's value.");
            }

            var query = new List<string>();
            foreach (var (name, parameter) in new[] { ("where", "where"), ("sort", "sort"), ("fields", "columns"), ("viewId", "view") })
            {
                var applies = parameter switch
                {
                    "where" or "view" => operation is RecordsOperation or CountRecordsOperation,
                    "sort" => operation == RecordsOperation,
                    _ => operation is RecordsOperation or GetRecordOperation,
                };
                if (!applies || Values.Get(p, parameter) is not { } value)
                {
                    continue;
                }

                var fits = parameter switch
                {
                    "where" => value.Length <= 4096 && !value.Any(char.IsControl),
                    "view" => Identifier.IsMatch(value),
                    _ => value.Split(',').All(field => FieldName.IsMatch(field.Trim())),
                };
                if (!fits)
                {
                    throw new InvalidOperationException(parameter switch
                    {
                        "where" => "The where formula is one line of up to 4,096 characters.",
                        "view" => $"'{value}' is not a view's id.",
                        _ => $"'{value}' is not a list of fields - Status,-Total.",
                    });
                }

                query.Add(name + "=" + Uri.EscapeDataString(parameter == "where" ? value : string.Join(",", value.Split(',').Select(field => field.Trim()))));
            }

            JsonNode? body = null;
            if (operation is CreateRecordOperation or UpdateRecordOperation or UpsertRecordOperation)
            {
                var fields = Values.Get(p, "fields") ?? throw new InvalidOperationException("Give the fields - a JSON object.");
                JsonNode? parsed = null;
                try
                {
                    parsed = JsonNode.Parse(fields);
                }
                catch (JsonException)
                {
                }

                var records = parsed switch
                {
                    JsonObject one => new List<JsonObject> { one },
                    JsonArray many when operation == CreateRecordOperation && many.Count is > 0 and <= 100 && many.All(item => item is JsonObject) => many.OfType<JsonObject>().ToList(),
                    JsonArray => throw new InvalidOperationException(operation == CreateRecordOperation ? "Give one to 100 records - JSON objects." : "Give one record's fields - a JSON object."),
                    _ => throw new InvalidOperationException("The fields are not a JSON object - {\"Name\": \"Ada\"}."),
                };
                if (operation != CreateRecordOperation)
                {
                    var record = records[0];
                    if (id is not null)
                    {
                        record[key] = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : id;
                    }
                    else if (operation == UpdateRecordOperation && record[key] is null)
                    {
                        throw new InvalidOperationException($"Give the record - its id, or its primary key ({key}) among the fields.");
                    }
                    else if (operation == UpsertRecordOperation)
                    {
                        record.Remove(key);
                    }
                }

                body = records.Count == 1 ? records[0] : new JsonArray(records.Select(record => (JsonNode)record.DeepClone()).ToArray());
            }

            string? baseId = null;
            if (operation == TablesOperation)
            {
                baseId = Values.Get(p, "base") is { } named ? Identifier.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a base's id - letters, digits, _ and -.") : throw new InvalidOperationException("Give the base - its id.");
            }

            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
            using var client = session.Client;
            var path = "/api/v2/tables/" + table + "/records";
            var tail = query.Count == 0 ? "" : "?" + string.Join("&", query);
            switch (operation)
            {
                case RecordsOperation:
                    {
                        var read = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var asked = Math.Min(PageSize, max - read);
                            var page = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"offset={read}").Append($"limit={asked}")), null, cancellationToken);
                            var list = (page?["list"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in list)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            if (list.Count < asked || page?["pageInfo"]?["isLastPage"] is JsonValue last && last.TryGetValue<bool>(out var isLast) && isLast)
                            {
                                yield break;
                            }
                        }
                    }

                case GetRecordOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, path + "/" + id + tail, null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CountRecordsOperation:
                    {
                        var counted = await session.SendAsync(HttpMethod.Get, path + "/count" + tail, null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["count"] = counted?["count"] is JsonValue count && count.TryGetValue<long>(out var total) ? total : null });
                        yield break;
                    }

                case DeleteRecordOperation:
                    await session.SendAsync(HttpMethod.Delete, path, new JsonObject { [key] = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : id }, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["table"] = table, ["id"] = id, ["deleted"] = true });
                    yield break;
                case BasesOperation:
                case TablesOperation:
                    {
                        var listed = await session.SendAsync(HttpMethod.Get, operation == BasesOperation ? "/api/v2/meta/bases/" : "/api/v2/meta/bases/" + baseId + "/tables", null, cancellationToken);
                        foreach (var item in (listed?["list"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(item["id"]), ["title"] = Text(item["title"]) });
                        }

                        yield break;
                    }

                default:
                    {
                        // A create answers with the new records' keys, an update with the changed ones': each record its fields as
                        // written and its key as NocoDB gave it.
                        var method = operation == CreateRecordOperation || (operation == UpsertRecordOperation && id is null) ? HttpMethod.Post : HttpMethod.Patch;
                        var answer = await session.SendAsync(method, path, body, cancellationToken);
                        var keys = (answer as JsonArray)?.OfType<JsonObject>().ToList() ?? (answer is JsonObject single ? new List<JsonObject> { single } : new List<JsonObject>());
                        var written = body as JsonArray is { } many ? many.OfType<JsonObject>().ToList() : new List<JsonObject> { (JsonObject)body! };
                        for (var i = 0; i < written.Count; i++)
                        {
                            var record = (JsonObject)written[i].DeepClone();
                            if (i < keys.Count && keys[i][key] is { } value)
                            {
                                record[key] = value.DeepClone();
                            }

                            yield return Record(record);
                        }

                        yield break;
                    }
            }
        }

        // A record: its fields by their names - a list or an object (a link, an attachment) as its JSON.
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

        // One connection's calls: the NocoDB's address, the token in xc-token; a throttled call waiting as NocoDB says; a failure
        // thrown with its status and NocoDB's word.
        private sealed class Session
        {
            private readonly string api;
            private readonly string token;

            private Session(string api, string token, HttpClient client)
            {
                this.api = api;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                var api = Values.BaseUrl(endpoint, DefaultEndpoint);
                var token = Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The NocoDB connection has no API token.");
                return new Session(api, token, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, api + path);
                    request.Headers.TryAddWithoutValidation("xc-token", token);
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
                    // A page that is not NocoDB's - a sign-in page, a proxy's - read as no records would hide that it never was.
                    throw new InvalidOperationException($"NocoDB answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = json is JsonObject error ? Text(error["msg"]) ?? Text(error["message"]) ?? Text(error["error"]) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
