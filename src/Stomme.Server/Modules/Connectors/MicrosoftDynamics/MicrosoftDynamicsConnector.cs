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

namespace Stomme.Modules.Connectors.MicrosoftDynamics
{
    /// <summary>
    /// Microsoft Dynamics 365 (BL-229, n8n's Microsoft Dynamics CRM node: an environment's accounts - and here any table): the
    /// Dataverse Web API v9.2 of an environment with an application user's client credentials - the tenant's token endpoint,
    /// the environment's own scope - through the family's token provider, over the host's egress. A test names the application
    /// user the environment knows.
    /// </summary>
    /// <remarks>
    /// <c>records</c> - a table by its entity set name (accounts, contacts, leads, opportunities, a custom one), by an OData
    /// filter, its columns, related records and order, page by page by the environment's next link - followed only while it
    /// stays on the same table and names another page - up to <c>max</c> (1,000); <c>record</c> - by its id; <c>create_record</c>
    /// and <c>update_record</c> - a JSON object of columns and values (a lookup bound by <c>column@odata.bind</c>), the record
    /// answered as the environment keeps it now, an update never creating one; <c>delete_record</c>. A record is its fields as
    /// the environment gives them, a list or an object as its JSON, the OData annotations left out. Every table, id, column and
    /// value held to its shape before a token is asked for. A throttled call waits as the environment says; a failure is thrown
    /// with its status and the environment's code and message.
    /// </remarks>
    public sealed class MicrosoftDynamicsConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string RecordOperation = "record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const int DefaultMax = 1000;

        private const int PageSize = 500;

        private static readonly string[] Known = { RecordsOperation, RecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation };

