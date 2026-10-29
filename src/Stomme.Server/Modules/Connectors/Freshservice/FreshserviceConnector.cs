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

namespace Stomme.Modules.Connectors.Freshservice
{
    /// <summary>
    /// Freshservice (n8n's Freshservice node: an IT service desk's tickets, changes, problems, releases, assets and the
    /// people and groups around them): a desk's API v2 with an API key as HTTP Basic, over the host's egress. A test names the
    /// desk's latest ticket.
    /// </summary>
    /// <remarks>
    /// Any of the desk's resources n8n's node knows - agents, groups, roles, announcements, asset types, changes, departments,
    /// locations, problems, products, releases, requesters, requester groups, applications (n8n's software), tickets - and its
    /// assets: <c>records</c> - page by page, a short page the end, up to <c>max</c> (1,000), tickets, agents and requesters by
    /// Freshservice's query language too; <c>record</c> - by its id; <c>create_record</c> and <c>update_record</c> - a JSON
    /// object of fields by Freshservice's names, the record answered as the desk keeps it now (roles read only);
    /// <c>delete_record</c>. A record is its fields as Freshservice gives them, a list or an object as its JSON. Every resource,
    /// id, query and value held to its shape before anything is sent. A throttled call waits as Freshservice says; a failure is
    /// thrown with its status, Freshservice's description and the fields it names.
    /// </remarks>
    public sealed class FreshserviceConnector : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string RecordOperation = "record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly string[] Known = { RecordsOperation, RecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation };

        // Each resource with the key Freshservice wraps one of its records in.
        private static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
        {
            ["agents"] = "agent", ["groups"] = "group", ["roles"] = "role", ["announcements"] = "announcement", ["asset_types"] = "asset_type",
            ["changes"] = "change", ["departments"] = "department", ["locations"] = "location", ["problems"] = "problem", ["products"] = "product",
            ["releases"] = "release", ["requesters"] = "requester", ["requester_groups"] = "requester_group", ["applications"] = "application",
            ["tickets"] = "ticket", ["assets"] = "asset",
        };

        private static readonly string[] Queried = { "tickets", "agents", "requesters" };

