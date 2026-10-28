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

namespace Stomme.Modules.Connectors.Salesforce
{
    /// <summary>
    /// Salesforce (BL-229, n8n's Salesforce node: records of any object searched by SOQL, got, created, updated, upserted and
    /// deleted): an org's REST API at its own address (<c>https://your-domain.my.salesforce.com</c>), a connected app's client id
    /// and secret by the OAuth2 CLIENT-CREDENTIALS scheme (Salesforce's client credentials flow, its token endpoint the org's own),
    /// through the shared <see cref="OAuth2TokenProvider"/>. A test reads the org's limits.
    /// </summary>
    /// <remarks>
    /// An object is named by its API name (Account, Contact, Lead, Opportunity, Case, or a custom one ending in __c) and a record by
    /// its id (15 or 18 letters and digits), an external id field by its API name - each held to its shape before it reaches the
    /// path. <c>query</c>: what a SOQL query finds, page by page by Salesforce's <c>nextRecordsUrl</c> - followed only while it stays
    /// the org's own query path - up to <c>max</c>; one record each, Salesforce's <c>attributes</c> dropped and a related record's
    /// fields flattened (<c>Account.Name</c>). <c>get</c>: one record, the <c>fields</c> named or every one. <c>create</c>: a record of
    /// an object from <c>fields</c> (Salesforce's JSON) - its id; <c>update</c> changes those given; <c>upsert</c> creates or updates
    /// by an external id field's value - its id and whether it was created; <c>delete</c> removes one. The API version is the
    /// <c>api_version</c> setting. A throttled call waits as Salesforce says; a failure is thrown with its status and Salesforce's
    /// first error.
    /// </remarks>
    public sealed class SalesforceConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultApiVersion = "62.0";

        public const string QueryOperation = "query";

        public const string GetOperation = "get";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        public const string UpsertOperation = "upsert";

        public const string DeleteOperation = "delete";

        public const int DefaultMax = 2000;

        private static readonly Regex ObjectName = new("^[A-Za-z][A-Za-z0-9_]{0,79}$", RegexOptions.CultureInvariant);

        private static readonly Regex RecordId = new("^([A-Za-z0-9]{15}|[A-Za-z0-9]{18})$", RegexOptions.CultureInvariant);

        private static readonly Regex ApiVersion = new("^[0-9]{2,3}\\.0$", RegexOptions.CultureInvariant);

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "salesforce";

