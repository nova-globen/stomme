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

namespace Stomme.Modules.Connectors.Odoo
{
    /// <summary>
    /// Odoo (BL-229, n8n's Odoo node: any model's records created, got, read, updated and deleted - its contacts, opportunities,
    /// notes and activities among them): an Odoo instance's models over either of its APIs - JSON-RPC (<c>/jsonrpc</c>, Odoo 14 and
    /// later: a login and a password or an API key, the user's id asked for first) or JSON-2 (<c>/json/2/{model}/{method}</c>, Odoo
    /// 19 and later: an API key as a bearer) - the connection's <c>api</c> setting choosing; the database named by the
    /// <c>database</c> setting or, as Odoo Online names it, by the first label of the instance's host. A test counts the contacts.
    /// </summary>
    /// <remarks>
    /// A model is its technical name (<c>res.partner</c>), a record its id (or ids, comma-separated), a method its name - each held
    /// to its shape. <c>records</c>: a model's records an Odoo domain keeps (a JSON list - <c>[["is_company", "=", true]]</c>), in
    /// an order, page by page (100 a page) up to <c>max</c> (1,000); <c>get_records</c>; one record each: its fields - a link to
    /// another record its id with its name beside it (<c>country_id</c>, <c>country_id_name</c>), a list of links its ids. The
    /// fields read are <c>fields</c> (comma-separated; Odoo's defaults when empty). <c>create_record</c>, <c>update_records</c>:
    /// values as a JSON object; <c>delete_records</c>. <c>models</c>: the models (a search in their names); <c>fields</c>: a model's
    /// fields, their labels, types and whether required. <c>call</c>: any public method of a model, its named arguments a JSON
    /// object (<c>ids</c> the records it runs on). Odoo's refusal is thrown in its words - its exception and its message.
    /// </remarks>
    public sealed class OdooConnector : IConnector, IConnectorExtractor
    {
        public const string RecordsOperation = "records";

        public const string GetRecordsOperation = "get_records";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordsOperation = "update_records";

        public const string DeleteRecordsOperation = "delete_records";

        public const string ModelsOperation = "models";

        public const string FieldsOperation = "fields";

        public const string CallOperation = "call";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Model = new("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)*$", RegexOptions.CultureInvariant);

        private static readonly Regex Method = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly Regex Field = new("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly Regex Order = new("^[a-z_][a-z0-9_.]*( (asc|desc))?(, ?[a-z_][a-z0-9_.]*( (asc|desc))?)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex DatabaseName = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);

        // JSON as written: values, their accents and markup, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { RecordsOperation, GetRecordsOperation, CreateRecordOperation, UpdateRecordsOperation, DeleteRecordsOperation, ModelsOperation, FieldsOperation, CallOperation };

        public string ConnectorType => "odoo";

        public string DisplayName => "Odoo";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Login (JSON-RPC)", ["password"] = "Password or API key" };

        public string EndpointExample => "https://mycompany.odoo.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm, ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://www.odoo.com");

