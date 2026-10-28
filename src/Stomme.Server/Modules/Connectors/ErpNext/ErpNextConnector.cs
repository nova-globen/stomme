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

namespace Stomme.Modules.Connectors.ErpNext
{
    /// <summary>
    /// ERPNext (BL-229, n8n's ERPNext node) over Frappe's REST API (<c>{address}/api/resource/{doctype}</c>) by an API key and
    /// secret - a user's, from its settings - as Frappe's <c>token key:secret</c> header, never in the address. A test names the
    /// key's user.
    /// </summary>
    /// <remarks>
    /// n8n's one resource, a document of any doctype: listed, read, made, changed and deleted. Measured against ERPNext 16.37
    /// (<c>scripts/erpnext-test-server.ps1</c>): n8n sends every value as text, so a child table - a sales order's items -
    /// reaches Frappe as a string and is a 500 (a Python TypeError), where here a value is JSON; a field the doctype has not got
    /// is dropped without a word on a create and on a change, a date that is no date is a 500 (the database's own error) and a
    /// filter's operator Frappe does not know a 500 (a KeyError) - so every field, child row and filter is held to the doctype's
    /// own definition (<c>frappe.desk.form.load.getdoctype</c>, read once a run), its type and choices checked, before anything
    /// is sent; n8n's 403 said "DocType unavailable" whatever Frappe said, where here Frappe's own message is. A document's
    /// <c>docstatus</c> may be changed to 1 or 2, which submits or cancels it, as Frappe has it.
    /// </remarks>
    public sealed class ErpNextConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 500;

        private const int MostText = 1048576;

        private static readonly Regex DocTypeName = new("^[A-Za-z][A-Za-z0-9 _-]{0,139}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z_][a-z0-9_]{0,139}$", RegexOptions.CultureInvariant);

        // Frappe's own operators, and n8n's names for six of them.
        private static readonly Dictionary<string, string> Operators = new(StringComparer.OrdinalIgnoreCase)
        {
            ["="] = "=", ["!="] = "!=", [">"] = ">", ["<"] = "<", [">="] = ">=", ["<="] = "<=", ["like"] = "like", ["not like"] = "not like",
            ["in"] = "in", ["not in"] = "not in", ["is"] = "is", ["between"] = "between", ["timespan"] = "timespan",
            ["isNot"] = "!=", ["greater"] = ">", ["less"] = "<", ["equalsGreater"] = ">=", ["equalsLess"] = "<=",
        };

        // Fields every document has beside its doctype's own.
        private static readonly string[] Standard = { "name", "owner", "creation", "modified", "modified_by", "docstatus", "idx", "parent", "parentfield", "parenttype" };

        private static readonly HashSet<string> Layout = new(StringComparer.Ordinal) { "Section Break", "Column Break", "Tab Break", "HTML", "Button", "Heading", "Fold" };

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "erpnext";

