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

namespace Stomme.Modules.Connectors.HaloPsa
{
    /// <summary>
    /// HaloPSA (n8n's HaloPSA node: a service desk's clients, their sites and users, and its tickets): Halo's resource
    /// server with an API application's client credentials at Halo's token endpoint through the family's token provider, over
    /// the host's egress. A test counts the desk's tickets.
    /// </summary>
    /// <remarks>
    /// Clients, sites, tickets and users: <c>records</c> - page by page, a short page or Halo's count the end, up to <c>max</c>
    /// (1,000), by Halo's search, active or inactive ones alone, and Halo's own query parameters; <c>record</c> - by its id;
    /// <c>create_record</c> - a JSON object of fields by Halo's names, <b>never with an id</b> (Halo would update that record);
    /// <c>update_record</c> - <b>the record got first, so an update never creates one</b>, then its fields changed and the rest
    /// kept; <c>delete_record</c>. A password is never a value - set in Halo. A record is its fields as Halo gives them, a list
    /// or an object as its JSON. Every resource, id, filter and value held to its shape before a token is asked for; a success
    /// answer that is not JSON, or none where a record is due, a failure that says so. A throttled call waits as Halo says; a
    /// failure is thrown with its status and Halo's message.
    /// </remarks>
    public sealed class HaloPsaConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string RecordOperation = "record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const int DefaultMax = 1000;

        /// <summary>n8n's scope for its HaloPSA credential.</summary>
        public const string DefaultScope = "admin edit:tickets edit:customers";

        private const int PageSize = 100;

        private static readonly string[] Known = { RecordsOperation, RecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation };

        // Each resource with its path on Halo's resource server and the key Halo lists it under.
        private static readonly Dictionary<string, (string Path, string Key)> Resources = new(StringComparer.Ordinal)
        {
            ["clients"] = ("Client", "clients"), ["sites"] = ("Site", "sites"), ["tickets"] = ("Tickets", "tickets"), ["users"] = ("Users", "users"),
        };

        // The query parameters the step sets itself.
        private static readonly string[] Paging = { "pageinate", "page_size", "page_no", "count", "search", "includeactive", "includeinactive" };

