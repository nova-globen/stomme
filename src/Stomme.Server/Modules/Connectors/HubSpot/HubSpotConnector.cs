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

namespace Stomme.Modules.Connectors.HubSpot
{
    /// <summary>
    /// HubSpot (n8n's HubSpot node: contacts, companies, deals and tickets got, searched, created, updated and upserted):
    /// the CRM's objects over HubSpot's API (<c>https://api.hubapi.com</c>), a private app's access token as the Bearer token. A
    /// test asks HubSpot which account the token is (<c>account-info/v3/details</c>).
    /// </summary>
    /// <remarks>
    /// <c>search</c>: an object's records - those HubSpot's search finds (<c>query</c>, its free text) and those <c>filters</c> keep
    /// (one <c>property = value</c> per line, all of them) - the <c>properties</c> named (or HubSpot's defaults), every page of 100
    /// followed by its cursor, at most <c>max</c>; one record each: <c>id</c>, <c>created_at</c>, <c>updated_at</c>,
    /// <c>archived</c> and each property by its name. <c>create</c>: <c>properties</c>, a record or a list as JSON, a record each;
    /// <c>update</c>: <c>records</c>, each its <c>id</c> and the properties to change; <c>upsert</c>: <c>records</c>, each found by
    /// <c>id_property</c> (a contact's email by default, n8n's "create or update") and created when none is - all sent in
    /// HubSpot's batches of 100, each answering the records HubSpot kept. The object is one of HubSpot's (or a custom one's
    /// <c>p_</c>name or id), a record id its digits, both held to that shape before they reach the path. A throttled call waits as
    /// HubSpot says; a failure is thrown with its status and HubSpot's category (<c>HTTP 400 VALIDATION_ERROR.</c>), the port
    /// answering it in its words.
    /// </remarks>
    public sealed class HubSpotConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.hubapi.com";

        public const string SearchOperation = "search";

        public const string CreateOperation = "create";

        public const string UpdateOperation = "update";

        public const string UpsertOperation = "upsert";

        /// <summary>The most records HubSpot takes in one batch, and in one page of a search.</summary>
        public const int Batch = 100;

        public const int DefaultMax = 1000;

        private static readonly Regex ObjectType = new("^(contacts|companies|deals|tickets|products|line_items|quotes|calls|emails|meetings|notes|tasks|p_[a-z0-9_]{1,100}|[0-9]-[0-9]{1,12})$", RegexOptions.CultureInvariant);