        private static readonly Regex Table = new("^[a-z][a-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex Id = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex Column = new("^[a-z][a-z0-9_]{0,99}(@odata\\.bind)?$", RegexOptions.CultureInvariant);

        private static readonly Regex Columns = new("^[a-z][a-z0-9_]{0,99}(,[a-z][a-z0-9_]{0,99})*$", RegexOptions.CultureInvariant);

        private static readonly Regex Order = new("^[a-z][a-z0-9_]{0,99}( (asc|desc))?(,[a-z][a-z0-9_]{0,99}( (asc|desc))?)*$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsoftdynamics";

        public string DisplayName => "Microsoft Dynamics 365";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => "https://kafe.crm4.dynamics.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) id (Entra ID > App registrations > the app - an application user of the environment)",
            ["client-secret"] = "Client secret (Certificates & secrets)",
            ["token-url"] = "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm, ConnectorCategory.MicrosoftCloudDirectory };

        public Uri WebsiteUrl { get; } = new("https://dynamics.microsoft.com");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/power-apps/developer/data-platform/webapi/overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false, Description: "The token's scope - the environment's own (https://kafe.crm4.dynamics.com/.default) when left empty."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField TableField = Field("table", "Table", "Its entity set name - accounts, contacts, leads, opportunities.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Record", "Its id - a GUID.", required: true);

        private static readonly ConnectorConfigField SelectField = Field("select", "Columns", "Comma-separated column names - name,telephone1; every one when left empty.");

        private static readonly ConnectorConfigField ExpandField = Field("expand", "Related", "Related records to include - primarycontactid($select=fullname).");

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of columns and values - {\"name\": \"Kafé AB\", \"primarycontactid@odata.bind\": \"/contacts(00000000-0000-0000-0000-000000000001)\"}.", ConnectorConfigFieldType.Multiline);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "n8n's Microsoft Dynamics CRM Account Get Many - any table.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TableField, Field("filter", "Filter", "An OData filter - statecode eq 0 and contains(name,'Kafé')."), SelectField, ExpandField,
                    Field("order_by", "Order", "Comma-separated columns, each asc or desc - name asc,createdon desc."),
                    Field("max", "At most", "How many, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(RecordOperation, "Get a record", "n8n's Microsoft Dynamics CRM Account Get - any table.") { Parameters = new ConnectorConfigSchema(new[] { TableField, IdField, SelectField, ExpandField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "n8n's Microsoft Dynamics CRM Account Create - any table.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, ValuesField with { Required = true }, SelectField }),
            },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Its columns changed, the rest kept - never a new record - n8n's Microsoft Dynamics CRM Account Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TableField, IdField, ValuesField with { Required = true }, SelectField }),
            },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "n8n's Microsoft Dynamics CRM Account Delete - any table.") { Parameters = new ConnectorConfigSchema(new[] { TableField, IdField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var environment = EnvironmentOf(context.Endpoint);
                var token = await AcquireTokenAsync(context.Egress, environment, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var who = await SendAsync(client, token, HttpMethod.Get, environment + "/api/data/v9.2/WhoAmI", null, null, cancellationToken);
                return ConnectionTestResult.Ok($"Microsoft Dynamics 365: signed in as the application user {Words(who?["UserId"]) ?? "?"}.");
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
                ?? throw new InvalidOperationException($"Microsoft Dynamics 365 has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var environment = EnvironmentOf(context.Endpoint);
            var table = Values.Get(p, "table") is { } named ? Table.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a table's entity set name - accounts, contacts (table).") : throw new InvalidOperationException("Give the table - its entity set name (table).");
            var id = operation is RecordsOperation or CreateRecordOperation ? null
                : Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's id - a GUID (id).") : throw new InvalidOperationException("Give the record's id (id).");
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = new List<string>();
            if (Values.Get(p, "select") is { } select && operation != DeleteRecordOperation)
            {
                var columns = select.Replace(" ", "", StringComparison.Ordinal);
                query.Add("$select=" + (Columns.IsMatch(columns) ? columns : throw new InvalidOperationException($"'{select}' is not a comma-separated list of column names (select).")));
            }

            if (operation is RecordsOperation or RecordOperation && Values.Get(p, "expand") is { } expand)
            {
                query.Add("$expand=" + Uri.EscapeDataString(Text.IsMatch(expand) ? expand : throw new InvalidOperationException($"'{expand}' is not a text without control characters (expand).")));
            }

            if (operation == RecordsOperation)
            {
                if (Values.Get(p, "filter") is { } filter)
                {
                    query.Add("$filter=" + Uri.EscapeDataString(Text.IsMatch(filter) ? filter : throw new InvalidOperationException($"'{filter}' is not a text without control characters (filter).")));
                }

                if (Values.Get(p, "order_by") is { } order)
                {
                    query.Add("$orderby=" + Uri.EscapeDataString(Order.IsMatch(order) ? order : throw new InvalidOperationException($"'{order}' is not a comma-separated list of columns, each asc or desc (order_by).")));
                }
            }

            JsonObject? body = operation is CreateRecordOperation or UpdateRecordOperation ? ValuesOf(p) : null;
            var api = environment + "/api/data/v9.2";
            var address = $"{api}/{table}{(id is null ? "" : $"({id})")}{(query.Count == 0 ? "" : "?" + string.Join("&", query))}";

            var token = await AcquireTokenAsync(context.Egress, environment, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation == RecordsOperation)
            {
                // The environment pages by its next link: followed only while it stays on the same table and names another page.
                var start = $"{api}/{table}";
                var read = 0;
                string? next = address;
                while (next is not null)
                {
                    var answer = await SendAsync(client, token, HttpMethod.Get, next, null, $"odata.maxpagesize={Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture)}", cancellationToken);
                    foreach (var item in (answer?["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    var link = answer?["@odata.nextLink"] is JsonValue value && value.TryGetValue<string>(out var linked) ? linked : null;
                    if (link is not null && !(link.StartsWith(start + "?", StringComparison.OrdinalIgnoreCase) && link != next))
                    {
                        throw new InvalidOperationException("Microsoft Dynamics 365 named a next page outside the table - it is not followed.");
                    }

                    next = link;
                }

                yield break;
            }

            var method = operation switch
            {
                RecordOperation => HttpMethod.Get,
                CreateRecordOperation => HttpMethod.Post,
                UpdateRecordOperation => HttpMethod.Patch,
                _ => HttpMethod.Delete,
            };
            var result = await SendAsync(client, token, method, address, body, body is null ? null : "return=representation", cancellationToken);
            if (operation == DeleteRecordOperation)
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["deleted"] = true });
                yield break;
            }

            yield return Record(result as JsonObject ?? throw new InvalidOperationException("Microsoft Dynamics 365 answered with no record."));
        }

        // The environment's address: https, a host and nothing after it but a pasted API path, forgiven.
        private static string EnvironmentOf(string? endpoint)
        {
            var given = (endpoint ?? "").Trim();
            var at = given.IndexOf("/api/data", StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                given = given[..at];
            }

            return Uri.TryCreate(given.TrimEnd('/'), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.AbsolutePath == "/" && uri.Query.Length == 0
                ? uri.GetLeftPart(UriPartial.Authority)
                : throw new InvalidOperationException("Give the environment's address - https://kafe.crm4.dynamics.com (Power Platform admin center > the environment > Environment URL).");
        }

        // A record's values: a JSON object of columns - a lookup bound by column@odata.bind - and values: a text, a number, true,
        // false or null each.
        private static JsonObject ValuesOf(IReadOnlyDictionary<string, string> parameters)
        {
            JsonNode? parsed;
            try
            {
                parsed = Values.Get(parameters, "values") is { } given ? JsonNode.Parse(given) : throw new InvalidOperationException("Give the values - a JSON object of columns and values (values).");
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonObject values || values.Count == 0)
            {
                throw new InvalidOperationException("The values are not a JSON object of columns and values - {\"name\": \"Kafé AB\"} (values).");
            }

            var body = new JsonObject();
            foreach (var (column, value) in values)
            {
                if (!Column.IsMatch(column))
                {
                    throw new InvalidOperationException($"'{column}' is not a column's name - lower-case letters, digits and _, or a lookup's column@odata.bind (values).");
                }

                body[column] = value switch
                {
                    null => null,
                    JsonValue text when text.TryGetValue<string>(out var s) => Text.IsMatch(s) || s.Length == 0 ? s : throw new InvalidOperationException($"The value of '{column}' has a control character (values)."),
                    JsonValue other when other.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => other.DeepClone(),
                    _ => throw new InvalidOperationException($"The value of '{column}' is not a text, a number, true, false or null (values)."),
                };

                if (column.EndsWith("@odata.bind", StringComparison.Ordinal) && !(body[column] is JsonValue bound && bound.TryGetValue<string>(out var reference) && Regex.IsMatch(reference, "^/[a-z][a-z0-9_]{0,99}\\([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\)$")))
                {
                    throw new InvalidOperationException($"The value of '{column}' is not a record's reference - /contacts(00000000-0000-0000-0000-000000000001) (values).");
                }
            }

            return body;
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => !pair.Key.StartsWith("@odata.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Words(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, string environment, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give the tenant's token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.Get(settings, "scope") ?? environment + "/.default")
            { Timeout = timeout }, cancellationToken);

        // The environment's answer as JSON, a throttled call waiting as it says; a failure thrown with its status and its code and
        // message; a success that is not JSON a failure that says so (an empty one - a delete's 204 - nothing).
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, string? prefer, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
                request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
                if (prefer is not null)
                {
                    request.Headers.TryAddWithoutValidation("Prefer", prefer);
                }

                if (method == HttpMethod.Patch)
                {
                    // An update, never an upsert: a record that is not there is a failure, not a new one.
                    request.Headers.TryAddWithoutValidation("If-Match", "*");
                }

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
                throw new InvalidOperationException($"Microsoft Dynamics 365 answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Words(json?["error"]?["code"]);
                var said = Words(json?["error"]?["message"]);
                var text = string.Join(": ", new[] { code, said }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }
    }
}
