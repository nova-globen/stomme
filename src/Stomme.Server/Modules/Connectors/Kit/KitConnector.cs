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

namespace Stomme.Modules.Connectors.Kit
{
    /// <summary>
    /// Kit (BL-229, n8n's ConvertKit node: subscribers through forms, sequences and tags, and the custom fields): Kit's API v4
    /// with an API key (<c>X-Kit-Api-Key</c>), over the host's egress. A test names the key's account.
    /// </summary>
    /// <remarks>
    /// <c>subscribers</c> (by status, e-mail and when created or updated, sorted - page by page by Kit's cursor up to
    /// <c>max</c>), <c>subscriber</c>, <c>create_subscriber</c> (Kit's upsert: its first name, state and fields),
    /// <c>update_subscriber</c> and <c>unsubscribe</c>; <c>forms</c>, <c>add_to_form</c> and <c>form_subscribers</c>;
    /// <c>sequences</c>, <c>add_to_sequence</c> and <c>sequence_subscribers</c>; <c>tags</c>, <c>create_tag</c>,
    /// <c>tag_subscriber</c>, <c>untag_subscriber</c> and <c>tag_subscribers</c>; <c>custom_fields</c>,
    /// <c>create_custom_field</c>, <c>update_custom_field</c> and <c>delete_custom_field</c>. A subscriber added to a form,
    /// a sequence or a tag by e-mail must exist first - created by <c>create_subscriber</c>. A record is its fields as Kit
    /// gives them, a list or an object as its JSON. Every id, e-mail, state, date, sort and field held to its shape before
    /// anything is sent. A throttled call waits as Kit says; a failure is thrown with its status and Kit's words.
    /// </remarks>
    public sealed class KitConnector : IConnector, IConnectorExtractor
    {
        public const string SubscribersOperation = "subscribers";

        public const string SubscriberOperation = "subscriber";

        public const string CreateSubscriberOperation = "create_subscriber";

        public const string UpdateSubscriberOperation = "update_subscriber";

        public const string UnsubscribeOperation = "unsubscribe";

        public const string FormsOperation = "forms";

        public const string AddToFormOperation = "add_to_form";

        public const string FormSubscribersOperation = "form_subscribers";

        public const string SequencesOperation = "sequences";

        public const string AddToSequenceOperation = "add_to_sequence";

        public const string SequenceSubscribersOperation = "sequence_subscribers";

        public const string TagsOperation = "tags";

        public const string CreateTagOperation = "create_tag";

        public const string TagSubscriberOperation = "tag_subscriber";

        public const string UntagSubscriberOperation = "untag_subscriber";

        public const string TagSubscribersOperation = "tag_subscribers";

        public const string CustomFieldsOperation = "custom_fields";

        public const string CreateCustomFieldOperation = "create_custom_field";

        public const string UpdateCustomFieldOperation = "update_custom_field";

        public const string DeleteCustomFieldOperation = "delete_custom_field";

        public const string DefaultEndpoint = "https://api.kit.com/v4";

        public const int DefaultMax = 1000;

        private const int PageSize = 1000;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            SubscribersOperation, SubscriberOperation, CreateSubscriberOperation, UpdateSubscriberOperation, UnsubscribeOperation,
            FormsOperation, AddToFormOperation, FormSubscribersOperation, SequencesOperation, AddToSequenceOperation, SequenceSubscribersOperation,
            TagsOperation, CreateTagOperation, TagSubscriberOperation, UntagSubscriberOperation, TagSubscribersOperation,
            CustomFieldsOperation, CreateCustomFieldOperation, UpdateCustomFieldOperation, DeleteCustomFieldOperation,
        };

        private static readonly string[] States = { "active", "inactive", "bounced", "complained", "cancelled" };

        public string ConnectorType => "kit";

        public string DisplayName => "Kit";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "V4 API key (Settings > Developer)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://kit.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.kit.com/api-reference/overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField EmailField = Field("email", "E-mail", "The subscriber's e-mail - created first by Create a subscriber.", required: true);