        public string DisplayName => "Salesforce";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        /// <summary>The client-credentials scheme as Salesforce names its parts.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Consumer key", ["client-secret"] = "Consumer secret", ["token-url"] = "Token endpoint (https://your-domain.my.salesforce.com/services/oauth2/token)",
        };

        public string EndpointExample => "https://your-domain.my.salesforce.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.salesforce.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.salesforce.com/docs/atlas.en-us.api_rest.meta/api_rest/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_version", "API version", ConnectorConfigFieldType.Text, Required: false, Description: "Salesforce's REST API version - 62.0.") { Default = DefaultApiVersion },
        });

        private static readonly ConnectorConfigField ObjectField = new("object", "Object", ConnectorConfigFieldType.Text, Required: true, Description: "Its API name - Account, Contact, Lead, Opportunity, Case, or a custom one (Invoice__c).");

        private static readonly ConnectorConfigField IdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "The record's id (15 or 18 letters and digits).");

        private static readonly ConnectorConfigField FieldsField = new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: true, Description: "Salesforce's own JSON - {\"LastName\": \"Lovelace\", \"Company\": \"Acme\"}.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(QueryOperation, "Search by SOQL", "What a SOQL query finds - one record each, a related record's fields flattened (Account.Name) - n8n's search and Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("soql", "SOQL", ConnectorConfigFieldType.Text, Required: true, Description: "SELECT Id, Name, Account.Name FROM Contact WHERE Email != null"),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many records, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetOperation, "Get a record", "One record - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField, new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: "The fields to read, comma-separated. Empty: every one.") }),
            },
            new ConnectorOperation(CreateOperation, "Create a record", "Adds a record of an object - one record: its id - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, FieldsField }),
            },
            new ConnectorOperation(UpdateOperation, "Update a record", "Changes a record's fields - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField, FieldsField }),
            },
            new ConnectorOperation(UpsertOperation, "Create or update a record", "Creates a record, or updates the one an external id field's value names - its id and whether it was created - n8n's Create or Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("external_id_field", "External id field", ConnectorConfigFieldType.Text, Required: true, Description: "Its API name - Email__c, or an external id field."),
                    new ConnectorConfigField("external_id", "External id", ConnectorConfigFieldType.Text, Required: true, Description: "Its value for this record."),
                    FieldsField,
                }),
            },
            new ConnectorOperation(DeleteOperation, "Delete a record", "Removes a record - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ObjectField, IdField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint, context.Settings);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                await SendAsync(client, token, HttpMethod.Get, api + "/limits", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in to {new Uri(api).Host}.");
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
            var api = Api(context.Endpoint, context.Settings);
            var operation = context.OperationName ?? QueryOperation;
            var parameters = context.Parameters;

            // What could be refused is refused before a token is asked for.
            string? soql = null, objectName = null, id = null, upsertPath = null, named = "";
            JsonObject? fields = null;
            if (Is(operation, QueryOperation))
            {
                soql = Values.Get(parameters, "soql") ?? throw new InvalidOperationException("Give the SOQL query.");
            }
            else if (Is(operation, GetOperation) || Is(operation, CreateOperation) || Is(operation, UpdateOperation) || Is(operation, UpsertOperation) || Is(operation, DeleteOperation))
            {
                objectName = Name(Values.Get(parameters, "object"), "object");
                if (Is(operation, GetOperation) || Is(operation, UpdateOperation) || Is(operation, DeleteOperation))
                {
                    id = Values.Get(parameters, "id") is { } given && RecordId.IsMatch(given) ? given : throw new InvalidOperationException($"'{Values.Get(parameters, "id")}' is not a Salesforce record's id (15 or 18 letters and digits).");
                }

                if (Is(operation, GetOperation) && Values.Get(parameters, "fields") is { } list)
                {
                    named = "?fields=" + string.Join(",", list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(field => Name(field, "field")));
                }

                if (Is(operation, CreateOperation) || Is(operation, UpdateOperation) || Is(operation, UpsertOperation))
                {
                    fields = Fields(Values.Get(parameters, "fields"));
                }

                if (Is(operation, UpsertOperation))
                {
                    var field = Name(Values.Get(parameters, "external_id_field"), "external id field");
                    var value = Values.Get(parameters, "external_id") ?? throw new InvalidOperationException("Give the external id's value.");
                    upsertPath = $"{api}/sobjects/{objectName}/{field}/{Uri.EscapeDataString(value)}";
                }
            }
            else
            {
                throw new InvalidOperationException($"Salesforce has no operation '{operation}' - query, get, create, update, upsert or delete.");
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (soql is not null)
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var origin = new Uri(api).GetLeftPart(UriPartial.Authority);
                var queryPath = new Uri(api).AbsolutePath + "/query/";
                var read = 0;
                string? next = $"{api}/query?q={Uri.EscapeDataString(soql)}";
                while (next is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await SendAsync(client, token, HttpMethod.Get, next, null, cancellationToken);
                    foreach (var record in (page?["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (read >= max)
                        {
                            yield break;
                        }

                        read++;
                        yield return Record(record);
                    }

                    // Salesforce's next page - a path of the org's own query resource, the token going nowhere else.
                    var more = page?["done"] is JsonValue done && done.TryGetValue<bool>(out var finished) && !finished;
                    next = more && read < max && Text(page?["nextRecordsUrl"]) is { } nextPath && nextPath.StartsWith(queryPath, StringComparison.Ordinal) ? origin + nextPath : null;
                }

                yield break;
            }

            if (Is(operation, GetOperation))
            {
                var found = await SendAsync(client, token, HttpMethod.Get, $"{api}/sobjects/{objectName}/{id}{named}", null, cancellationToken);
                yield return Record(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, CreateOperation))
            {
                var made = await SendAsync(client, token, HttpMethod.Post, $"{api}/sobjects/{objectName}", fields, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Text(made?["id"]), ["object"] = objectName });
                yield break;
            }

            if (Is(operation, UpdateOperation))
            {
                await SendAsync(client, token, HttpMethod.Patch, $"{api}/sobjects/{objectName}/{id}", fields, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["object"] = objectName, ["updated"] = string.Join(", ", fields!.Select(field => field.Key)) });
                yield break;
            }

            if (Is(operation, UpsertOperation))
            {
                var answer = await SendAsync(client, token, HttpMethod.Patch, upsertPath!, fields, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Text(answer?["id"]),
                    ["object"] = objectName,
                    ["created"] = answer?["created"] is JsonValue created && created.TryGetValue<bool>(out var isNew) && isNew,
                });
                yield break;
            }

            await SendAsync(client, token, HttpMethod.Delete, $"{api}/sobjects/{objectName}/{id}", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["object"] = objectName, ["deleted"] = true });
        }

        // A record as Salesforce answers it, its attributes dropped and a related record's fields flattened under its name.
        private static ConnectorRecord Record(JsonObject record)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            Flatten(record, "", fields);
            return new ConnectorRecord(fields);
        }

        private static void Flatten(JsonObject record, string prefix, Dictionary<string, object?> fields)
        {
            foreach (var (name, value) in record)
            {
                if (name == "attributes")
                {
                    continue;
                }

                switch (value)
                {
                    case JsonObject related when related["attributes"] is not null:
                        Flatten(related, prefix + name + ".", fields);
                        break;
                    case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                        fields[prefix + name] = text;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<long>(out var whole):
                        fields[prefix + name] = whole;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<double>(out var real):
                        fields[prefix + name] = real;
                        break;
                    case JsonValue scalar when scalar.TryGetValue<bool>(out var flag):
                        fields[prefix + name] = flag;
                        break;
                    case null:
                        fields[prefix + name] = null;
                        break;
                    default:
                        fields[prefix + name] = value.ToJsonString();
                        break;
                }
            }
        }

        // A record's fields, Salesforce's own JSON, each field's API name held to its shape.
        private static JsonObject Fields(string? given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given ?? throw new InvalidOperationException("Give the record's fields - Salesforce's JSON."));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The fields are not JSON - {\"LastName\": \"Lovelace\"}.");
            }

            var fields = parsed as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object.");
            if (fields.Count == 0)
            {
                throw new InvalidOperationException("Give at least one field.");
            }

            foreach (var (name, _) in fields)
            {
                Name(name, "field");
            }

            return fields;
        }

        private static string Name(string? given, string what) =>
            given is not null && ObjectName.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Salesforce {what}'s API name.");

        // The org's REST API: its own address, /services/data/v{version}.
        private static string Api(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var org) || (org.Scheme != Uri.UriSchemeHttps && org.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not an org's address - https://your-domain.my.salesforce.com.");
            }

            var version = Values.GetOrDefault(settings, "api_version", DefaultApiVersion).TrimStart('v', 'V');
            return ApiVersion.IsMatch(version)
                ? $"{org.GetLeftPart(UriPartial.Authority)}/services/data/v{version}"
                : throw new InvalidOperationException($"'{version}' is not an API version - 62.0.");
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                credentials.TryGetValue("client-secret", out var secret) ? secret : string.Empty,
                string.Empty) { Timeout = timeout }, cancellationToken);

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Salesforce's answer as JSON (an empty answer null), a throttled call waiting as Salesforce says; a failure thrown with its
        // status and Salesforce's first error.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                throw new InvalidOperationException($"Salesforce answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var first = (json as JsonArray)?.FirstOrDefault() as JsonObject;
                var said = first is null ? null : $"{Text(first["errorCode"])}: {Text(first["message"])}";
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : " " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
