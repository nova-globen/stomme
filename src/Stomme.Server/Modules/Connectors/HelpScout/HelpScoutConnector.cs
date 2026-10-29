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

namespace Stomme.Modules.Connectors.HelpScout
{
    /// <summary>
    /// Help Scout (n8n's Help Scout node: a shared inbox's conversations and their threads, its customers and inboxes):
    /// the Mailbox API 2.0 with an app's client credentials at Help Scout's token endpoint through the family's token provider,
    /// over the host's egress. A test counts the inboxes the app can see.
    /// </summary>
    /// <remarks>
    /// Conversations, customers, inboxes (mailboxes), a conversation's threads and the customer properties: <c>records</c> - page
    /// by page to Help Scout's last page, up to <c>max</c> (1,000), by Help Scout's own query parameters (a status, an inbox, a
    /// tag, a search); <c>record</c>; <c>create_record</c> - a conversation or a customer, answered by Help Scout with its id
    /// alone (never the address it names), then got; a thread added to a conversation, its type its path; <c>update_record</c> -
    /// a conversation's or a customer's fields REPLACED one by one (Help Scout's patch), <b>the rest kept</b> - n8n's PUT
    /// overwrote a customer whole - then the record got; <c>delete_record</c> - a conversation. A record is its fields as Help
    /// Scout gives them, its links left out, a list or an object as its JSON. Every resource, id, filter and value held to its
    /// shape before a token is asked for; a success answer that is not JSON, or none where a record is due, a failure that says
    /// so. A throttled call waits as Help Scout says; a failure is thrown with its status, Help Scout's message and the fields
    /// it names.
    /// </remarks>
    public sealed class HelpScoutConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.helpscout.net";

        public const string RecordsOperation = "records";

        public const string RecordOperation = "record";

        public const string CreateRecordOperation = "create_record";

        public const string UpdateRecordOperation = "update_record";

        public const string DeleteRecordOperation = "delete_record";

        public const int DefaultMax = 1000;

        private static readonly string[] Known = { RecordsOperation, RecordOperation, CreateRecordOperation, UpdateRecordOperation, DeleteRecordOperation };

        // Each resource with its path and the key Help Scout lists it under.
        private static readonly Dictionary<string, (string Path, string Key)> Resources = new(StringComparer.Ordinal)
        {
            ["conversations"] = ("conversations", "conversations"), ["customers"] = ("customers", "customers"), ["mailboxes"] = ("mailboxes", "mailboxes"),
            ["threads"] = ("threads", "threads"), ["customer_properties"] = ("customer-properties", "customer-properties"),
        };

        // A thread's type and the path Help Scout adds it by.
        private static readonly Dictionary<string, string> ThreadPaths = new(StringComparer.Ordinal)
        {
            ["reply"] = "reply", ["customer"] = "customer", ["note"] = "notes", ["chat"] = "chats", ["phone"] = "phones",
        };

        private static readonly Regex Id = new("^[0-9]{1,12}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[A-Za-z][A-Za-z0-9]{0,63}(\\.[A-Za-z][A-Za-z0-9]{0,63})?$", RegexOptions.CultureInvariant);

