#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.ServiceNow
{
    /// <summary>
    /// ServiceNow (BL-96 increment 4, lifted from the predecessor platform's <c>servicenow</c>): any Table-API table paged by offset, a short
    /// or empty page a clean stop (the legacy bridge threw on an empty page), reference objects collapsed to one
    /// <c>{name}_value</c> column, a pasted API URL forgiven down to the instance. Basic credentials; every request through
    /// the host's egress (D110).
    /// </summary>
    /// <remarks>
    /// BL-229 (n8n's ServiceNow node) adds a record got, created, updated and deleted in any table, and an incident created and
    /// updated by its own fields - <c>table</c> unchanged but for an optional <c>max</c> and a step's own display values.
    /// A record's values are a JSON object of columns and values; every table, sys_id, column, value and choice held to its
    /// shape before anything is sent. A written record comes back as ServiceNow keeps it now, its references collapsed as
    /// <c>table</c>'s are. A failure is thrown with its status and ServiceNow's message and detail; a success answer that is
    /// not JSON a failure that says so.
    /// </remarks>
    public sealed class ServiceNowConnector : IConnector, IConnectorExtractor
    {
        internal const string DefaultPageSize = "1000";

        public const string TableOperation = "table";

        public const string RecordOperation = "record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const string CreateIncidentOperation = "create_incident";

        public const string UpdateIncidentOperation = "update_incident";

        private static readonly string[] Known = { TableOperation, RecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation, CreateIncidentOperation, UpdateIncidentOperation };

        private static readonly Regex TableName = new("^[a-z][a-z0-9_]{0,79}$", RegexOptions.CultureInvariant);

        private static readonly Regex SysId = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        private static readonly Regex Column = new("^[a-z][a-z0-9_]{0,79}$", RegexOptions.CultureInvariant);

        private static readonly Regex Choice = new("^[A-Za-z0-9_\\-]{1,40}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]{0,4000}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // An incident's own fields, by ServiceNow's column names - n8n's.
        private static readonly string[] IncidentFields =
        {
            "short_description", "description", "caller_id", "urgency", "impact", "state", "category", "subcategory", "assignment_group", "assigned_to",
            "contact_type", "business_service", "cmdb_ci", "hold_reason", "work_notes", "close_code", "close_notes",
        };

        public string ConnectorType => "servicenow";

        public string DisplayName => "ServiceNow";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public string EndpointExample => "https://acme.service-now.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Integration user's name (User Administration > Users - with the roles its steps need)",
            ["password"] = "Its password",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri? WebsiteUrl => new("https://www.servicenow.com");

        public Uri? ApiDocumentationUrl => new("https://developer.servicenow.com/dev.do#!/reference/api/latest/rest/c_TableAPI");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per page (sysparm_limit).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
            new ConnectorConfigField("display_value", "Return display values", ConnectorConfigFieldType.Boolean, Description: "Ask the Table API for display values instead of raw values (sysparm_display_value).")
            {
                Group = "Query",
                Default = "false",
            },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField TableField = Field("table", "Table name", "The table - incident, cmdb_ci_computer, sys_user.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Record", "Its sys_id - 32 hexadecimal characters.", required: true);

        private static readonly ConnectorConfigField DisplayField = Field("display_value", "Display values", "false (the values), true (what they display) or all (both) - the connection's setting when left empty.");

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of columns and values - {\"short_description\": \"Printer down\", \"urgency\": \"2\"}.", ConnectorConfigFieldType.Multiline);

        private static ConnectorConfigField[] IncidentSchema(bool create) => new[]
        {
            Field("short_description", "Short description", "What happened, in a line.", required: create),
            Field("description", "Description", "The whole story.", ConnectorConfigFieldType.Multiline),
            Field("caller_id", "Caller", "The caller - a user's sys_id or name."),
            Field("urgency", "Urgency", "1 (high), 2 (medium) or 3 (low)."),
            Field("impact", "Impact", "1 (high), 2 (medium) or 3 (low)."),
            Field("state", "State", "Its state's value - 1 New, 2 In Progress, 3 On Hold, 6 Resolved, 7 Closed."),
            Field("category", "Category", "Its category's value - inquiry, software, hardware."),
            Field("subcategory", "Subcategory", "Its subcategory's value."),
            Field("assignment_group", "Assignment group", "A group's sys_id or name."),
            Field("assigned_to", "Assigned to", "A user's sys_id or name."),
            Field("contact_type", "Channel", "How it came - email, phone, self-service, walk-in."),
            Field("business_service", "Service", "A business service's sys_id or name."),
            Field("cmdb_ci", "Configuration item", "A configuration item's sys_id or name."),
            Field("hold_reason", "On hold reason", "Its value, when it is put on hold."),
            Field("work_notes", "Work notes", "A note for those working on it.", ConnectorConfigFieldType.Multiline),
            Field("close_code", "Resolution code", "Its value, when it is resolved."),
            Field("close_notes", "Resolution notes", "How it was resolved.", ConnectorConfigFieldType.Multiline),
            ValuesField,
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TableOperation, "Table", "Streams any Table-API table (e.g. incident, cmdb_ci_computer).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("table", "Table name", ConnectorConfigFieldType.Text, Required: true, Description: "The table to read, e.g. cmdb_ci_computer."),
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Description: "Optional comma-separated column list (sysparm_fields)."),
                    new ConnectorConfigField("query", "Query", ConnectorConfigFieldType.Text, Description: "Optional encoded query (sysparm_query)."),
                    new ConnectorConfigField("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Description: "How many rows at most - every one when left empty."),
                    DisplayField,
                }),
            },
            new ConnectorOperation(RecordOperation, "Get a record", "A record of any table by its sys_id - n8n's ServiceNow Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, IdField, Field("fields", "Fields", "Comma-separated columns - every one when left empty."), DisplayField }),
            },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "A record in any table - n8n's ServiceNow Table Record Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, ValuesField }),
            },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "A record's columns changed, the rest kept - n8n's ServiceNow Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, IdField, Field("values", "Values", ValuesField.Description!, ConnectorConfigFieldType.Multiline, required: true) }),
            },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "A record of any table - n8n's ServiceNow Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, IdField }),
            },
            new ConnectorOperation(CreateIncidentOperation, "Create an incident", "By its own fields, and any other column in values - n8n's ServiceNow Incident Create.")
            {
                Parameters = new ConnectorConfigSchema(IncidentSchema(create: true)),
            },
            new ConnectorOperation(UpdateIncidentOperation, "Update an incident", "Its fields changed, the rest kept - n8n's ServiceNow Incident Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { IdField }.Concat(IncidentSchema(create: false)).ToArray()),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = BaseUrl(context.Endpoint);
                using var client = BearerClient.Create(context.Egress, context.Timeout, BearerClient.Basic(context.Credentials, "ServiceNow"), acceptJson: true);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/api/now/table/sys_user?sysparm_limit=1&sysparm_fields=sys_id")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "ServiceNow", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"ServiceNow responded with {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail(FailureMessage(response.StatusCode));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? TableOperation
                : Known.FirstOrDefault(known => string.Equals(known, context.OperationName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"ServiceNow has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var baseUrl = BaseUrl(context.Endpoint);
            var authorization = BearerClient.Basic(context.Credentials, "ServiceNow");
            var display = DisplayValue(context.Settings, p);
            if (operation == TableOperation)
            {
                var table = Values.Get(p, "table") ?? throw new InvalidOperationException("The table operation requires a 'table' parameter.");
                var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : int.MaxValue;
                var limit = Math.Min(PageSize(context.Settings, p), max);

                using var client = BearerClient.Create(context.Egress, context.Timeout, authorization, acceptJson: true);
                var offset = 0;
                var read = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rows = ReadPage(await BearerClient.GetAsync(client, BuildUrl(baseUrl, table, limit, offset, display, p), FailureMessage, cancellationToken));
                    foreach (var row in rows)
                    {
                        yield return FlattenedRows.Flatten(row, collapseReferences: true);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    // A short (or empty) page ends the scan cleanly.
                    if (rows.Count < limit)
                    {
                        yield break;
                    }

                    offset += limit;
                }
            }

            // Everything held to its shape before anything is sent.
            var incident = operation is CreateIncidentOperation or UpdateIncidentOperation;
            var name = incident ? "incident"
                : Values.Get(p, "table") is { } named ? TableName.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's name - incident, cmdb_ci_computer (table).")
                : throw new InvalidOperationException("Give the table (table).");
            var id = operation is CreateRecordOperation or CreateIncidentOperation ? null
                : Values.Get(p, "id") is { } given ? SysId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's sys_id - 32 hexadecimal characters (id).")
                : throw new InvalidOperationException("Give the record's sys_id (id).");
            JsonObject? body = null;
            if (operation is CreateRecordOperation or UpdateRecordOperation || incident)
            {
                body = ValuesOf(p) ?? new JsonObject();
                if (incident)
                {
                    foreach (var field in IncidentFields)
                    {
                        if (Values.Get(p, field) is not { } value)
                        {
                            continue;
                        }

                        if (body.ContainsKey(field))
                        {
                            throw new InvalidOperationException($"'{field}' is given twice - by itself and in the values.");
                        }

                        body[field] = field switch
                        {
                            "urgency" or "impact" => value is "1" or "2" or "3" ? value : throw new InvalidOperationException($"'{value}' is not 1 (high), 2 (medium) or 3 (low) ({field})."),
                            "state" or "category" or "subcategory" or "contact_type" or "hold_reason" => Choice.IsMatch(value) ? value : throw new InvalidOperationException($"'{value}' is not a choice's value - letters, digits, - and _ ({field})."),
                            _ => TextOf(value, field),
                        };
                    }

                    if (operation == CreateIncidentOperation && !(body["short_description"] is JsonValue line && line.TryGetValue<string>(out var said) && said.Trim().Length > 0))
                    {
                        throw new InvalidOperationException("Give the short description (short_description).");
                    }
                }

                if (body.Count == 0 && operation is UpdateRecordOperation or UpdateIncidentOperation)
                {
                    throw new InvalidOperationException("Give what to change (values).");
                }
            }

            var address = $"{baseUrl}/api/now/table/{name}{(id is null ? "" : "/" + id)}";
            if (operation != DeleteRecordOperation)
            {
                address += "?sysparm_exclude_reference_link=true&sysparm_display_value=" + display + (operation == RecordOperation && Values.Get(p, "fields") is { } fields ? "&sysparm_fields=" + Uri.EscapeDataString(fields) : "");
            }

            var method = operation switch
            {
                RecordOperation => HttpMethod.Get,
                CreateRecordOperation or CreateIncidentOperation => HttpMethod.Post,
                DeleteRecordOperation => HttpMethod.Delete,
                _ => HttpMethod.Patch,
            };
            using (var client = BearerClient.Create(context.Egress, context.Timeout, authorization, acceptJson: true))
            {
                var answer = await SendAsync(client, method, address, body, cancellationToken);
                if (operation == DeleteRecordOperation)
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["sys_id"] = id, ["deleted"] = true });
                    yield break;
                }

                using var document = JsonDocument.Parse(answer);
                if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("ServiceNow answered with no record.");
                }

                yield return FlattenedRows.Flatten(result, collapseReferences: true);
            }
        }

        // A record's values: a JSON object of columns and values - a text, a number, true or false each.
        private static JsonObject? ValuesOf(IReadOnlyDictionary<string, string> parameters)
        {
            if (Values.Get(parameters, "values") is not { } given)
            {
                return null;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonObject values)
            {
                throw new InvalidOperationException("The values are not a JSON object of columns and values - {\"short_description\": \"Printer down\"} (values).");
            }

            var body = new JsonObject();
            foreach (var (column, value) in values)
            {
                if (!Column.IsMatch(column))
                {
                    throw new InvalidOperationException($"'{column}' is not a column's name - lower-case letters, digits and _ (values).");
                }

                body[column] = value switch
                {
                    JsonValue text when text.TryGetValue<string>(out var s) => TextOf(s, column),
                    JsonValue other when other.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => other.DeepClone(),
                    _ => throw new InvalidOperationException($"The value of '{column}' is not a text, a number, true or false (values)."),
                };
            }

            return body;
        }

        private static string TextOf(string value, string name) =>
            Text.IsMatch(value) ? value : throw new InvalidOperationException($"The {name} is not a text of 4,000 characters at most, without control characters ({name}).");

        // The step's display values when it names them - false, true or all; the connection's setting otherwise.
        private static string DisplayValue(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters) =>
            Values.Get(parameters, "display_value") is { } given
                ? given.Trim().ToLowerInvariant() is "false" or "true" or "all" ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not false, true or all (display_value).")
                : Values.GetBool(settings, "display_value", defaultValue: false) ? "true" : "false";

        // ServiceNow's answer, a throttled call waiting as ServiceNow says; a failure thrown with its status and ServiceNow's
        // message and detail; a success that is not JSON a failure that says so (a delete's empty answer excepted).
        private static async Task<string> SendAsync(HttpClient client, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
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
                throw new InvalidOperationException($"ServiceNow answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = Words(json?["error"]?["message"]);
                var detail = Words(json?["error"]?["detail"]);
                var text = string.Join(": ", new[] { message, detail }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            if (json is null && method != HttpMethod.Delete)
            {
                throw new InvalidOperationException($"ServiceNow answered HTTP {(int)response.StatusCode} with nothing.");
            }

            return payload;
        }

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string BuildUrl(string baseUrl, string table, int limit, int offset, string displayValue, IReadOnlyDictionary<string, string> parameters)
        {
            var url = new StringBuilder().Append(baseUrl).Append("/api/now/table/").Append(Uri.EscapeDataString(table))
                .Append("?sysparm_exclude_reference_link=true")
                .Append("&sysparm_display_value=").Append(displayValue)
                .Append("&sysparm_limit=").Append(limit)
                .Append("&sysparm_offset=").Append(offset);
            if (Values.Get(parameters, "fields") is { } fields)
            {
                url.Append("&sysparm_fields=").Append(Uri.EscapeDataString(fields));
            }

            if (Values.Get(parameters, "query") is { } query)
            {
                url.Append("&sysparm_query=").Append(Uri.EscapeDataString(query));
            }

            return url.ToString();
        }

        private static List<JsonElement> ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : error.GetRawText();
                throw new InvalidOperationException($"ServiceNow returned an error: {message}");
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The ServiceNow response carries no 'result' array.");
            }

            var rows = new List<JsonElement>();
            foreach (var row in result.EnumerateArray())
            {
                rows.Add(row.Clone());
            }

            return rows;
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "limit") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (!int.TryParse(raw, out var size) || size <= 0)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not a positive integer.");
            }

            return size;
        }

        private static string BaseUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The ServiceNow connection needs its instance base URL as the endpoint (e.g. https://acme.service-now.com).");
            }

            // A pasted full API URL is forgiven, as the legacy validation did.
            var baseUrl = endpoint.Trim();
            var apiIndex = baseUrl.IndexOf("/api/now", StringComparison.OrdinalIgnoreCase);
            if (apiIndex > 0)
            {
                baseUrl = baseUrl[..apiIndex];
            }

            return Values.BaseUrl(baseUrl, baseUrl);
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "ServiceNow rejected the credentials (401) - check the username/password (OAuth and MFA accounts are not yet supported by this connector).",
            HttpStatusCode.Forbidden => "ServiceNow denied access (403) - the account lacks read access to the table.",
            _ => $"ServiceNow request failed with {(int)status} {status}.",
        };
    }
}
