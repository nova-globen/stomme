#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Airtable
{
    /// <summary>
    /// Airtable (n8n's Airtable node: its search, create and update): a table's records over Airtable's API
    /// (<c>https://api.airtable.com/v0</c>), a personal access token as the Bearer token. A test asks Airtable whom the token is
    /// (<c>meta/whoami</c>).
    /// </summary>
    /// <remarks>
    /// <c>search</c>: the table's records - optionally a view's, or those a formula keeps (Airtable's filterByFormula), in pages of
    /// 100 followed by their offset - one record each: its fields, <c>id</c> and <c>created_time</c>. <c>create</c>: <c>fields</c>, a
    /// record or a list of records as JSON, sent ten at a time as Airtable takes them (typecast on, as n8n's is, so "12" fills a
    /// number); <c>update</c>: <c>records</c>, a record or a list each with its <c>id</c> and the fields to change. Each answers the
    /// records Airtable kept. The base's id is written into the path, so it is held to Airtable's shape (app and fourteen
    /// letters and digits); the table's name is escaped; a record id to update is held to its shape too. A throttled call waits
    /// as it is told (the family's Retry-After policy). A failure is thrown with its status and Airtable's own error type
    /// (<c>HTTP 404 NOT_FOUND.</c>), the port answering it in its words.
    /// </remarks>
    public sealed class AirtableConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.airtable.com/v0";

        public const string SearchOperation = "search";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        /// <summary>The most records Airtable takes in one create or update.</summary>
        public const int Batch = 10;

        private static readonly Regex BaseId = new("^app[A-Za-z0-9]{14}$", RegexOptions.CultureInvariant);

        private static readonly Regex RecordId = new("^rec[A-Za-z0-9]{14}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "airtable";

        public string DisplayName => "Airtable";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Airtable names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Personal access token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri WebsiteUrl { get; } = new("https://www.airtable.com");

        public Uri ApiDocumentationUrl { get; } = new("https://airtable.com/developers/web/api/introduction");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("base", "Base", ConnectorConfigFieldType.Text, Required: false,
                Description: "A base's id (app...) - from its address, airtable.com/app.../... - which an operation naming none uses."),
        });

        private static readonly ConnectorConfigField BaseParameter = new("base", "Base", ConnectorConfigFieldType.Text, Required: false, Description: "The base's id (app...). Empty: the connection's.");

        private static readonly ConnectorConfigField TableParameter = new("table", "Table", ConnectorConfigFieldType.Text, Required: true, Description: "The table's name, or its id (tbl...).");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search records", "The table's records - a view's, or those a formula keeps - one record each: its fields, id and created_time - n8n's search.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BaseParameter,
                    TableParameter,
                    new ConnectorConfigField("view", "View", ConnectorConfigFieldType.Text, Required: false, Description: "A view's name or id: its records, in its order."),
                    new ConnectorConfigField("formula", "Formula", ConnectorConfigFieldType.Text, Required: false, Description: "Airtable's filterByFormula - the records it is true for, e.g. {Status} = 'Open'."),
                }),
            },
            new ConnectorOperation(CreateOperation, "Create records", "Creates a record, or each of a list of records, from their fields - n8n's create; one record each that Airtable kept, its id among its fields.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BaseParameter,
                    TableParameter,
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: "A record's fields as JSON, or a list of records - {\"Name\": \"Ada\", \"Total\": 12}."),
                }),
            },
            new ConnectorOperation(UpdateOperation, "Update records", "Changes fields of records named by their ids - n8n's update; one record each that Airtable kept.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    BaseParameter,
                    TableParameter,
                    new ConnectorConfigField("records", "Records", ConnectorConfigFieldType.Multiline, Required: true, Description: "A record, or a list, each its id and the fields to change - {\"id\": \"rec...\", \"Status\": \"Done\"}."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, baseUrl + "/meta/whoami", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {me["email"]?.GetValue<string>() ?? me["id"]?.GetValue<string>() ?? "the token's owner"}.");
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
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var baseId = Base(Values.Get(context.Parameters, "base") ?? Values.Get(context.Settings, "base") ?? throw new InvalidOperationException("Name the base - on the operation or the connection."));
            var table = Values.Get(context.Parameters, "table") ?? throw new InvalidOperationException("Name the table.");
            var tableUrl = $"{baseUrl}/{baseId}/{Uri.EscapeDataString(table)}";
            var token = Token(context.Credentials);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (string.Equals(context.OperationName, CreateOperation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.OperationName, UpdateOperation, StringComparison.OrdinalIgnoreCase))
            {
                var update = string.Equals(context.OperationName, UpdateOperation, StringComparison.OrdinalIgnoreCase);
                var records = Records(Values.Get(context.Parameters, update ? "records" : "fields"), update);
                for (var start = 0; start < records.Count; start += Batch)
                {
                    var body = new JsonObject { ["records"] = new JsonArray(records.Skip(start).Take(Batch).Select(record => (JsonNode?)record).ToArray()), ["typecast"] = true };
                    var answer = await SendAsync(client, token, update ? HttpMethod.Patch : HttpMethod.Post, tableUrl, body, cancellationToken);
                    foreach (var kept in (answer["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(kept);
                    }
                }

                yield break;
            }

            string? offset = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new List<string> { "pageSize=100" };
                if (Values.Get(context.Parameters, "view") is { } view)
                {
                    query.Add("view=" + Uri.EscapeDataString(view));
                }

                if (Values.Get(context.Parameters, "formula") is { } formula)
                {
                    query.Add("filterByFormula=" + Uri.EscapeDataString(formula));
                }

                if (offset is not null)
                {
                    query.Add("offset=" + Uri.EscapeDataString(offset));
                }

                var page = await SendAsync(client, token, HttpMethod.Get, tableUrl + "?" + string.Join("&", query), null, cancellationToken);
                foreach (var found in (page["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return Record(found);
                }

                offset = page["offset"]?.GetValue<string>();
            }
            while (!string.IsNullOrEmpty(offset));
        }

        // The records to send: for a create each record's fields; for an update each its id (held to its shape) and fields.
        private static List<JsonObject> Records(string? given, bool update)
        {
            if (string.IsNullOrWhiteSpace(given))
            {
                throw new InvalidOperationException(update ? "Give the records to update." : "Give the fields of the records to create.");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The records are not JSON - a record or a list of records.");
            }

            var records = parsed switch
            {
                JsonObject one => new List<JsonObject> { one },
                JsonArray many when many.All(item => item is JsonObject) => many.Cast<JsonObject>().ToList(),
                _ => throw new InvalidOperationException("The records are not a record or a list of records."),
            };

            return records.Select(record =>
            {
                var fields = new JsonObject();
                string? id = null;
                foreach (var (name, value) in record)
                {
                    if (update && name == "id")
                    {
                        id = value is JsonValue text && text.TryGetValue<string>(out var given) ? given : value?.ToJsonString();
                        continue;
                    }

                    fields[name] = value?.DeepClone();
                }

                if (!update)
                {
                    return new JsonObject { ["fields"] = fields };
                }

                return id is not null && RecordId.IsMatch(id)
                    ? new JsonObject { ["id"] = id, ["fields"] = fields }
                    : throw new InvalidOperationException($"'{id}' is not a record id (rec...) - each record to update names its own.");
            }).ToList();
        }

        // A record as the pipeline reads it: its fields, its id and when it was made.
        private static ConnectorRecord Record(JsonObject record)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = record["id"]?.GetValue<string>(),
                ["created_time"] = record["createdTime"]?.GetValue<string>(),
            };
            foreach (var (name, value) in record["fields"] as JsonObject ?? new JsonObject())
            {
                fields[name] = value switch
                {
                    null => null,
                    JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
                    JsonValue scalar when scalar.TryGetValue<long>(out var number) => number,
                    JsonValue scalar when scalar.TryGetValue<double>(out var real) => real,
                    JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
                    _ => value.ToJsonString(), // a list (linked records, attachments) or an object, as JSON
                };
            }

            return new ConnectorRecord(fields);
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") ?? throw new InvalidOperationException("The connection has no access token.");

        private static string Base(string id) =>
            BaseId.IsMatch(id.Trim()) ? id.Trim() : throw new InvalidOperationException($"'{id}' is not a base id - app and fourteen letters and digits, from the base's address.");

        // Airtable's answer as JSON, a throttled call (Airtable allows five a second for a base) waiting as it is told; a failure
        // thrown with its status and Airtable's own error type.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (content is not null)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Airtable answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var type = json?["error"] is JsonObject error ? error["type"]?.GetValue<string>() : json?["error"]?.GetValue<string>();
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(type is null ? "" : " " + type)}.");
            }

            return json as JsonObject ?? throw new InvalidOperationException("Airtable answered with no JSON.");
        }
    }
}