        private static readonly Regex QueryName = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x1F]{1,1024}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "helpscout";

        public string DisplayName => "Help Scout";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "App ID (Your Profile > My Apps > the app)",
            ["client-secret"] = "App Secret (the same app)",
            ["token-url"] = "Token endpoint - https://api.helpscout.net/v2/oauth2/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.helpscout.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.helpscout.com/mailbox-api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField ResourceField = Field("resource", "Resource", "conversations, customers, mailboxes, threads or customer_properties.", required: true);

        private static readonly ConnectorConfigField ConversationField = Field("conversation", "Conversation", "Threads: the conversation's id - a number.");

        private static readonly ConnectorConfigField IdField = Field("id", "Record", "Its id - a number.", required: true);

        private static readonly ConnectorConfigField ValuesField = Field("values", "Values", "A JSON object of fields by Help Scout's names - {\"subject\": \"Kassan är nere\", \"mailboxId\": 123, \"type\": \"email\", \"status\": \"active\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"threads\": [{\"type\": \"customer\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"text\": \"Hej\"}]}.", ConnectorConfigFieldType.Multiline, required: true);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(RecordsOperation, "Get records", "n8n's Help Scout Get Many - conversations, customers, inboxes, a conversation's threads, the customer properties.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ResourceField, ConversationField,
                    Field("filters", "Filters", "A JSON object of Help Scout's query parameters - {\"status\": \"all\", \"mailbox\": 123, \"tag\": \"kassa\", \"query\": \"(subject:\\\"Kassan\\\")\"}."),
                    Field("max", "At most", "How many, at most.", ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(RecordOperation, "Get a record", "n8n's Help Scout Get - a conversation, a customer or an inbox.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
            new ConnectorOperation(CreateRecordOperation, "Create a record", "n8n's Help Scout Create - a conversation, a customer, or a thread on a conversation (its type among the values: reply, customer, note, chat or phone).")
            {
                Parameters = new ConnectorConfigSchema(new[] { ResourceField, ConversationField, ValuesField }),
            },
            new ConnectorOperation(UpdateRecordOperation, "Update a record", "A conversation's or a customer's fields replaced, the rest kept - n8n's Help Scout Update.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField, ValuesField }) },
            new ConnectorOperation(DeleteRecordOperation, "Delete a record", "A conversation - n8n's Help Scout Delete.") { Parameters = new ConnectorConfigSchema(new[] { ResourceField, IdField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v2";
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var answer = (await SendAsync(client, token, HttpMethod.Get, api + "/mailboxes", null, cancellationToken)).Json as JsonObject;
                return ConnectionTestResult.Ok(answer?["page"]?["totalElements"] is JsonValue count && count.TryGetValue<long>(out var inboxes)
                    ? $"Help Scout: signed in - {inboxes.ToString(CultureInfo.InvariantCulture)} inbox{(inboxes == 1 ? "" : "es")}."
                    : "Help Scout: signed in.");
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
                ?? throw new InvalidOperationException($"Help Scout has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v2";
            var resource = Values.Get(p, "resource") is { } named
                ? Resources.ContainsKey(named) ? named : throw new InvalidOperationException($"'{named}' is not one of Help Scout's resources - conversations, customers, mailboxes, threads or customer_properties (resource).")
                : throw new InvalidOperationException("Give the resource - conversations, customers, mailboxes, threads or customer_properties (resource).");
            var allowed = operation switch
            {
                RecordsOperation => Resources.Keys.ToArray(),
                RecordOperation => new[] { "conversations", "customers", "mailboxes" },
                CreateRecordOperation => new[] { "conversations", "customers", "threads" },
                UpdateRecordOperation => new[] { "conversations", "customers" },
                _ => new[] { "conversations" },
            };
            if (!allowed.Contains(resource))
            {
                throw new InvalidOperationException($"Help Scout's {resource.Replace('_', ' ')} cannot be {operation switch { RecordOperation => "got one by one", CreateRecordOperation => "created", UpdateRecordOperation => "updated", DeleteRecordOperation => "deleted", _ => "read" }} here - {string.Join(", ", allowed[..^1])}{(allowed.Length > 1 ? " or " : "")}{allowed[^1]} (resource).");
            }

            string IdOf(string name, string what) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not {what}'s id - a number ({name}).") : throw new InvalidOperationException($"Give {what}'s id ({name}).");
            var conversation = resource == "threads" ? IdOf("conversation", "the conversation") : null;
            var id = operation is RecordOperation or UpdateRecordOperation or DeleteRecordOperation ? IdOf("id", "the record") : null;
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var query = operation == RecordsOperation ? QueryOf(p) : "";
            var values = operation is CreateRecordOperation or UpdateRecordOperation ? ValuesOf(p) : null;
            string? threadPath = null;
            if (resource == "threads" && values is not null)
            {
                threadPath = values["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && ThreadPaths.TryGetValue(kind, out var path)
                    ? path
                    : throw new InvalidOperationException("Give the thread's type among the values - reply, customer, note, chat or phone (values).");
                values.Remove("type");
            }

            var (collection, key) = Resources[resource];
            var list = resource == "threads" ? $"{api}/conversations/{conversation}/threads" : $"{api}/{collection}";
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (operation == RecordsOperation)
            {
                var read = 0;
                for (var page = 1; ; page++)
                {
                    var answer = (await SendAsync(client, token, HttpMethod.Get, $"{list}?page={page.ToString(CultureInfo.InvariantCulture)}{query}", null, cancellationToken)).Json as JsonObject;
                    var items = (answer?["_embedded"]?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    // Help Scout's last page, or an empty one, ends the scan.
                    var last = answer?["page"]?["totalPages"] is JsonValue pages && pages.TryGetValue<int>(out var total) ? total : page;
                    if (items.Count == 0 || page >= last)
                    {
                        yield break;
                    }
                }
            }

            var one = $"{api}/{collection}/{id}";
            if (operation == RecordOperation)
            {
                yield return Record((await SendAsync(client, token, HttpMethod.Get, one, null, cancellationToken)).Json as JsonObject ?? throw new InvalidOperationException($"Help Scout answered with no {Singular(resource)}."));
                yield break;
            }

            if (operation == DeleteRecordOperation)
            {
                await SendAsync(client, token, HttpMethod.Delete, one, null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
                yield break;
            }

            if (operation == UpdateRecordOperation)
            {
                // Help Scout's patch: each field replaced, the rest kept.
                var patch = new JsonArray(values!.Select(pair => (JsonNode)new JsonObject { ["op"] = "replace", ["path"] = "/" + pair.Key, ["value"] = pair.Value?.DeepClone() }).ToArray());
                await SendAsync(client, token, HttpMethod.Patch, one, patch, cancellationToken);
                yield return Record((await SendAsync(client, token, HttpMethod.Get, one, null, cancellationToken)).Json as JsonObject ?? throw new InvalidOperationException($"Help Scout answered with no {Singular(resource)}."));
                yield break;
            }

            // A new record: Help Scout answers with its id alone; the record is got by it - never by the address the answer names.
            var created = await SendAsync(client, token, HttpMethod.Post, threadPath is null ? list : $"{api}/conversations/{conversation}/{threadPath}", values, cancellationToken);
            var newId = created.ResourceId is { } made && Id.IsMatch(made) ? made : throw new InvalidOperationException($"Help Scout created the {Singular(resource)} but named no id for it.");
            if (threadPath is not null)
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(newId, CultureInfo.InvariantCulture), ["conversation"] = long.Parse(conversation!, CultureInfo.InvariantCulture), ["created"] = true });
                yield break;
            }

            yield return Record((await SendAsync(client, token, HttpMethod.Get, $"{api}/{collection}/{newId}", null, cancellationToken)).Json as JsonObject ?? throw new InvalidOperationException($"Help Scout answered with no {Singular(resource)}."));
        }

        private static string Singular(string resource) => resource switch { "mailboxes" => "inbox", "customer_properties" => "customer property", _ => resource[..^1] };

        // Get Many's query: Help Scout's own parameters, each escaped; the page the step's own.
        private static string QueryOf(IReadOnlyDictionary<string, string> parameters)
        {
            if (Values.Get(parameters, "filters") is not { } given)
            {
                return "";
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
                throw new InvalidOperationException("The filters are not a JSON object of Help Scout's query parameters - {\"status\": \"all\"} (filters).");
            }

            var query = new StringBuilder();
            foreach (var (name, value) in filters)
            {
                if (!QueryName.IsMatch(name) || name == "page")
                {
                    throw new InvalidOperationException(name == "page" ? "'page' is set by the step - give max instead (filters)." : $"'{name}' is not one of Help Scout's query parameters (filters).");
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

        // A record's values: a JSON object of fields by Help Scout's names.
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
                    throw new InvalidOperationException($"'{name}' is not a field's name - firstName, subject, primaryCustomer.id (values).");
                }
            }

            return values;
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => pair.Key != "_links").ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? throw new InvalidOperationException("Give Help Scout's token endpoint - https://api.helpscout.net/v2/oauth2/token."),
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                string.Empty)
            { Timeout = timeout }, cancellationToken);

        // Help Scout's answer as JSON, with the id a new record is answered by; a throttled call waiting as Help Scout says; a
        // failure thrown with its status, Help Scout's message and the fields it names; a success that is not JSON a failure that
        // says so (an empty answer - nothing).
        private static async Task<(JsonNode? Json, string? ResourceId)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken)
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
                throw new InvalidOperationException($"Help Scout answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = json as JsonObject;
                var said = Words(failure?["message"]);
                var fields = (failure?["_embedded"]?["errors"] as JsonArray)?.OfType<JsonObject>().Select(error => string.Join(": ", new[] { Words(error["path"]), Words(error["message"]) }.Where(part => !string.IsNullOrEmpty(part)))).Where(part => part.Length > 0).ToList() ?? new List<string>();
                var text = string.Join(" - ", new[] { said, fields.Count == 0 ? null : string.Join("; ", fields) }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return (json, response.Headers.TryGetValues("Resource-ID", out var ids) ? ids.FirstOrDefault() : null);
        }
    }
}