        private static readonly Regex Id = new("^[0-9]{1,10}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z_][a-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x1F]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "halopsa";

        public string DisplayName => "HaloPSA";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => "https://kafe.halopsa.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Client id (Configuration > Integrations > HaloPSA API > View Applications > the application)",
            ["client-secret"] = "Client secret (the application's Client Credentials)",
            ["token-url"] = "Token endpoint - https://kafe.halopsa.com/auth/token (Halo's hosted: ...?tenant=kafe)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri WebsiteUrl { get; } = new("https://halopsa.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://halopsa.com/apidoc/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false, Description: "The token's scope - n8n's, or Halo's own names: all, read:tickets, edit:customers.") { Default = DefaultScope },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ResourceField = Field("resource", "Resource", "clients, sites, tickets or users.", required: true);

        private static readonly ConnectorConfigField IdField = Field("id", "Record", "Its id - a number.", required: true);

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of fields by Halo's names - {\"summary\": \"Kassan är nere\", \"details\": \"Sedan nio\", \"tickettype_id\": 1}.", ConnectorConfigFieldType.Multiline, required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "n8n's HaloPSA Get Many - clients, sites, tickets or users.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ResourceField,
                    Field("search", "Search", "Halo's search - the text to filter by."),
                    Field("status", "Status", "active or inactive alone - all when left empty."),
                    Field("filters", "Filters", "A JSON object of Halo's query parameters - {\"client_id\": 12, \"open_only\": true}."),
                    Field("max", "At most", "How many, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(RecordOperation, "Get a record", "n8n's HaloPSA Get.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "n8n's HaloPSA Create.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, ValuesField }) },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Its fields changed, the rest kept, never creating one - n8n's HaloPSA Update.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField, ValuesField }) },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "n8n's HaloPSA Delete.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = ApiOf(context.Endpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/Tickets?pageinate=true&page_size=1&page_no=1", null, cancellationToken);
                return ConnectionTestResult.Ok((answer as JsonObject)?["record_count"] is JsonValue count && count.TryGetValue<long>(out var tickets)
                    ? $"HaloPSA: signed in - {tickets.ToString(CultureInfo.InvariantCulture)} ticket{(tickets == 1 ? "" : "s")}."
                    : "HaloPSA: signed in.");
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
                ?? throw new InvalidOperationException($"HaloPSA has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var api = ApiOf(context.Endpoint);
            var resource = Values.Get(p, "resource") is { } named
                ? Resources.ContainsKey(named) ? named : throw new InvalidOperationException($"'{named}' is not one of HaloPSA's resources - clients, sites, tickets or users (resource).")
                : throw new InvalidOperationException("Give the resource - clients, sites, tickets or users (resource).");
            var (path, key) = Resources[resource];
            var id = operation is RecordsOperation or CreateRecordOperation ? null
                : Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's id - a number (id).") : throw new InvalidOperationException("Give the record's id (id).");
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = operation == RecordsOperation ? QueryOf(p) : null;
            var body = operation is CreateRecordOperation or UpdateRecordOperation ? ValuesOf(p) : null;

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation == RecordsOperation)
            {
                var size = Math.Min(PageSize, max);
                var read = 0;
                for (var page = 1; ; page++)
                {
                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/{path}?pageinate=true&page_size={size.ToString(CultureInfo.InvariantCulture)}&page_no={page.ToString(CultureInfo.InvariantCulture)}{query}", null, cancellationToken);
                    var items = ((answer as JsonObject)?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    // A short (or empty) page, or Halo's count reached, ends the scan.
                    if (items.Count < size || (answer as JsonObject)?["record_count"] is JsonValue count && count.TryGetValue<long>(out var total) && read >= total)
                    {
                        yield break;
                    }
                }
            }

            var one = $"{api}/{path}/{id}";
            if (operation == DeleteRecordOperation)
            {
                await SendAsync(client, token, HttpMethod.Delete, one, null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
                yield break;
            }

            if (operation == UpdateRecordOperation)
            {
                // Halo posts an update to the collection, and a post naming an id it does not hold may create one: the record
                // is got first, and a record it does not hold is a failure with nothing written.
                _ = await SendAsync(client, token, HttpMethod.Get, one, null, cancellationToken) as JsonObject ?? throw new InvalidOperationException($"HaloPSA answered with no record {id} - nothing was written.");
                body!.Insert(0, "id", long.Parse(id!, CultureInfo.InvariantCulture));
            }

            var result = operation == RecordOperation
                ? await SendAsync(client, token, HttpMethod.Get, one, null, cancellationToken)
                : await SendAsync(client, token, HttpMethod.Post, $"{api}/{path}", new JsonArray(body), cancellationToken);
            yield return Record((result is JsonArray list ? list.Count == 1 ? list[0] : null : result) as JsonObject ?? throw new InvalidOperationException($"HaloPSA answered with no {key[..^1]}."));
        }

        // Halo's resource server: its web application's address with /api (the address alone forgiven).
        private static string ApiOf(string? endpoint)
        {
            var given = (endpoint ?? "").Trim().TrimEnd('/');
            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(given, out var uri) && uri.Scheme is "https" or "http" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
                && (uri.AbsolutePath is "/" or "" || uri.AbsolutePath.Equals("/api", StringComparison.OrdinalIgnoreCase))
                ? uri.GetLeftPart(UriPartial.Authority) + "/api"
                : throw new InvalidOperationException("Give HaloPSA's address - https://kafe.halopsa.com (its resource server, /api, forgiven).");
        }

        // Get Many's query: Halo's search, active or inactive alone, and Halo's own parameters - each escaped.
        private static string QueryOf(IReadOnlyDictionary<string, string> parameters)
        {
            var query = new StringBuilder();
            if (Values.Get(parameters, "search") is { } search)
            {
                query.Append("&search=").Append(Text.IsMatch(search) ? Uri.EscapeDataString(search) : throw new InvalidOperationException($"'{search}' is not a search of 512 characters at most, without control characters (search)."));
            }

            switch (Values.Get(parameters, "status")?.ToLowerInvariant())
            {
                case null or "all":
                    break;
                case "active":
                    query.Append("&includeactive=true&includeinactive=false");
                    break;
                case "inactive":
                    query.Append("&includeactive=false&includeinactive=true");
                    break;
                default:
                    throw new InvalidOperationException($"'{Values.Get(parameters, "status")}' is not active, inactive or all (status).");
            }

            if (Values.Get(parameters, "filters") is not { } given)
            {
                return query.ToString();
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

            if (parsed is not JsonObject filters)
            {
                throw new InvalidOperationException("The filters are not a JSON object of Halo's query parameters - {\"client_id\": 12} (filters).");
            }

            foreach (var (name, value) in filters)
            {
                if (!FieldName.IsMatch(name) || Paging.Contains(name))
                {
                    throw new InvalidOperationException(Paging.Contains(name) ? $"'{name}' is set by the step - give search, status or max instead (filters)." : $"'{name}' is not one of Halo's query parameters - lower-case letters, digits and _ (filters).");
                }

                var text = value is JsonValue scalar ? scalar.GetValueKind() switch
                {
                    JsonValueKind.String => scalar.GetValue<string>(),
                    JsonValueKind.Number => scalar.ToJsonString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                } : null;
                if (text is null || !Text.IsMatch(text))
                {
                    throw new InvalidOperationException($"The filter '{name}' is not a text, a number or true or false (filters).");
                }

                query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(text));
            }

            return query.ToString();
        }

        // A record's values: a JSON object of fields by Halo's names - never its id, never a password.
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
                throw new InvalidOperationException("The values are not a JSON object of fields - {\"summary\": \"Kassan är nere\"} (values).");
            }

            foreach (var (name, _) in values)
            {
                if (!FieldName.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a field's name - lower-case letters, digits and _ (values).");
                }

                if (name == "id")
                {
                    throw new InvalidOperationException("A record's id is never among its values - Halo would update that record; give it as the id of an update (values).");
                }

                if (name.Contains("password", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A password is never a step's value - set it in HaloPSA (values).");
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

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give Halo's token endpoint - https://kafe.halopsa.com/auth/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Halo's answer as JSON, a throttled call waiting as Halo says; a failure thrown with its status and Halo's message;
        // a success that is not JSON a failure that says so (an empty answer - nothing).
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
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
                throw new InvalidOperationException($"HaloPSA answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Words(json) ?? (json is JsonObject error ? Words(error["message"]) ?? Words(error["Message"]) ?? Words(error["error_description"]) ?? Words(error["error"]) : null);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : " " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