        private static readonly Regex RecordId = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex PropertyName = new("^[a-z0-9_]{1,100}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "hubspot";

        public string DisplayName => "HubSpot";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as HubSpot names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Private app access token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.hubspot.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.hubspot.com/docs/api/crm/understanding-the-crm");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField ObjectField = new("object", "Object", ConnectorConfigFieldType.Text, Required: false, Description: "contacts, companies, deals, tickets - or a custom object's p_name.") { Default = "contacts" };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SearchOperation, "Search records", "An object's records - those a search finds and filters keep - one record each: its id, times and properties - n8n's Get Many and Search.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "HubSpot's free-text search - a name, an email, a domain."),
                    new ConnectorConfigField("filters", "Filters", ConnectorConfigFieldType.Multiline, Required: false, Description: "One 'property = value' per line; a record must match them all - lifecyclestage = customer."),
                    new ConnectorConfigField("properties", "Properties", ConnectorConfigFieldType.Text, Required: false, Description: "The properties to read, comma-separated. Empty: HubSpot's defaults."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many records, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(CreateOperation, "Create records", "Creates a record, or each of a list, from its properties - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("properties", "Properties", ConnectorConfigFieldType.Multiline, Required: true, Description: "A record's properties as JSON, or a list - {\"email\": \"ada@example.com\", \"firstname\": \"Ada\"}."),
                }),
            },
            new ConnectorOperation(UpdateOperation, "Update records", "Changes properties of records named by their ids - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("records", "Records", ConnectorConfigFieldType.Multiline, Required: true, Description: "A record, or a list, each its id and the properties to change - {\"id\": \"123\", \"lifecyclestage\": \"customer\"}."),
                }),
            },
            new ConnectorOperation(UpsertOperation, "Create or update records", "Updates the record a property finds - a contact's email - or creates it when none is - n8n's Create or Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ObjectField,
                    new ConnectorConfigField("id_property", "Found by", ConnectorConfigFieldType.Text, Required: false, Description: "The unique property a record is found by.") { Default = "email" },
                    new ConnectorConfigField("records", "Records", ConnectorConfigFieldType.Multiline, Required: true, Description: "A record, or a list, each with that property and the rest to set - {\"email\": \"ada@example.com\", \"firstname\": \"Ada\"}."),
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
                var account = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, baseUrl + "/account-info/v3/details", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in to account {account["portalId"]?.ToString() ?? "of the token"}.");
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
            var objectType = Values.Get(context.Parameters, "object") ?? "contacts";
            if (!ObjectType.IsMatch(objectType))
            {
                throw new InvalidOperationException($"'{objectType}' is not a HubSpot object - contacts, companies, deals, tickets, or a custom object's p_name.");
            }

            var objects = $"{Values.BaseUrl(context.Endpoint, DefaultEndpoint)}/crm/v3/objects/{objectType}";
            var operation = context.OperationName ?? SearchOperation;
            if (!string.Equals(operation, SearchOperation, StringComparison.OrdinalIgnoreCase))
            {
                var (path, inputs) = Inputs(operation, context.Parameters); // read and checked before anything is sent
                var token = Token(context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                for (var start = 0; start < inputs.Count; start += Batch)
                {
                    var answer = await SendAsync(client, token, HttpMethod.Post, $"{objects}/batch/{path}", new JsonObject
                    {
                        ["inputs"] = new JsonArray(inputs.Skip(start).Take(Batch).Select(input => (JsonNode?)input).ToArray()),
                    }, cancellationToken);
                    foreach (var kept in (answer["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        yield return Record(kept);
                    }
                }

                yield break;
            }

            var filters = Filters(Values.Get(context.Parameters, "filters"));
            var properties = (Values.Get(context.Parameters, "properties") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => PropertyName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a HubSpot property's name - lower-case letters, digits and _."))
                .ToList();
            var max = int.TryParse(Values.Get(context.Parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) && given > 0 ? given : DefaultMax;
            var query = Values.Get(context.Parameters, "query");
            var accessToken = Token(context.Credentials);
            using var http = context.Egress.CreateHttpClient(context.Timeout);
            var read = 0;
            string? after = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new JsonObject { ["limit"] = Math.Min(Batch, max - read) };
                if (query is not null)
                {
                    body["query"] = query;
                }

                if (filters.Count > 0)
                {
                    body["filterGroups"] = new JsonArray(new JsonObject { ["filters"] = new JsonArray(filters.Select(filter => (JsonNode?)filter.DeepClone()).ToArray()) });
                }

                if (properties.Count > 0)
                {
                    body["properties"] = new JsonArray(properties.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
                }

                if (after is not null)
                {
                    body["after"] = after;
                }

                var page = await SendAsync(http, accessToken, HttpMethod.Post, $"{objects}/search", body, cancellationToken);
                foreach (var found in (page["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (read >= max)
                    {
                        break;
                    }

                    read++;
                    yield return Record(found);
                }

                after = read < max ? page["paging"]?["next"]?["after"]?.GetValue<string>() : null;
            }
            while (!string.IsNullOrEmpty(after));
        }

        // A search's filters: one 'property = value' per line, each HubSpot's EQ filter.
        private static List<JsonObject> Filters(string? given) =>
            (given ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
            {
                var at = line.IndexOf('=', StringComparison.Ordinal);
                var name = at > 0 ? line[..at].Trim() : "";
                if (!PropertyName.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{line}' is not 'property = value' - a property's name, lower-case.");
                }

                return new JsonObject { ["propertyName"] = name, ["operator"] = "EQ", ["value"] = line[(at + 1)..].Trim() };
            }).ToList();

        // What a create, an update or an upsert sends, each record as HubSpot's batch takes it; refused before anything is sent.
        private static (string Path, List<JsonObject> Inputs) Inputs(string operation, IReadOnlyDictionary<string, string> parameters)
        {
            if (string.Equals(operation, CreateOperation, StringComparison.OrdinalIgnoreCase))
            {
                return ("create", Records(Values.Get(parameters, "properties"), "properties").Select(record => new JsonObject { ["properties"] = Properties(record, null) }).ToList());
            }

            if (string.Equals(operation, UpdateOperation, StringComparison.OrdinalIgnoreCase))
            {
                return ("update", Records(Values.Get(parameters, "records"), "records").Select(record =>
                {
                    var id = Text(record["id"]);
                    return id is not null && RecordId.IsMatch(id)
                        ? new JsonObject { ["id"] = id, ["properties"] = Properties(record, "id") }
                        : throw new InvalidOperationException($"'{id}' is not a HubSpot record id - each record to update names its own.");
                }).ToList());
            }

            if (string.Equals(operation, UpsertOperation, StringComparison.OrdinalIgnoreCase))
            {
                var idProperty = Values.Get(parameters, "id_property") ?? "email";
                if (!PropertyName.IsMatch(idProperty))
                {
                    throw new InvalidOperationException($"'{idProperty}' is not a HubSpot property's name.");
                }

                return ("upsert", Records(Values.Get(parameters, "records"), "records").Select(record => Text(record[idProperty]) is { Length: > 0 } key
                    ? new JsonObject { ["idProperty"] = idProperty, ["id"] = key, ["properties"] = Properties(record, null) }
                    : throw new InvalidOperationException($"A record has no {idProperty} - each is found by it.")).ToList());
            }

            throw new InvalidOperationException($"HubSpot has no operation '{operation}' - search, create, update or upsert.");
        }

        // A record's properties as HubSpot takes them: each a string (a number or a true written as text), a null clearing it.
        private static JsonObject Properties(JsonObject record, string? skip)
        {
            var properties = new JsonObject();
            foreach (var (name, value) in record)
            {
                if (name == skip)
                {
                    continue;
                }

                if (!PropertyName.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a HubSpot property's name - lower-case letters, digits and _.");
                }

                properties[name] = value is null ? "" : Text(value);
            }

            return properties;
        }

        private static string? Text(JsonNode? value) => value switch
        {
            null => null,
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
            JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            _ => value.ToJsonString(),
        };

        private static List<JsonObject> Records(string? given, string what)
        {
            if (string.IsNullOrWhiteSpace(given))
            {
                throw new InvalidOperationException($"Give the {what} - a record or a list of records as JSON.");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {what} are not JSON - a record or a list of records.");
            }

            return parsed switch
            {
                JsonObject one => new List<JsonObject> { one },
                JsonArray many when many.All(item => item is JsonObject) => many.Cast<JsonObject>().ToList(),
                _ => throw new InvalidOperationException($"The {what} are not a record or a list of records."),
            };
        }

        // A record as the pipeline reads it: its id and times, then each property by its name.
        private static ConnectorRecord Record(JsonObject found)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = found["id"]?.GetValue<string>(),
                ["created_at"] = found["createdAt"]?.GetValue<string>(),
                ["updated_at"] = found["updatedAt"]?.GetValue<string>(),
                ["archived"] = found["archived"]?.GetValue<bool>() ?? false,
            };
            foreach (var (name, value) in found["properties"] as JsonObject ?? new JsonObject())
            {
                fields[name] = Text(value);
            }

            return new ConnectorRecord(fields);
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "token") ?? throw new InvalidOperationException("The connection has no access token.");

        // HubSpot's answer as JSON, a throttled call waiting as HubSpot says; a failure thrown with its status and HubSpot's category.
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
                throw new InvalidOperationException($"HubSpot answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var category = json?["category"] is JsonValue word && word.TryGetValue<string>(out var text) ? text : null;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(category is null ? "" : " " + category)}.");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