        private static readonly Regex Id = new("^[0-9]{1,19}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z][a-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex Query = new("^[^\\x00-\\x1F\"]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "freshservice";

        public string DisplayName => "Freshservice";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://kafe.freshservice.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api-key"] = "API key (Profile settings > Your API key)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri WebsiteUrl { get; } = new("https://www.freshworks.com/freshservice/");

        public Uri ApiDocumentationUrl { get; } = new("https://api.freshservice.com/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ResourceField = Field("resource", "Resource", "tickets, changes, problems, releases, assets, agents, requesters, groups, departments, locations, products, asset_types, applications, announcements, requester_groups or roles.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Record", "Its id - a number.", required: true);

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of fields by Freshservice's names - {\"subject\": \"Kassan är nere\", \"priority\": 2, \"email\": \"ada@kafe.example.com\"}.", ConnectorConfigFieldType.Multiline, required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "n8n's Freshservice Get Many - any of its resources.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ResourceField,
                    Field("query", "Query", "Tickets, agents and requesters: Freshservice's query - priority:3 AND status:2."),
                    Field("max", "At most", "How many, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(RecordOperation, "Get a record", "n8n's Freshservice Get - any of its resources.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "n8n's Freshservice Create - any of its resources but roles.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, ValuesField }) },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Its fields changed, the rest kept - n8n's Freshservice Update.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField, ValuesField }) },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "n8n's Freshservice Delete - any of its resources but roles.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = ApiOf(context.Endpoint);
                var authorization = AuthorizationOf(context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var latest = ((await SendAsync(client, authorization, HttpMethod.Get, api + "/tickets?per_page=1", null, cancellationToken))?["tickets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                return ConnectionTestResult.Ok(latest is null ? "Freshservice: signed in - no ticket yet." : $"Freshservice: signed in - the latest ticket is #{latest["id"]?.ToJsonString()}.");
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
                ?? throw new InvalidOperationException($"Freshservice has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var api = ApiOf(context.Endpoint);
            var authorization = AuthorizationOf(context.Credentials);
            var resource = Values.Get(p, "resource") is { } named
                ? Resources.ContainsKey(named) ? named : throw new InvalidOperationException($"'{named}' is not one of Freshservice's resources - tickets, changes, problems, releases, assets, agents, requesters, groups, departments, locations, products, asset_types, applications, announcements, requester_groups or roles (resource).")
                : throw new InvalidOperationException("Give the resource - tickets, agents, changes (resource).");
            if (resource == "roles" && operation is CreateRecordOperation or UpdateRecordOperation or DeleteRecordOperation)
            {
                throw new InvalidOperationException("Freshservice's roles are read only - get them or one of them.");
            }

            var id = operation is RecordsOperation or CreateRecordOperation ? null
                : Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's id - a number (id).") : throw new InvalidOperationException("Give the record's id (id).");
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = operation == RecordsOperation && Values.Get(p, "query") is { } asked
                ? !Queried.Contains(resource) ? throw new InvalidOperationException($"Freshservice takes a query for tickets, agents and requesters alone - not {resource} (query).")
                : Query.IsMatch(asked) ? asked : throw new InvalidOperationException($"'{asked}' is not a query of 512 characters at most, without quotes or control characters (query).")
                : null;
            var body = operation is CreateRecordOperation or UpdateRecordOperation ? ValuesOf(p) : null;

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation == RecordsOperation)
            {
                var path = query is null ? $"{api}/{resource}?" : resource == "tickets" ? $"{api}/tickets/filter?query={Uri.EscapeDataString("\"" + query + "\"")}&" : $"{api}/{resource}?query={Uri.EscapeDataString("\"" + query + "\"")}&";
                var size = Math.Min(PageSize, max);
                var read = 0;
                for (var page = 1; ; page++)
                {
                    var answer = await SendAsync(client, authorization, HttpMethod.Get, $"{path}page={page.ToString(CultureInfo.InvariantCulture)}&per_page={size.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
                    var items = (answer?[resource] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    // A short (or empty) page ends the scan.
                    if (items.Count < size)
                    {
                        yield break;
                    }
                }
            }

            var method = operation switch
            {
                RecordOperation => HttpMethod.Get,
                CreateRecordOperation => HttpMethod.Post,
                UpdateRecordOperation => HttpMethod.Put,
                _ => HttpMethod.Delete,
            };
            var result = await SendAsync(client, authorization, method, $"{api}/{resource}{(id is null ? "" : "/" + id)}", body, cancellationToken);
            if (operation == DeleteRecordOperation)
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
                yield break;
            }

            yield return Record(result?[Resources[resource]] as JsonObject ?? throw new InvalidOperationException($"Freshservice answered with no {Resources[resource].Replace('_', ' ')}."));
        }

        // The desk's address: https://{domain}.freshservice.com (or a pasted API address, forgiven) - its API v2.
        private static string ApiOf(string? endpoint)
        {
            var given = (endpoint ?? "").Trim();
            var at = given.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                given = given[..at];
            }

            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(given.TrimEnd('/'), out var uri) && uri.Scheme is "https" or "http" && uri.AbsolutePath == "/" && uri.Query.Length == 0
                ? uri.GetLeftPart(UriPartial.Authority) + "/api/v2"
                : throw new InvalidOperationException("Give the desk's address - https://kafe.freshservice.com.");
        }

        private static AuthenticationHeaderValue AuthorizationOf(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "api-key") is { } key && key.Length > 0 && !key.Contains(':')
                ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":X")))
                : throw new InvalidOperationException("Give the API key (api-key) - Profile settings > Your API key.");

        // A record's values: a JSON object of fields by Freshservice's names.
        private static JsonObject ValuesOf(IReadOnlyDictionary<string, string> parameters)
        {
            JsonNode? parsed;
            try
            {
                parsed = Values.Get(parameters, "values") is { } given ? JsonNode.Parse(given) : throw new InvalidOperationException("Give the values - a JSON object of fields (values).");
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonObject values || values.Count == 0)
            {
                throw new InvalidOperationException("The values are not a JSON object of fields - {\"subject\": \"Kassan är nere\"} (values).");
            }

            foreach (var (name, _) in values)
            {
                if (!FieldName.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a field's name - lower-case letters, digits and _ (values).");
                }
            }

            return values;
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        // Freshservice's answer as JSON, a throttled call waiting as it says; a failure thrown with its status, Freshservice's
        // description and the fields it names; a success that is not JSON a failure that says so (a delete's 204 - nothing).
        private static async Task<JsonNode?> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
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
                throw new InvalidOperationException($"Freshservice answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Words(json?["description"]) ?? Words(json?["message"]);
                var fields = (json?["errors"] as JsonArray)?.OfType<JsonObject>().Select(error => string.Join(": ", new[] { Words(error["field"]), Words(error["message"]) }.Where(part => !string.IsNullOrEmpty(part)))).Where(part => part.Length > 0).ToList() ?? new List<string>();
                var text = string.Join(" - ", new[] { said, fields.Count == 0 ? null : string.Join("; ", fields) }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }
    }
}