        private static ConnectorConfigField IdField(string name, string what) => Field(name, char.ToUpperInvariant(what[0]) + what[1..], $"The {what}'s id.", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(SubscribersOperation, "Get subscribers", "Subscribers, by status, e-mail and when created or updated.")
            {
                Parameters = Schema(
                    Field("status", "Status", "active (the default), inactive, bounced, complained, cancelled or all."),
                    Field("email", "E-mail", "Its exact e-mail."),
                    Field("created_after", "Created after", "A date - 2026-10-01."),
                    Field("created_before", "Created before", "A date."),
                    Field("updated_after", "Updated after", "A date."),
                    Field("updated_before", "Updated before", "A date."),
                    Field("sort", "Sort", "id (the default), created_at or updated_at - a leading - for descending."),
                    MaxField),
            },
            new ConnectorOperation(SubscriberOperation, "Get a subscriber", "A subscriber by its id.") { Parameters = Schema(IdField("subscriber", "subscriber")) },
            new ConnectorOperation(CreateSubscriberOperation, "Create a subscriber", "Created, or its first name updated when the e-mail is already a subscriber's.")
            {
                Parameters = Schema(
                    Field("email", "E-mail", "Their e-mail.", required: true),
                    Field("first_name", "First name", "Their first name."),
                    Field("state", "State", "active (the default), inactive, bounced, complained or cancelled."),
                    Field("fields", "Fields", "A JSON object of custom fields by their keys - {\"last_name\": \"Lovelace\"}.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateSubscriberOperation, "Update a subscriber", "Changes what is given.")
            {
                Parameters = Schema(IdField("subscriber", "subscriber"), Field("email", "E-mail", "Their new e-mail."), Field("first_name", "First name", "Their first name."), Field("fields", "Fields", "A JSON object of custom fields by their keys.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UnsubscribeOperation, "Unsubscribe", "A subscriber unsubscribed from everything.") { Parameters = Schema(IdField("subscriber", "subscriber")) },
            new ConnectorOperation(FormsOperation, "Get forms", "The forms and landing pages - n8n's ConvertKit Form Get Many.") { Parameters = Schema(Field("status", "Status", "active (the default), archived, trashed or all."), MaxField) },
            new ConnectorOperation(AddToFormOperation, "Add to a form", "A subscriber added to a form by e-mail - n8n's ConvertKit Form Add Subscriber.") { Parameters = Schema(IdField("form", "form"), EmailField, Field("referrer", "Referrer", "Where they came from - an address.")) },
            new ConnectorOperation(FormSubscribersOperation, "Get a form's subscribers", "n8n's ConvertKit Form Get Subscriptions.") { Parameters = Schema(IdField("form", "form"), Field("status", "Status", "active (the default), inactive, bounced, complained, cancelled or all."), MaxField) },
            new ConnectorOperation(SequencesOperation, "Get sequences", "n8n's ConvertKit Sequence Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(AddToSequenceOperation, "Add to a sequence", "A subscriber added to a sequence by e-mail - n8n's ConvertKit Sequence Add Subscriber.") { Parameters = Schema(IdField("sequence", "sequence"), EmailField) },
            new ConnectorOperation(SequenceSubscribersOperation, "Get a sequence's subscribers", "n8n's ConvertKit Sequence Get Subscriptions.") { Parameters = Schema(IdField("sequence", "sequence"), Field("status", "Status", "active (the default), inactive, bounced, complained, cancelled or all."), MaxField) },
            new ConnectorOperation(TagsOperation, "Get tags", "n8n's ConvertKit Tag Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(CreateTagOperation, "Create a tag", "n8n's ConvertKit Tag Create.") { Parameters = Schema(Field("name", "Name", "Its name.", required: true)) },
            new ConnectorOperation(TagSubscriberOperation, "Tag a subscriber", "A subscriber tagged by e-mail - n8n's ConvertKit Tag Subscriber Add.") { Parameters = Schema(IdField("tag", "tag"), EmailField) },
            new ConnectorOperation(UntagSubscriberOperation, "Untag a subscriber", "A tag taken off a subscriber by e-mail - n8n's ConvertKit Tag Subscriber Delete.") { Parameters = Schema(IdField("tag", "tag"), EmailField) },
            new ConnectorOperation(TagSubscribersOperation, "Get a tag's subscribers", "n8n's ConvertKit Tag Subscriber Get Many.") { Parameters = Schema(IdField("tag", "tag"), Field("status", "Status", "active (the default), inactive, bounced, complained, cancelled or all."), MaxField) },
            new ConnectorOperation(CustomFieldsOperation, "Get custom fields", "n8n's ConvertKit Custom Field Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(CreateCustomFieldOperation, "Create a custom field", "n8n's ConvertKit Custom Field Create.") { Parameters = Schema(Field("label", "Label", "Its label.", required: true)) },
            new ConnectorOperation(UpdateCustomFieldOperation, "Rename a custom field", "Its new label - n8n's ConvertKit Custom Field Update.") { Parameters = Schema(IdField("custom_field", "custom field"), Field("label", "Label", "Its new label.", required: true)) },
            new ConnectorOperation(DeleteCustomFieldOperation, "Delete a custom field", "n8n's ConvertKit Custom Field Delete.") { Parameters = Schema(IdField("custom_field", "custom field")) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var answer = await session.SendAsync(HttpMethod.Get, "/account", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Kit: the key is {(Text(answer?["account"]?["name"]) is { Length: > 0 } name ? name : "an account")}'s{(Text(answer?["user"]?["email"]) is { } email ? $" ({email})" : "")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? SubscribersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Kit has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string name, string what) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).") : throw new InvalidOperationException($"Give the {what}'s id.");
            string EmailOf(bool required) => Values.Get(p, "email") is { } given ? Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail (email).") : required ? throw new InvalidOperationException("Give the subscriber's e-mail.") : "";
            var id = operation switch
            {
                SubscriberOperation or UpdateSubscriberOperation or UnsubscribeOperation => IdOf("subscriber", "subscriber"),
                AddToFormOperation or FormSubscribersOperation => IdOf("form", "form"),
                AddToSequenceOperation or SequenceSubscribersOperation => IdOf("sequence", "sequence"),
                TagSubscriberOperation or UntagSubscriberOperation or TagSubscribersOperation => IdOf("tag", "tag"),
                UpdateCustomFieldOperation or DeleteCustomFieldOperation => IdOf("custom_field", "custom field"),
                _ => null,
            };
            var email = operation is CreateSubscriberOperation or AddToFormOperation or AddToSequenceOperation or TagSubscriberOperation or UntagSubscriberOperation ? EmailOf(required: true)
                : operation is SubscribersOperation or UpdateSubscriberOperation ? EmailOf(required: false) : "";

            JsonObject? body = null;
            if (operation is CreateSubscriberOperation or UpdateSubscriberOperation)
            {
                body = new JsonObject();
                if (email.Length > 0)
                {
                    body["email_address"] = email;
                }

                if (Values.Get(p, "first_name") is { } first)
                {
                    body["first_name"] = first;
                }

                if (operation == CreateSubscriberOperation && Values.Get(p, "state") is { } state)
                {
                    body["state"] = States.Contains(state.ToLowerInvariant()) ? state.ToLowerInvariant() : throw new InvalidOperationException($"'{state}' is not active, inactive, bounced, complained or cancelled (state).");
                }

                if (Values.Get(p, "fields") is { } fields)
                {
                    var parsed = Parse(fields) as JsonObject ?? throw new InvalidOperationException("The fields are not a JSON object - {\"last_name\": \"Lovelace\"}.");
                    var texts = new JsonObject();
                    foreach (var (key, value) in parsed)
                    {
                        // Kit keeps a custom field as a text: a number or true or false as its JSON, null as nothing.
                        texts[key] = value switch
                        {
                            null => null,
                            JsonValue text when text.TryGetValue<string>(out var s) => s,
                            JsonValue other => other.ToJsonString(Wire),
                            _ => throw new InvalidOperationException($"'{key}' is not a text, a number, true or false or null (fields)."),
                        };
                    }

                    body["fields"] = texts;
                }

                if (operation == UpdateSubscriberOperation && body.Count == 0)
                {
                    throw new InvalidOperationException("Give what to change - their e-mail, first name or fields.");
                }
            }

            if (operation is AddToFormOperation or AddToSequenceOperation or TagSubscriberOperation)
            {
                body = new JsonObject { ["email_address"] = email };
                if (operation == AddToFormOperation && Values.Get(p, "referrer") is { } referrer)
                {
                    body["referrer"] = referrer;
                }
            }

            string Required(string name, string what) => Values.Get(p, name) is { } value ? value.Length <= 255 ? value : throw new InvalidOperationException($"The {what} is longer than 255 characters.") : throw new InvalidOperationException($"Give the {what}.");
            if (operation == CreateTagOperation)
            {
                body = new JsonObject { ["name"] = Required("name", "tag's name") };
            }

            if (operation is CreateCustomFieldOperation or UpdateCustomFieldOperation)
            {
                body = new JsonObject { ["label"] = Required("label", "custom field's label") };
            }

            var query = new List<string>();
            if (operation is SubscribersOperation or FormSubscribersOperation or SequenceSubscribersOperation or TagSubscribersOperation or FormsOperation && Values.Get(p, "status") is { } status)
            {
                var allowed = operation == FormsOperation ? new[] { "active", "archived", "trashed", "all" } : States.Append("all").ToArray();
                query.Add("status=" + (allowed.Contains(status.ToLowerInvariant()) ? status.ToLowerInvariant() : throw new InvalidOperationException($"'{status}' is not {string.Join(", ", allowed[..^1])} or {allowed[^1]} (status).")));
            }

            if (operation == SubscribersOperation)
            {
                if (email.Length > 0)
                {
                    query.Add("email_address=" + Uri.EscapeDataString(email));
                }

                foreach (var name in new[] { "created_after", "created_before", "updated_after", "updated_before" })
                {
                    if (Values.Get(p, name) is { } date)
                    {
                        query.Add(name + "=" + (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? date : throw new InvalidOperationException($"'{date}' is not a date - 2026-10-01 ({name}).")));
                    }
                }

                if (Values.Get(p, "sort") is { } sort)
                {
                    var field = sort.TrimStart('-');
                    query.Add("sort_field=" + (field is "id" or "created_at" or "updated_at" ? field : throw new InvalidOperationException($"'{sort}' is not id, created_at or updated_at, a leading - for descending (sort).")));
                    query.Add("sort_order=" + (sort.StartsWith('-') ? "desc" : "asc"));
                }
            }

            using var client = session.Client;
            ConnectorRecord Under(JsonNode? answer, string key) => Record(answer?[key] as JsonObject ?? new JsonObject());
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case SubscriberOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Get, $"/subscribers/{id}", null, cancellationToken), "subscriber");
                    yield break;
                case CreateSubscriberOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, "/subscribers", body, cancellationToken), "subscriber");
                    yield break;
                case UpdateSubscriberOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Put, $"/subscribers/{id}", body, cancellationToken), "subscriber");
                    yield break;
                case UnsubscribeOperation:
                    await session.SendAsync(HttpMethod.Post, $"/subscribers/{id}/unsubscribe", new JsonObject(), cancellationToken);
                    yield return Done(("id", long.Parse(id!, CultureInfo.InvariantCulture)), ("unsubscribed", true));
                    yield break;
                case AddToFormOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, $"/forms/{id}/subscribers", body, cancellationToken), "subscriber");
                    yield break;
                case AddToSequenceOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, $"/sequences/{id}/subscribers", body, cancellationToken), "subscriber");
                    yield break;
                case CreateTagOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, "/tags", body, cancellationToken), "tag");
                    yield break;
                case TagSubscriberOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, $"/tags/{id}/subscribers", body, cancellationToken), "subscriber");
                    yield break;
                case UntagSubscriberOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/tags/{id}/subscribers?email_address={Uri.EscapeDataString(email)}", null, cancellationToken);
                    yield return Done(("tag", long.Parse(id!, CultureInfo.InvariantCulture)), ("email_address", email), ("untagged", true));
                    yield break;
                case CreateCustomFieldOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Post, "/custom_fields", body, cancellationToken), "custom_field");
                    yield break;
                case UpdateCustomFieldOperation:
                    yield return Under(await session.SendAsync(HttpMethod.Put, $"/custom_fields/{id}", body, cancellationToken), "custom_field");
                    yield break;
                case DeleteCustomFieldOperation:
                    await session.SendAsync(HttpMethod.Delete, $"/custom_fields/{id}", null, cancellationToken);
                    yield return Done(("id", long.Parse(id!, CultureInfo.InvariantCulture)), ("deleted", true));
                    yield break;
                default:
                    {
                        // Kit pages by a cursor and says whether there is a next page: the size fixed at the start, no next page
                        // or an empty one the end.
                        var (path, key) = operation switch
                        {
                            SubscribersOperation => ("/subscribers", "subscribers"),
                            FormsOperation => ("/forms", "forms"),
                            FormSubscribersOperation => ($"/forms/{id}/subscribers", "subscribers"),
                            SequencesOperation => ("/sequences", "sequences"),
                            SequenceSubscribersOperation => ($"/sequences/{id}/subscribers", "subscribers"),
                            TagsOperation => ("/tags", "tags"),
                            TagSubscribersOperation => ($"/tags/{id}/subscribers", "subscribers"),
                            _ => ("/custom_fields", "custom_fields"),
                        };
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? after = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"per_page={size}").Concat(after is null ? Array.Empty<string>() : new[] { "after=" + Uri.EscapeDataString(after) })), null, cancellationToken);
                            var items = (answer?[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            after = Text(answer?["pagination"]?["end_cursor"]);
                            if (items.Count == 0 || answer?["pagination"]?["has_next_page"] is not JsonValue next || !next.TryGetValue<bool>(out var more) || !more || after is not { Length: > 0 })
                            {
                                yield break;
                            }
                        }
                    }
            }
        }

        private static JsonNode? Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A record: its fields as Kit gives them, a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => (object?)d,
            var other => other.ToJsonString(Wire),
        }, StringComparer.Ordinal));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: Kit's API v4 with the key in X-Kit-Api-Key; a throttled call waiting as Kit says; a failure
        // thrown with its status and Kit's words.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string key;

            private Session(string baseUrl, string key, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.key = key;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Kit connection has no API key."), egress.CreateHttpClient(timeout));

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Add("X-Kit-Api-Key", key);
                    if (payload is not null)
                    {
                        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    return request;
                }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not Kit's - a sign-in page, a proxy's - read as no subscribers would hide that it never was.
                    throw new InvalidOperationException($"Kit answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = (json as JsonObject)?["errors"] is JsonArray errors ? string.Join("; ", errors.Select(Text).Where(text => text is not null)) : null;
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
