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

namespace Stomme.Modules.Connectors.ZohoCrm
{
    /// <summary>
    /// Zoho CRM (n8n's Zoho CRM node: its accounts, contacts, deals, invoices, leads, products, purchase orders, quotes,
    /// sales orders and vendors): any module's records over Zoho CRM's REST API (<c>https://www.zohoapis.com/crm/v2</c>, or the
    /// data centre's), an OAuth client's refresh token exchanged at Zoho's accounts server (the <c>token_url</c> setting, the data
    /// centre's) through the shared <see cref="OAuth2TokenProvider"/>, the access token sent as Zoho's <c>Zoho-oauthtoken</c>. A
    /// test names the user.
    /// </summary>
    /// <remarks>
    /// A module is its API name (<c>Leads</c>, <c>Sales_Orders</c>), a record its id, a field its API name - each held to its shape
    /// before anything is sent. <c>records</c>: a module's records, the fields asked for, in an order (<c>Created_Time desc</c>),
    /// with Zoho's own parameters as name=value pairs, page by page (200 a page, or the most asked for when fewer) up to
    /// <c>max</c> (1,000) - Zoho's <c>more_records</c> ending the pages; <c>search_records</c>: by criteria
    /// (<c>(Last_Name:equals:Lovelace)</c>), an e-mail address, a phone or a word; <c>get_record</c>. Each record its fields by
    /// their API names, Zoho's own <c>$</c> fields left out; a lookup or an owner its name, with its id beside it
    /// (<c>Account_Name</c>, <c>Account_Name_id</c>); a list of values joined, of lookups their names joined; what else is not one
    /// value as its JSON. <c>create_record</c>, <c>upsert_record</c> (by the fields that find a duplicate), <c>update_record</c> -
    /// values a JSON object of API names - and <c>delete_record</c>: the record's id, and what Zoho did, said. <c>fields</c> (a
    /// module's fields: API name, label, type, whether custom or read-only) and <c>modules</c>. A throttled call waits as Zoho
    /// says; a refusal - an HTTP error or a record Zoho refused - is thrown with its code, its message and the field it names.
    /// </remarks>
    public sealed class ZohoCrmConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://www.zohoapis.com/crm/v2";

        public const string DefaultTokenUrl = "https://accounts.zoho.com/oauth/v2/token";

        public const string RecordsOperation = "records";

        public const string SearchRecordsOperation = "search_records";

        public const string GetRecordOperation = "get_record";

        public const string CreateRecordOperation = "create_record";

        public const string UpsertRecordOperation = "upsert_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const string FieldsOperation = "fields";

        public const string ModulesOperation = "modules";

        public const int DefaultMax = 1000;

        private const int PageSize = 200;