        public Uri ApiDocumentationUrl { get; } = new("https://www.odoo.com/documentation/19.0/developer/reference/external_api.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api", "API", ConnectorConfigFieldType.Text, Required: false,
                Description: "jsonrpc - Odoo 14 and later, a login and a password or an API key - or json2 - Odoo 19 and later, an API key alone.") { Default = "jsonrpc" },
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: false,
                Description: "The database's name. Empty: the first label of the instance's host - mycompany for mycompany.odoo.com."),
        });

        private static readonly ConnectorConfigField ModelField = new("model", "Model", ConnectorConfigFieldType.Text, Required: true, Description: "A model's technical name - res.partner, crm.lead, sale.order.");

        private static readonly ConnectorConfigField IdsField = new("ids", "Records", ConnectorConfigFieldType.Text, Required: true, Description: "A record's id, or ids comma-separated.");

        private static readonly ConnectorConfigField FieldsField = new("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: "Fields to read, comma-separated. Empty: Odoo's defaults.");

        private static readonly ConnectorConfigField ValuesField = new("values", "Values", ConnectorConfigFieldType.Multiline, Required: true, Description: "A JSON object of field values - {\"name\": \"Ada\", \"email\": \"ada@example.com\"}.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "A model's records a domain keeps, in an order - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ModelField,
                    new ConnectorConfigField("domain", "Domain", ConnectorConfigFieldType.Multiline, Required: false, Description: "An Odoo domain as JSON - [[\"is_company\", \"=\", true]]. Empty: every record."),
                    FieldsField,
                    new ConnectorConfigField("order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "name asc, create_date desc."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetRecordsOperation, "Get records by id", "Records by their ids - n8n's Get.") { Parameters = new ConnectorConfigSchema(new[] { ModelField, IdsField, FieldsField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "Makes a record - n8n's Create.") { Parameters = new ConnectorConfigSchema(new[] { ModelField, ValuesField }) },
            new ConnectorOperation(UpdateRecordsOperation, "Update records", "Changes records' fields - n8n's Update.") { Parameters = new ConnectorConfigSchema(new[] { ModelField, IdsField, ValuesField }) },
            new ConnectorOperation(DeleteRecordsOperation, "Delete records", "Deletes records - n8n's Delete.") { Parameters = new ConnectorConfigSchema(new[] { ModelField, IdsField }) },
            new ConnectorOperation(ModelsOperation, "Get models", "The instance's models - their technical names and labels.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Words in a model's name or label.") }),
            },
            new ConnectorOperation(FieldsOperation, "Get fields", "A model's fields - their names, labels and types, and whether required.") { Parameters = new ConnectorConfigSchema(new[] { ModelField }) },
            new ConnectorOperation(CallOperation, "Call a method", "Runs a model's public method - action_confirm, message_post... - its named arguments a JSON object.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ModelField,
                    new ConnectorConfigField("method", "Method", ConnectorConfigFieldType.Text, Required: true, Description: "action_confirm."),
                    new ConnectorConfigField("arguments", "Arguments", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of named arguments, ids the records it runs on - {\"ids\": [7]}."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var odoo = Session.Open(context.Endpoint, context.Settings, context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var count = await odoo.CallAsync(client, "res.partner", "search_count", new JsonObject { ["domain"] = new JsonArray() }, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in to {odoo.Database} - {(count is JsonValue value && value.TryGetValue<long>(out var n) ? n : 0).ToString(CultureInfo.InvariantCulture)} contacts.");
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
            var odoo = Session.Open(context.Endpoint, context.Settings, context.Credentials);
            var operation = context.OperationName ?? RecordsOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Odoo has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before anything is sent.
            var model = Is(operation, ModelsOperation) ? "ir.model" : Values.Get(parameters, "model") is { } named && Model.IsMatch(named) ? named : throw new InvalidOperationException($"'{Values.Get(parameters, "model")}' is not a model - res.partner, crm.lead.");
            var ids = Is(operation, GetRecordsOperation) || Is(operation, UpdateRecordsOperation) || Is(operation, DeleteRecordsOperation) ? Ids(Values.Get(parameters, "ids")) : null;
            var fields = Values.Get(parameters, "fields") is { } wanted ? FieldList(wanted) : new JsonArray();
            var values = Is(operation, CreateRecordOperation) || Is(operation, UpdateRecordsOperation) ? JsonObjectOf(Values.Get(parameters, "values"), "values", "{\"name\": \"Ada\"}") : null;
            if (values is { Count: 0 })
            {
                throw new InvalidOperationException("Give at least one field's value.");
            }

            var domain = Is(operation, RecordsOperation) ? Values.Get(parameters, "domain") is { } given ? DomainOf(given) : new JsonArray() : null;
            var order = Values.Get(parameters, "order") is { } sorted ? Order.IsMatch(sorted) ? sorted : throw new InvalidOperationException($"'{sorted}' is not an order - name asc, create_date desc.") : null;
            var max = Values.Get(parameters, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var method = Is(operation, CallOperation) ? Values.Get(parameters, "method") is { } call && Method.IsMatch(call) ? call : throw new InvalidOperationException($"'{Values.Get(parameters, "method")}' is not a public method's name - action_confirm.") : null;
            var arguments = Is(operation, CallOperation) ? Values.Get(parameters, "arguments") is { } args ? JsonObjectOf(args, "arguments", "{\"ids\": [7]}") : new JsonObject() : null;

            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, RecordsOperation) || Is(operation, ModelsOperation))
            {
                if (Is(operation, ModelsOperation))
                {
                    domain = Values.Get(parameters, "query") is { } query
                        ? new JsonArray("|", new JsonArray("model", "ilike", query), new JsonArray("name", "ilike", query))
                        : new JsonArray();
                    fields = new JsonArray("model", "name");
                    order = "model";
                }

                var read = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(PageSize, max - read);
                    var body = new JsonObject { ["domain"] = domain!.DeepClone(), ["fields"] = fields.DeepClone(), ["offset"] = read, ["limit"] = size };
                    if (order is not null)
                    {
                        body["order"] = order;
                    }

                    var page = await odoo.CallAsync(client, model, "search_read", body, cancellationToken) as JsonArray ?? new JsonArray();
                    foreach (var record in page.OfType<JsonObject>())
                    {
                        yield return Is(operation, ModelsOperation)
                            ? new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = Text(record["model"]), ["name"] = Text(record["name"]) })
                            : RecordOf(record);
                        if (++read >= max)
                        {
                            yield break; // never past the most asked for, whatever a page holds
                        }
                    }

                    if (page.Count < size)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, GetRecordsOperation))
            {
                var records = await odoo.CallAsync(client, model, "read", new JsonObject { ["ids"] = ids, ["fields"] = fields }, cancellationToken) as JsonArray ?? new JsonArray();
                foreach (var record in records.OfType<JsonObject>())
                {
                    yield return RecordOf(record);
                }

                yield break;
            }

            if (Is(operation, CreateRecordOperation))
            {
                var made = await odoo.CallAsync(client, model, "create", new JsonObject { ["vals_list"] = new JsonArray(values) }, cancellationToken);
                var id = made is JsonArray list ? list.FirstOrDefault() : made;
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id is JsonValue number && number.TryGetValue<long>(out var created) ? created : null });
                yield break;
            }

            if (Is(operation, UpdateRecordsOperation) || Is(operation, DeleteRecordsOperation))
            {
                var update = Is(operation, UpdateRecordsOperation);
                await odoo.CallAsync(client, model, update ? "write" : "unlink", update ? new JsonObject { ["ids"] = ids, ["vals"] = values } : new JsonObject { ["ids"] = ids }, cancellationToken);
                foreach (var id in ids!.Select(id => id!.GetValue<long>()))
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, [update ? "updated" : "deleted"] = true });
                }

                yield break;
            }

            if (Is(operation, FieldsOperation))
            {
                var described = await odoo.CallAsync(client, model, "fields_get", new JsonObject { ["attributes"] = new JsonArray("string", "type", "required", "readonly", "relation") }, cancellationToken) as JsonObject ?? new JsonObject();
                foreach (var (name, about) in described.OrderBy(field => field.Key, StringComparer.Ordinal))
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = name,
                        ["label"] = Text(about?["string"]),
                        ["type"] = Text(about?["type"]),
                        ["required"] = about?["required"] is JsonValue required && required.TryGetValue<bool>(out var must) && must,
                        ["readonly"] = about?["readonly"] is JsonValue readOnly && readOnly.TryGetValue<bool>(out var fixedValue) && fixedValue,
                        ["relation"] = Text(about?["relation"]),
                    });
                }

                yield break;
            }

            var answer = await odoo.CallAsync(client, model, method!, arguments!, cancellationToken);
            yield return answer is JsonObject one ? RecordOf(one) : new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = Plain(answer) });
        }

        // A record as a record here: a link to another record its id with its name beside it, a list of links its ids, the rest as it is.
        private static ConnectorRecord RecordOf(JsonObject record)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in record)
            {
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue id && id.TryGetValue<long>(out var linked) && Text(link[1]) is { } shown)
                {
                    fields[name] = linked;
                    fields[name + "_name"] = shown;
                }
                else if (value is JsonArray list && list.All(item => item is JsonValue number && number.TryGetValue<long>(out _)))
                {
                    fields[name] = string.Join(", ", list.Select(item => item!.GetValue<long>().ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    fields[name] = Plain(value);
                }
            }

            return new ConnectorRecord(fields);
        }

        private static object? Plain(JsonNode? value) => value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            _ => value.ToJsonString(Wire),
        };

        private static JsonArray Ids(string? given)
        {
            var ids = new JsonArray();
            foreach (var part in (given ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                ids.Add(long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{part}' is not a record's id."));
            }

            return ids.Count > 0 ? ids : throw new InvalidOperationException("Give the records' ids - 7, or 7, 8, 9.");
        }

        private static JsonArray FieldList(string given)
        {
            var fields = new JsonArray();
            foreach (var field in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                fields.Add(Field.IsMatch(field) ? field : throw new InvalidOperationException($"'{field}' is not a field's name."));
            }

            return fields;
        }

        // An Odoo domain: a JSON list of conditions ([field, operator, value]) and the operators &, | and ! between them.
        private static JsonArray DomainOf(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is JsonArray domain && domain.All(term => term is JsonArray { Count: 3 } condition && Text(condition[0]) is { } field && Field.IsMatch(field.Split('.')[0]) && Text(condition[1]) is not null
                || Text(term) is "&" or "|" or "!"))
            {
                return domain;
            }

            throw new InvalidOperationException($"'{given}' is not an Odoo domain - [[\"is_company\", \"=\", true], [\"name\", \"ilike\", \"Ada\"]].");
        }

        private static JsonObject JsonObjectOf(string? given, string what, string example)
        {
            JsonNode? parsed = null;
            try
            {
                parsed = given is null ? null : JsonNode.Parse(given);
            }
            catch (JsonException)
            {
            }

            return parsed as JsonObject ?? throw new InvalidOperationException($"The {what} are not a JSON object - {example}.");
        }

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        /// <summary>One conversation with an instance over the API the connection names: its address, database and sign-in.</summary>
        private sealed class Session
        {
            private string api = "";
            private string url = "";
            private string login = "";
            private string secret = "";
            private long? uid;

            public string Database { get; private set; } = "";

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> credentials)
            {
                var url = Values.BaseUrl(endpoint, "");
                var api = (Values.Get(settings, "api") ?? "jsonrpc").ToLowerInvariant();
                if (api is not "jsonrpc" and not "json2")
                {
                    throw new InvalidOperationException($"'{Values.Get(settings, "api")}' is not an Odoo API - jsonrpc or json2.");
                }

                var database = Values.Get(settings, "database") ?? new Uri(url).Host.Split('.')[0];
                if (!DatabaseName.IsMatch(database))
                {
                    throw new InvalidOperationException($"'{database}' is not a database's name.");
                }

                var secret = Values.Get(credentials, "password") ?? throw new InvalidOperationException("Give the connection's password or API key.");
                var login = Values.Get(credentials, "username") ?? "";
                if (api == "jsonrpc" && login.Length == 0)
                {
                    throw new InvalidOperationException("Give the connection's login - JSON-RPC signs in with it.");
                }

                return new Session { api = api, url = url, login = login, secret = secret, Database = database };
            }

            // A model's method, its named arguments: JSON-2 takes them as they are; JSON-RPC as execute_kw's arguments - the records
            // it runs on (or a create's values, a domain) first, the rest by name - after the user's id is asked for.
            public async Task<JsonNode?> CallAsync(HttpClient client, string model, string method, JsonObject arguments, CancellationToken cancellationToken)
            {
                if (api == "json2")
                {
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/json/2/{model}/{method}") { Content = new StringContent(arguments.ToJsonString(Wire), Encoding.UTF8, "application/json") };
                        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", secret);
                        request.Headers.Add("X-Odoo-Database", Database);
                        return request;
                    }, cancellationToken);
                    var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                    var json = Parse(payload, response);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(Text(json?["name"]) is { } name ? " " + name : "")}{(Text(json?["message"]) is { } said ? ": " + Short(said) : ".")}");
                    }

                    return json;
                }

                uid ??= await RpcAsync(client, "common", "login", new JsonArray(Database, login, secret), cancellationToken) is JsonValue id && id.TryGetValue<long>(out var user) && user > 0
                    ? user
                    : throw new InvalidOperationException($"Odoo refused the login to {Database} - check the login, the password or API key and the database.");
                var positional = new JsonArray();
                var named = (JsonObject)arguments.DeepClone();
                foreach (var first in new[] { "ids", "vals_list", "domain" })
                {
                    if (named.Remove(first, out var value))
                    {
                        positional.Add(first == "vals_list" && value is JsonArray { Count: 1 } single ? single[0]!.DeepClone() : value);
                        break;
                    }
                }

                if (method == "write" && named.Remove("vals", out var vals))
                {
                    positional.Add(vals);
                }

                return await RpcAsync(client, "object", "execute_kw", new JsonArray(Database, uid, secret, model, method, positional, named), cancellationToken);
            }

            private async Task<JsonNode?> RpcAsync(HttpClient client, string service, string method, JsonArray args, CancellationToken cancellationToken)
            {
                var content = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "call", ["id"] = 1, ["params"] = new JsonObject { ["service"] = service, ["method"] = method, ["args"] = args } }.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, $"{url}/jsonrpc") { Content = new StringContent(content, Encoding.UTF8, "application/json") }, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = Parse(payload, response);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}.");
                }

                if (json?["error"] is JsonObject error)
                {
                    var name = Text(error["data"]?["name"]);
                    var said = Text(error["data"]?["message"]) ?? Text(error["message"]);
                    throw new InvalidOperationException($"Odoo refused{(name is null ? "" : " - " + name)}{(said is null ? "." : ": " + Short(said))}");
                }

                return json?["result"];
            }

            // A success answer that is not JSON a failure that says so; a failure's body not JSON nothing to read from.
            private static JsonNode? Parse(string payload, HttpResponseMessage response)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Odoo answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            private static string Short(string said) => said.Length > 300 ? said[..300] : said;
        }
    }
}