        public string DisplayName => "ERPNext";

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["access-key"] = "API key", ["secret-key"] = "API secret" };

        public string EndpointExample => "https://erp.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Commerce };

        public Uri WebsiteUrl { get; } = new("https://erpnext.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.frappe.io/framework/user/en/api/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField DocTypeField = new("doctype", "Document type", ConnectorConfigFieldType.Text, Required: true, Description: "Its doctype - Customer, Sales Order, ToDo.");

        private static readonly ConnectorConfigField NameField = new("name", "Document", ConnectorConfigFieldType.Text, Required: true, Description: "Its name - CUST-0001, SAL-ORD-2026-00001.");

        private static ConnectorConfigField FieldsField(bool required) => new("fields", "Fields", ConnectorConfigFieldType.Multiline, Required: required,
            Description: "A JSON object of its fields - {\"customer\": \"Kafé Lund\", \"items\": [{\"item_code\": \"KAFFE\", \"qty\": 3}]} - from the step's Query box. Over several lines from the step's Query box.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("documents", "Get documents", "n8n's Document - Get Many.")
            {
                Parameters = new(new[]
                {
                    DocTypeField,
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Required: false, Description: "The fields to return, by commas - its name alone by default, * for all."),
                    new ConnectorConfigField("filters", "Filters", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - [[\"status\", \"=\", \"Open\"]] or {\"status\": \"Open\"}; operators =, !=, >, <, >=, <=, like, not like, in, not in, is, between. From the step's Query box."),
                    new ConnectorConfigField("order_by", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "A field, then asc or desc - modified desc."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation("document", "Get a document", "n8n's Document - Get.") { Parameters = new(new[] { DocTypeField, NameField }) },
            new ConnectorOperation("document_create", "Create a document", "n8n's Document - Create.") { Parameters = new(new[] { DocTypeField, FieldsField(true) }) },
            new ConnectorOperation("document_update", "Update a document", "n8n's Document - Update; docstatus 1 submits it, 2 cancels it.") { Parameters = new(new[] { DocTypeField, NameField, FieldsField(true) }) },
            new ConnectorOperation("document_delete", "Delete a document", "n8n's Document - Delete.") { Parameters = new(new[] { DocTypeField, NameField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                var user = Text((await session.SendAsync(HttpMethod.Get, "/api/method/frappe.auth.get_logged_user", null, cancellationToken))?["message"]);
                return ConnectionTestResult.Ok($"ERPNext answers: the key is {user ?? "a user"}'s.");
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
            var operation = (context.OperationName ?? "documents").Trim().ToLowerInvariant();
            if (Operations.All(known => known.Name != operation))
            {
                throw new InvalidOperationException($"ERPNext has no operation '{context.OperationName}' - documents, document, document_create, document_update or document_delete.");
            }

            var p = context.Parameters;
            var doctype = Values.Get(p, "doctype")?.Trim() ?? throw new InvalidOperationException("Give the document type - Customer, Sales Order, ToDo (doctype).");
            if (!DocTypeName.IsMatch(doctype))
            {
                throw new InvalidOperationException($"'{doctype}' is not a doctype's name - Customer, Sales Order (doctype).");
            }

            var route = "/api/resource/" + Uri.EscapeDataString(doctype);
            string Named() => route + "/" + Uri.EscapeDataString(Values.Get(p, "name") is { Length: > 0 and <= 140 } given && !given.Any(char.IsControl) ? given : throw new InvalidOperationException("Give the document - its name, one line (name)."));
            switch (operation)
            {
                case "documents":
                    {
                        var max = Values.Get(p, "max") is { } most
                            ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).")
                            : DefaultMax;
                        var fields = Values.Get(p, "fields") is { } named ? named.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray() : Array.Empty<string>();
                        var filters = Values.Get(p, "filters") ?? Values.Get(p, "query");
                        var order = Values.Get(p, "order_by")?.Trim();
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var meta = await session.MetaAsync(doctype, cancellationToken);
                        var query = new List<string>();
                        if (fields.Length > 0)
                        {
                            query.Add("fields=" + Uri.EscapeDataString(JsonSerializer.Serialize(fields.Contains("*") ? new[] { "*" } : fields.Select(field => meta.Known(field, "fields")).ToArray())));
                        }

                        if (filters is not null)
                        {
                            query.Add("filters=" + Uri.EscapeDataString(Filters(filters, doctype, meta).ToJsonString(Wire)));
                        }

                        if (order is { Length: > 0 })
                        {
                            var parts = order.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var direction = parts.Length == 2 ? parts[1].ToLowerInvariant() : "asc";
                            if (parts.Length is not (1 or 2) || direction is not ("asc" or "desc"))
                            {
                                throw new InvalidOperationException($"'{order}' is not a field and asc or desc - modified desc (order_by).");
                            }

                            query.Add("order_by=" + Uri.EscapeDataString(meta.Known(parts[0], "order_by") + " " + direction));
                        }

                        var found = 0;
                        for (var start = 0; found < max; start += PageSize)
                        {
                            var items = await session.SendAsync(HttpMethod.Get, $"{route}?{string.Join("&", query.Append($"limit_start={start}").Append($"limit_page_length={PageSize}"))}", null, cancellationToken) is JsonObject { } answer && answer["data"] is JsonArray list
                                ? list.OfType<JsonObject>().ToList()
                                : throw new InvalidOperationException("ERPNext answered with what is not a list - check the address: a sign-in page or a proxy answers so.");
                            foreach (var item in items.Take(max - found))
                            {
                                found++;
                                yield return Record(item);
                            }

                            if (items.Count < PageSize)
                            {
                                break;
                            }
                        }

                        break;
                    }

                case "document":
                    {
                        var target = Named();
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        yield return Record(await Data(session, HttpMethod.Get, target, null, cancellationToken));
                        break;
                    }

                case "document_create":
                case "document_update":
                    {
                        var target = operation == "document_update" ? Named() : route;
                        var given = Values.Get(p, "fields") ?? Values.Get(p, "query") ?? throw new InvalidOperationException("Give the fields - a JSON object, {\"description\": \"Ring Ada\"} (fields).");
                        var fields = Parse(given, "fields") as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object - {\"description\": \"Ring Ada\"} (fields).");
                        if (fields.Count == 0)
                        {
                            throw new InvalidOperationException("Give at least one field (fields).");
                        }

                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var meta = await session.MetaAsync(doctype, cancellationToken);
                        var body = await CheckAsync(session, meta, fields, operation == "document_create", "fields", cancellationToken);
                        yield return Record(await Data(session, operation == "document_create" ? HttpMethod.Post : HttpMethod.Put, target, body, cancellationToken));
                        break;
                    }

                default:
                    {
                        var target = Named();
                        using var session = Session.Open(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                        var answer = await session.SendAsync(HttpMethod.Delete, target, null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["doctype"] = doctype, ["name"] = Values.Get(p, "name"), ["deleted"] = Text(answer?["data"]) == "ok" || Text(answer?["message"]) == "ok" });
                        break;
                    }
            }
        }

        // A document's fields held to its doctype: each one it has, of its type, a child table's rows to the child doctype.
        private static async Task<JsonObject> CheckAsync(Session session, Meta meta, JsonObject fields, bool create, string where, CancellationToken cancellationToken)
        {
            var body = new JsonObject();
            foreach (var (key, value) in fields)
            {
                if (key == "docstatus")
                {
                    body[key] = value is JsonValue status && status.TryGetValue<int>(out var number) && number is 0 or 1 or 2 || (value is JsonValue text && text.TryGetValue<string>(out var said) && int.TryParse(said, out number) && number is 0 or 1 or 2)
                        ? number
                        : throw new InvalidOperationException($"A docstatus is 0 (draft), 1 (submitted) or 2 (cancelled) ({where}).");
                    continue;
                }

                if (!meta.Fields.TryGetValue(key, out var field))
                {
                    if (key == "name" && create)
                    {
                        body[key] = value?.DeepClone(); // a name given where the doctype is named by prompt
                        continue;
                    }

                    meta.Known(key, where);
                }

                body[key] = field!.Type switch
                {
                    "Table" or "Table MultiSelect" => await RowsAsync(session, field, value, cancellationToken),
                    "Int" => Number(value, key, where) is var whole && whole == Math.Floor(whole) ? JsonValue.Create((long)whole) : throw new InvalidOperationException($"{key} is a whole number ({where})."),
                    "Float" or "Currency" or "Percent" => JsonValue.Create(Number(value, key, where)),
                    "Check" => Flag(value, key, where),
                    "Date" => Shaped(value, key, where, "yyyy-MM-dd", "2026-10-21"),
                    "Datetime" => Shaped(value, key, where, "yyyy-MM-dd HH:mm:ss", "2026-10-21 08:00:00"),
                    "Time" => Shaped(value, key, where, "HH:mm:ss", "08:00:00"),
                    "Select" => value is JsonValue choice && choice.TryGetValue<string>(out var option) && (option.Length == 0 || field.Options.Contains(option))
                        ? JsonValue.Create(option)
                        : throw new InvalidOperationException($"{key} is one of {string.Join(", ", field.Options.Where(item => item.Length > 0).Select(item => '"' + item + '"'))} ({where})."),
                    _ => value is JsonValue plain && (plain.TryGetValue<string>(out var text) ? text.Length <= MostText : true)
                        ? plain.DeepClone()
                        : throw new InvalidOperationException($"{key} is a value, not {(value is null ? "null" : "a list or an object")} ({where})."),
                };
            }

            if (create)
            {
                foreach (var missing in meta.Fields.Values.Where(field => field.Required && field.Default is null && !body.ContainsKey(field.Name) && field.Type is not ("Table" or "Table MultiSelect")))
                {
                    throw new InvalidOperationException($"Give {missing.Name} - {meta.DocType} requires it ({where}).");
                }
            }

            return body;
        }

        private static async Task<JsonArray> RowsAsync(Session session, Field field, JsonNode? value, CancellationToken cancellationToken)
        {
            var rows = (value is JsonValue text && text.TryGetValue<string>(out var said) ? Parse(said, field.Name) : value) as JsonArray
                ?? throw new InvalidOperationException($"{field.Name} is a list of rows - [{{\"item_code\": \"KAFFE\", \"qty\": 3}}] ({field.Name}).");
            var child = await session.MetaAsync(field.Options.FirstOrDefault() ?? string.Empty, cancellationToken);
            var checkedRows = new JsonArray();
            foreach (var row in rows)
            {
                checkedRows.Add(await CheckAsync(session, child, row as JsonObject ?? throw new InvalidOperationException($"Each row of {field.Name} is a JSON object ({field.Name})."), create: true, field.Name, cancellationToken)); // a row is new: what it requires is given
            }

            return checkedRows;
        }

        // A list's filters: Frappe's own list of [field, operator, value] or an object of field and value, each field the doctype's.
        private static JsonArray Filters(string given, string doctype, Meta meta)
        {
            var parsed = Parse(given, "filters");
            var filters = new JsonArray();
            void Add(string field, string op, JsonNode? value)
            {
                var known = Operators.TryGetValue(op.Trim(), out var frappe) ? frappe : throw new InvalidOperationException($"'{op}' is no operator of Frappe's - =, !=, >, <, >=, <=, like, not like, in, not in, is, between (filters).");
                filters.Add(new JsonArray(doctype, meta.Known(field, "filters"), known, value?.DeepClone()));
            }

            switch (parsed)
            {
                case JsonObject pairs:
                    foreach (var (field, value) in pairs)
                    {
                        Add(field, "=", value);
                    }

                    break;
                case JsonArray list:
                    foreach (var item in list)
                    {
                        var parts = item as JsonArray ?? throw new InvalidOperationException("Each filter is a list - [\"status\", \"=\", \"Open\"] (filters).");
                        var offset = parts.Count == 4 ? 1 : 0; // Frappe's own form names the doctype first
                        if (parts.Count - offset != 3 || Text(parts[offset]) is not { } field || Text(parts[offset + 1]) is not { } op)
                        {
                            throw new InvalidOperationException("Each filter is a field, an operator and a value - [\"status\", \"=\", \"Open\"] (filters).");
                        }

                        Add(field, op, parts[offset + 2]);
                    }

                    break;
                default:
                    throw new InvalidOperationException("The filters are JSON - [[\"status\", \"=\", \"Open\"]] or {\"status\": \"Open\"} (filters).");
            }

            return filters;
        }

        private static double Number(JsonNode? value, string key, string where) =>
            value is JsonValue number && (number.TryGetValue<double>(out var amount) || (number.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))) && double.IsFinite(amount)
                ? amount
                : throw new InvalidOperationException($"{key} is a number ({where}).");

        private static JsonNode Flag(JsonNode? value, string key, string where) =>
            value is JsonValue flag && (flag.TryGetValue<bool>(out var yes) || (flag.TryGetValue<int>(out var number) && number is 0 or 1 && (yes = number == 1) == yes) || (flag.TryGetValue<string>(out var text) && (bool.TryParse(text, out yes) || (text is "0" or "1" && (yes = text == "1") == yes))))
                ? JsonValue.Create(yes ? 1 : 0)
                : throw new InvalidOperationException($"{key} is true or false ({where}).");

        private static JsonNode Shaped(JsonNode? value, string key, string where, string format, string example) =>
            value is JsonValue text && text.TryGetValue<string>(out var said) && (format == "yyyy-MM-dd"
                ? DateOnly.TryParseExact(said, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                : DateTime.TryParseExact(said, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                ? JsonValue.Create(said)
                : throw new InvalidOperationException($"'{value}' is not {(format == "HH:mm:ss" ? "a time" : format.Length > 10 ? "a moment" : "a date")} - {example} ({key}).");

        private static JsonNode? Parse(string value, string name)
        {
            try
            {
                return value.Length <= MostText ? JsonNode.Parse(value) : throw new InvalidOperationException($"The {name} are at most a megabyte ({name}).");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {name} are not JSON ({name}).");
            }
        }

        private static async Task<JsonObject> Data(Session session, HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken) =>
            (await session.SendAsync(method, route, body, cancellationToken))?["data"] as JsonObject
                ?? throw new InvalidOperationException("ERPNext answered without the document - check the address: a sign-in page or a proxy answers so.");

        private static ConnectorRecord Record(JsonObject data) => new(data.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => (object?)other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed record Field(string Name, string Type, bool Required, string? Default, string[] Options);

        // A doctype's definition as Frappe gives it: its fields by name, beside the fields every document has.
        private sealed class Meta
        {
            public Meta(string docType, Dictionary<string, Field> fields)
            {
                DocType = docType;
                Fields = fields;
            }

            public string DocType { get; }

            public Dictionary<string, Field> Fields { get; }

            public string Known(string field, string where)
            {
                var name = field.Trim();
                if (FieldName.IsMatch(name) && (Fields.ContainsKey(name) || Standard.Contains(name)))
                {
                    return name;
                }

                var near = Fields.Keys.Where(key => key.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(5).ToArray();
                throw new InvalidOperationException($"{DocType} has no field '{field}'{(near.Length > 0 ? " - " + string.Join(", ", near) + "?" : string.Empty)} ({where}).");
            }
        }

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string root;
            private readonly string authorization;
            private readonly Dictionary<string, Meta> metas = new(StringComparer.Ordinal);

            private Session(HttpClient client, string root, string authorization)
            {
                this.client = client;
                this.root = root;
                this.authorization = authorization;
            }

            public static Session Open(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give ERPNext's address - https://erp.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    root = root[..^"/api".Length]; // the API's own address given: the site's
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not ERPNext's address - https://erp.kafe.example, with no query.");
                }

                var key = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The ERPNext connection has no API key.");
                var secret = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The ERPNext connection has no API secret.");
                return new Session(egress.CreateHttpClient(timeout), root, $"token {key}:{secret}");
            }

            public void Dispose() => client.Dispose();

            /// <summary>A doctype's definition, read once a session: its fields, their types, choices and whether it requires them.</summary>
            public async Task<Meta> MetaAsync(string doctype, CancellationToken cancellationToken)
            {
                if (metas.TryGetValue(doctype, out var known))
                {
                    return known;
                }

                var docs = (await SendAsync(HttpMethod.Get, "/api/method/frappe.desk.form.load.getdoctype?doctype=" + Uri.EscapeDataString(doctype), null, cancellationToken))?["docs"] as JsonArray
                    ?? throw new InvalidOperationException("ERPNext answered without the doctype's definition - check the address.");
                foreach (var doc in docs.OfType<JsonObject>())
                {
                    var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
                    foreach (var field in (doc["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (Text(field["fieldname"]) is { } name && Text(field["fieldtype"]) is { } type && !Layout.Contains(type))
                        {
                            fields[name] = new Field(name, type, field["reqd"]?.ToString() == "1", Text(field["default"]) is { Length: > 0 } preset ? preset : null,
                                (Text(field["options"]) ?? string.Empty).Split('\n'));
                        }
                    }

                    if (Text(doc["name"]) is { } docName)
                    {
                        metas[docName] = new Meta(docName, fields);
                    }
                }

                return metas.TryGetValue(doctype, out known) ? known : throw new InvalidOperationException($"ERPNext has no doctype '{doctype}' (doctype).");
            }

            /// <summary>A call with Frappe's token header; its JSON, or its status and its own message.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
                {
                    var request = new HttpRequestMessage(method, root + route);
                    request.Headers.TryAddWithoutValidation("Authorization", authorization);
                    request.Headers.Accept.ParseAdd("application/json");
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json;
                try
                {
                    json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    json = null;
                }

                if (response.IsSuccessStatusCode)
                {
                    return json ?? throw new InvalidOperationException("ERPNext answered with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }

                var status = (int)response.StatusCode;
                var said = Said(json);
                said = said.Length > 300 ? said[..300] : said;
                throw new InvalidOperationException($"ERPNext answered HTTP {status}{(said.Length == 0 ? "." : ": " + said + (said.EndsWith('.') ? string.Empty : "."))}"
                    + (status == 401 ? " - check the API key and secret." : string.Empty));
            }

            // Frappe's message: its server messages (JSON in JSON), else its exception without its class, its HTML taken out.
            private static string Said(JsonNode? json)
            {
                var messages = new List<string>();
                if (Text(json?["_server_messages"]) is { } server)
                {
                    try
                    {
                        foreach (var item in JsonNode.Parse(server) as JsonArray ?? new JsonArray())
                        {
                            if (Text(item) is { } inner && JsonNode.Parse(inner)?["message"] is JsonValue message && message.TryGetValue<string>(out var said))
                            {
                                messages.Add(said);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                if (messages.Count == 0 && Text(json?["exception"]) is { } exception)
                {
                    var colon = exception.IndexOf(": ", StringComparison.Ordinal);
                    messages.Add(colon > 0 ? exception[(colon + 2)..] : exception);
                }

                var text = Regex.Replace(string.Join(" ", messages), "<br\\s*/?>", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                text = Regex.Replace(text, "<[^>]+>", string.Empty, RegexOptions.CultureInvariant);
                return Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
            }
        }
    }
}