        private static readonly Regex ApiName = new("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex RecordId = new("^[0-9]{1,25}$", RegexOptions.CultureInvariant);

        private static readonly Regex QueryName = new("^[A-Za-z_][A-Za-z0-9_]{0,59}$", RegexOptions.CultureInvariant);

        // JSON as written: names, descriptions and their accents and markup, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RecordsOperation, SearchRecordsOperation, GetRecordOperation, CreateRecordOperation, UpsertRecordOperation, UpdateRecordOperation, DeleteRecordOperation, FieldsOperation, ModulesOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "zohocrm";

        public string DisplayName => "Zoho CRM";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.zoho.com/crm/");

        public Uri ApiDocumentationUrl { get; } = new("https://www.zoho.com/crm/developer/docs/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Your data centre's Zoho accounts server - https://accounts.zoho.eu/oauth/v2/token in the EU (.in, .com.au, .jp, .ca, .sa, .com.cn elsewhere); the API endpoint then https://www.zohoapis.eu/crm/v2.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField ModuleField = new("module", "Module", ConnectorConfigFieldType.Text, Required: true, Description: "Its API name - Leads, Contacts, Accounts, Deals, Sales_Orders.");

        private static readonly ConnectorConfigField IdField = new("id", "Record", ConnectorConfigFieldType.Text, Required: true, Description: "The record's id.");

        private static readonly ConnectorConfigField ValuesField = new("values", "Values", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object of API names - {\"Last_Name\": \"Lovelace\", \"Email\": \"ada@example.com\"}; a lookup {\"Account_Name\": {\"id\": \"4150868000000224005\"}}.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "A module's records, in an order - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ModuleField,
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: "API names, comma-separated - Last_Name, Email. Zoho's own when empty."),
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "A field and asc or desc - Created_Time desc."),
                    new ConnectorConfigField("query", "More", ConnectorConfigFieldType.Text, Required: false, Description: "Zoho's own parameters as name=value pairs, comma-separated - approved=false, converted=true, cvid=4150868000000087501."),
                    Max(),
                }),
            },
            new ConnectorOperation(SearchRecordsOperation, "Search records", "A module's records criteria, an address, a phone or a word find.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ModuleField,
                    new ConnectorConfigField("criteria", "Criteria", ConnectorConfigFieldType.Text, Required: false, Description: "Zoho's - (Last_Name:equals:Lovelace)and(City:starts_with:Lon)."),
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: false, Description: "Records with this address."),
                    new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "Records with this number."),
                    new ConnectorConfigField("word", "Word", ConnectorConfigFieldType.Text, Required: false, Description: "Records holding it anywhere."),
                    Max(),
                }),
            },
            new ConnectorOperation(GetRecordOperation, "Get a record", "One record - n8n's Get.") { Parameters = new ConnectorConfigSchema(new[] { ModuleField, IdField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "Adds a record - n8n's Create.") { Parameters = new ConnectorConfigSchema(new[] { ModuleField, ValuesField }) },
            new ConnectorOperation(UpsertRecordOperation, "Create or update a record", "Adds a record, or changes the one its duplicate-check fields find - n8n's Create or Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ModuleField,
                    ValuesField,
                    new ConnectorConfigField("duplicate_check_fields", "Find it by", ConnectorConfigFieldType.Text, Required: false, Description: "API names, comma-separated - Email. The module's own when empty."),
                }),
            },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "Changes a record - n8n's Update.") { Parameters = new ConnectorConfigSchema(new[] { ModuleField, IdField, ValuesField with { Description = "A JSON object of the API names that change." } }) },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "Deletes a record - n8n's Delete.") { Parameters = new ConnectorConfigSchema(new[] { ModuleField, IdField }) },
            new ConnectorOperation(FieldsOperation, "Get fields", "A module's fields - their API names, labels and types - n8n's lead Get Fields.") { Parameters = new ConnectorConfigSchema(new[] { ModuleField }) },
            new ConnectorOperation(ModulesOperation, "Get modules", "The account's modules and their API names.") { Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var users = await SendAsync(client, token, HttpMethod.Get, api + "/users?type=CurrentUser", null, cancellationToken);
                var me = (users?["users"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
                return ConnectionTestResult.Ok($"Signed in as {Text(me["full_name"]) ?? Text(me["email"]) ?? "the token's user"}.");
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
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? CreateRecordOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Zoho CRM has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var module = operation == ModulesOperation ? null
                : Values.Get(p, "module") is { } named ? ApiName.IsMatch(named) ? named : throw new InvalidOperationException($"'{named}' is not a module's API name - Leads, Sales_Orders.")
                : throw new InvalidOperationException("Give the module - its API name, Leads.");
            var id = operation is GetRecordOperation or UpdateRecordOperation or DeleteRecordOperation
                ? Values.Get(p, "id") is { } given ? RecordId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a record's id.") : throw new InvalidOperationException("Give the record's id.")
                : null;
            JsonObject? body = null;
            if (operation is CreateRecordOperation or UpsertRecordOperation or UpdateRecordOperation)
            {
                var values = ObjectOf(Values.Get(p, "values") ?? throw new InvalidOperationException("Give the values - a JSON object of API names."), "values", "{\"Last_Name\": \"Lovelace\"}");
                if (values.Count == 0)
                {
                    throw new InvalidOperationException("Give the values - a JSON object of API names.");
                }

                foreach (var (name, _) in values)
                {
                    if (!ApiName.IsMatch(name))
                    {
                        throw new InvalidOperationException($"'{name}' is not a field's API name - Last_Name.");
                    }
                }

                body = new JsonObject { ["data"] = new JsonArray(values) };
                if (operation == UpsertRecordOperation && Values.Get(p, "duplicate_check_fields") is { } check)
                {
                    body["duplicate_check_fields"] = new JsonArray(Names(check, "a field's API name").Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
                }
            }

            var query = new List<string>();
            if (operation == RecordsOperation)
            {
                if (Values.Get(p, "fields") is { } fields)
                {
                    query.Add("fields=" + Uri.EscapeDataString(string.Join(",", Names(fields, "a field's API name"))));
                }

                if (Values.Get(p, "order") is { } order)
                {
                    var parts = order.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var direction = parts.Length == 2 ? parts[1].ToLowerInvariant() : "asc";
                    query.Add(parts.Length is 1 or 2 && ApiName.IsMatch(parts[0]) && direction is "asc" or "desc"
                        ? $"sort_by={parts[0]}&sort_order={direction}"
                        : throw new InvalidOperationException($"'{order}' is not an order - a field's API name, then asc or desc: Created_Time desc."));
                }

                if (Values.Get(p, "query") is { } more)
                {
                    foreach (var pair in more.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var equals = pair.IndexOf('=');
                        var name = equals > 0 ? pair[..equals].Trim() : "";
                        query.Add(QueryName.IsMatch(name)
                            ? name + "=" + Uri.EscapeDataString(pair[(equals + 1)..].Trim())
                            : throw new InvalidOperationException($"'{pair}' is not a parameter - name=value, approved=false."));
                    }
                }
            }

            if (operation == SearchRecordsOperation)
            {
                var asked = new List<string>();
                if (Values.Get(p, "criteria") is { } criteria)
                {
                    asked.Add("criteria=" + Uri.EscapeDataString(criteria.StartsWith('(') && criteria.EndsWith(')') ? OneLine(criteria, "criteria") : throw new InvalidOperationException($"'{criteria}' are not criteria - (Last_Name:equals:Lovelace).")));
                }

                if (Values.Get(p, "email") is { } email)
                {
                    asked.Add("email=" + Uri.EscapeDataString(IsAddress(email) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail address.")));
                }

                if (Values.Get(p, "phone") is { } phone)
                {
                    asked.Add("phone=" + Uri.EscapeDataString(OneLine(phone, "phone")));
                }

                if (Values.Get(p, "word") is { } word)
                {
                    asked.Add("word=" + Uri.EscapeDataString(OneLine(word, "word")));
                }

                query.AddRange(asked.Count == 1 ? asked : throw new InvalidOperationException("Give one of criteria, an e-mail address, a phone or a word."));
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (operation is RecordsOperation or SearchRecordsOperation)
            {
                // A page's size stays the same from page to page: Zoho counts its pages by it.
                var size = Math.Min(PageSize, max);
                var path = $"{api}/{module}{(operation == SearchRecordsOperation ? "/search" : "")}?{string.Join("&", query.Append("per_page=" + size.ToString(CultureInfo.InvariantCulture)))}";
                var read = 0;
                for (var page = 1; ; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{path}&page={page.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
                    var items = answer?["data"] as JsonArray ?? new JsonArray();
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    if (items.Count == 0 || answer?["info"]?["more_records"] is not JsonValue more || !more.TryGetValue<bool>(out var hasMore) || !hasMore)
                    {
                        yield break;
                    }
                }
            }

            if (operation == GetRecordOperation)
            {
                var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/{module}/{id}", null, cancellationToken);
                yield return Record((answer?["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException($"No {module} record has the id {id}."));
                yield break;
            }

            if (operation is FieldsOperation or ModulesOperation)
            {
                var fields = operation == FieldsOperation;
                var answer = await SendAsync(client, token, HttpMethod.Get, fields ? $"{api}/settings/fields?module={module}" : $"{api}/settings/modules", null, cancellationToken);
                var read = 0;
                foreach (var item in (answer?[fields ? "fields" : "modules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return new ConnectorRecord(fields
                        ? new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["api_name"] = Text(item["api_name"]),
                            ["label"] = Text(item["field_label"]),
                            ["type"] = Text(item["data_type"]),
                            ["custom"] = Flag(item["custom_field"]),
                            ["read_only"] = Flag(item["read_only"]),
                        }
                        : new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["api_name"] = Text(item["api_name"]),
                            ["name"] = Text(item["plural_label"]) ?? Text(item["module_name"]),
                            ["singular"] = Text(item["singular_label"]),
                            ["api_supported"] = Flag(item["api_supported"]),
                            ["creatable"] = Flag(item["creatable"]),
                        });
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                yield break;
            }

            var (method, url) = operation switch
            {
                CreateRecordOperation => (HttpMethod.Post, $"{api}/{module}"),
                UpsertRecordOperation => (HttpMethod.Post, $"{api}/{module}/upsert"),
                UpdateRecordOperation => (HttpMethod.Put, $"{api}/{module}/{id}"),
                _ => (HttpMethod.Delete, $"{api}/{module}/{id}"),
            };
            var written = await SendAsync(client, token, method, url, body, cancellationToken);
            var result = (written?["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
            if (Text(result["status"]) == "error")
            {
                throw new InvalidOperationException(Refusal(result));
            }

            var details = result["details"] as JsonObject ?? new JsonObject();
            var outcome = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Text(details["id"]) ?? id,
                ["status"] = Text(result["code"]) ?? Text(result["status"]),
            };
            if (Text(result["action"]) is { } action)
            {
                outcome["action"] = action; // an upsert's: insert or update
            }

            if (Text(details["Created_Time"]) is { } created)
            {
                outcome["created"] = created;
            }

            if (Text(details["Modified_Time"]) is { } modified)
            {
                outcome["modified"] = modified;
            }

            yield return new ConnectorRecord(outcome);
        }

        // A record's fields by their API names, Zoho's own $ fields left out: a lookup its name and its id beside it, a list of
        // values or of lookups joined, what else is not one value as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in item)
            {
                if (name.StartsWith('$'))
                {
                    continue;
                }

                switch (value)
                {
                    case null:
                        fields[name] = null;
                        break;
                    case JsonValue single:
                        fields[name] = Scalar(single);
                        break;
                    case JsonObject lookup when lookup["id"] is JsonValue:
                        fields[name] = Text(lookup["name"]) ?? Text(lookup["id"]);
                        fields[name + "_id"] = Text(lookup["id"]);
                        break;
                    case JsonArray list when list.All(entry => entry is JsonValue):
                        fields[name] = string.Join(", ", list.Select(entry => Scalar((JsonValue)entry!)?.ToString()));
                        break;
                    case JsonArray list when list.Count > 0 && list.All(entry => entry is JsonObject named && named["name"] is JsonValue && named.Count <= 2):
                        fields[name] = string.Join(", ", list.Select(entry => Text(entry!["name"])));
                        break;
                    default:
                        fields[name] = value.ToJsonString(Wire);
                        break;
                }
            }

            return new ConnectorRecord(fields);
        }

        private static object? Scalar(JsonValue value) =>
            value.TryGetValue<string>(out var text) ? text
            : value.TryGetValue<bool>(out var flag) ? flag
            : value.TryGetValue<long>(out var whole) ? whole
            : value.TryGetValue<double>(out var real) ? real
            : value.ToJsonString();

        private static string Refusal(JsonObject refused)
        {
            var field = Text(refused["details"]?["api_name"]);
            return $"Zoho CRM refused it: {Text(refused["code"]) ?? "error"} - {Text(refused["message"]) ?? "no reason given"}{(field is null ? "" : $" ({field})")}.";
        }

        private static List<string> Names(string given, string what) =>
            given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(name => ApiName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not {what} - Last_Name."))
                .ToList();

        private static JsonObject ObjectOf(string given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} are not a JSON object - {example}.");
        }

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

        private static string? Text(JsonNode? node) => node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null
            : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Zoho's answer as JSON (an empty answer - its 204 for nothing found - nothing), the token in its Zoho-oauthtoken scheme, a
        // throttled call waiting as Zoho says; a failure thrown with its status, Zoho's code, its message and the field it names.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("Authorization", "Zoho-oauthtoken " + token);
                request.Headers.Accept.ParseAdd("application/json");
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
                throw new InvalidOperationException($"Zoho CRM answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var refused = (json?["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? json as JsonObject;
                var code = Text(refused?["code"]);
                var said = Text(refused?["message"]);
                var field = Text(refused?["details"]?["api_name"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}{(field is null ? "" : $" ({field})")}");
            }

            return json;
        }
    }
}
