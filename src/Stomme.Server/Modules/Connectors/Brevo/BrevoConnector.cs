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

namespace Stomme.Modules.Connectors.Brevo
{
    /// <summary>
    /// Brevo, formerly Sendinblue (n8n's Brevo node: its contacts, contact attributes, transactional e-mail and senders):
    /// an account over Brevo's API (<c>https://api.brevo.com/v3</c>), its API key sent in Brevo's own <c>api-key</c> header. A test
    /// names the account.
    /// </summary>
    /// <remarks>
    /// A contact is its id or its e-mail address, escaped before it reaches the path; an attribute its category and name; a sender
    /// its id. <c>contacts</c>: the account's contacts, newest or oldest first, changed since a time (UTC), page by page by offset
    /// up to <c>max</c> (1,000); <c>get_contact</c>; one record each: its id, address, its lists' ids, whether it is blocked from
    /// e-mail or SMS, when it was made and changed, and each attribute a field of its own (<c>FIRSTNAME</c>). <c>create_contact</c>
    /// (attributes a JSON object, lists' ids), <c>upsert_contact</c> (made, or changed when it exists - n8n's Create or Update),
    /// <c>update_contact</c> (attributes, lists to add and to leave), <c>delete_contact</c>. <c>send_email</c>: a sender and
    /// recipients (<c>Ada &lt;ada@example.com&gt;</c>, comma-separated), copies, a reply-to, a subject and a text or HTML body - or a
    /// template and its parameters (a JSON object, or n8n's <c>key=value</c> pairs) - with tags; the message's id said.
    /// <c>attributes</c>, <c>create_attribute</c> (a normal one's type, a global or calculated one's value, a category's options),
    /// <c>update_attribute</c>, <c>delete_attribute</c>; <c>senders</c>, <c>create_sender</c>, <c>delete_sender</c>; <c>lists</c>.
    /// A throttled call waits as Brevo says; a failure is thrown with its status, Brevo's code and its message.
    /// </remarks>
    public sealed class BrevoConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.brevo.com/v3";

        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string CreateContactOperation = "create_contact";

        public const string UpsertContactOperation = "upsert_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const string SendEmailOperation = "send_email";

        public const string AttributesOperation = "attributes";

        public const string CreateAttributeOperation = "create_attribute";

        public const string UpdateAttributeOperation = "update_attribute";

        public const string DeleteAttributeOperation = "delete_attribute";

        public const string SendersOperation = "senders";

        public const string CreateSenderOperation = "create_sender";

        public const string DeleteSenderOperation = "delete_sender";

        public const string ListsOperation = "lists";

        public const int DefaultMax = 1000;

        private const int PageSize = 500;

        private static readonly Regex Named = new("^(?<name>.*?)\\s*<(?<email>[^<>\\s]+)>$", RegexOptions.CultureInvariant);

        private static readonly Regex AttributeName = new("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

        private static readonly Regex Number = new("^[0-9]{1,18}$", RegexOptions.CultureInvariant);

        private static readonly string[] Categories5 = { "normal", "transactional", "category", "calculated", "global" };

        private static readonly string[] Types = { "text", "date", "float", "boolean" };

        // JSON as written: names, bodies and their accents and markup, as they are.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            ContactsOperation, GetContactOperation, CreateContactOperation, UpsertContactOperation, UpdateContactOperation, DeleteContactOperation, SendEmailOperation,
            AttributesOperation, CreateAttributeOperation, UpdateAttributeOperation, DeleteAttributeOperation, SendersOperation, CreateSenderOperation, DeleteSenderOperation, ListsOperation,
        };

        public string ConnectorType => "brevo";

        public string DisplayName => "Brevo";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://www.brevo.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.brevo.com/reference");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static readonly ConnectorConfigField ContactField = new("contact", "Contact", ConnectorConfigFieldType.Text, Required: true, Description: "The contact's id or e-mail address.");

        private static readonly ConnectorConfigField AttributesField = new("attributes", "Attributes", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"FIRSTNAME\": \"Ada\", \"LASTNAME\": \"Lovelace\"}.");

        private static readonly ConnectorConfigField ListIdsField = new("list_ids", "Lists", ConnectorConfigFieldType.Text, Required: false, Description: "Lists' ids, comma-separated.");

        private static readonly ConnectorConfigField CategoryField = new("category", "Category", ConnectorConfigFieldType.Text, Required: false, Description: "normal, transactional, category, calculated or global.") { Default = "normal" };

        private static readonly ConnectorConfigField NameField = new("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "The attribute's name - LOYALTY_TIER.");

        private static ConnectorConfigField Max() => new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] ContactWrite(bool create) => new[]
        {
            new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: create, Description: "ada@example.com."),
            AttributesField,
            ListIdsField,
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "The account's contacts, each attribute a field - n8n's contact Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("sort", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "desc - newest first - or asc.") { Default = "desc" },
                    new ConnectorConfigField("modified_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or 2026-10-01T09:00:00+02:00."),
                    Max(),
                }),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact - n8n's contact Get.") { Parameters = new ConnectorConfigSchema(new[] { ContactField }) },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "Adds a contact - n8n's contact Create.") { Parameters = new ConnectorConfigSchema(ContactWrite(create: true)) },
            new ConnectorOperation(UpsertContactOperation, "Create or update a contact", "Adds a contact, or changes it when its address is known - n8n's contact Create or Update.") { Parameters = new ConnectorConfigSchema(ContactWrite(create: true)) },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes a contact's attributes and lists - n8n's contact Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ContactField, AttributesField, ListIdsField,
                    new ConnectorConfigField("unlink_list_ids", "Lists to leave", ConnectorConfigFieldType.Text, Required: false, Description: "Lists' ids, comma-separated."),
                }),
            },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "Deletes a contact - n8n's contact Delete.") { Parameters = new ConnectorConfigSchema(new[] { ContactField }) },
            new ConnectorOperation(SendEmailOperation, "Send an e-mail", "Sends a transactional e-mail, with a body or a template - n8n's email Send and Send Template.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("sender", "From", ConnectorConfigFieldType.Text, Required: false, Description: "Shop <shop@example.com> - a verified sender. Empty with a template: the template's."),
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "Ada <ada@example.com>, lin@example.com."),
                    new ConnectorConfigField("cc", "Copy", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("bcc", "Blind copy", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "An address."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line. A template's own, when empty."),
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Multiline, Required: false, Description: "The body as text."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Multiline, Required: false, Description: "The body as HTML."),
                    new ConnectorConfigField("template_id", "Template", ConnectorConfigFieldType.Text, Required: false, Description: "A template's id, in place of a body."),
                    new ConnectorConfigField("params", "Template parameters", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"ORDER\": \"1001\"} - or name=value pairs, comma-separated."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                }),
            },
            new ConnectorOperation(AttributesOperation, "Get attributes", "The contact attributes - n8n's attribute Get Many.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
            new ConnectorOperation(CreateAttributeOperation, "Create an attribute", "Adds a contact attribute - n8n's attribute Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    CategoryField, NameField,
                    new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "A normal attribute's - text, date, float or boolean.") { Default = "text" },
                    new ConnectorConfigField("value", "Value", ConnectorConfigFieldType.Text, Required: false, Description: "A global or calculated attribute's formula."),
                    new ConnectorConfigField("options", "Options", ConnectorConfigFieldType.Text, Required: false, Description: "A category attribute's - 1:Gold, 2:Silver."),
                }),
            },
            new ConnectorOperation(UpdateAttributeOperation, "Update an attribute", "Changes a global or calculated attribute's value, or a category's options - n8n's attribute Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    CategoryField with { Default = "calculated", Description = "calculated, global or category." }, NameField,
                    new ConnectorConfigField("value", "Value", ConnectorConfigFieldType.Text, Required: false, Description: "A global or calculated attribute's formula."),
                    new ConnectorConfigField("options", "Options", ConnectorConfigFieldType.Text, Required: false, Description: "A category attribute's - 1:Gold, 2:Silver."),
                }),
            },
            new ConnectorOperation(DeleteAttributeOperation, "Delete an attribute", "Deletes a contact attribute - n8n's attribute Delete.") { Parameters = new ConnectorConfigSchema(new[] { CategoryField, NameField }) },
            new ConnectorOperation(SendersOperation, "Get senders", "The account's senders - n8n's sender Get Many.") { Parameters = new ConnectorConfigSchema(Array.Empty<ConnectorConfigField>()) },
            new ConnectorOperation(CreateSenderOperation, "Create a sender", "Adds a sender - n8n's sender Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "Shop."),
                    new ConnectorConfigField("email", "E-mail address", ConnectorConfigFieldType.Text, Required: true, Description: "shop@example.com."),
                }),
            },
            new ConnectorOperation(DeleteSenderOperation, "Delete a sender", "Deletes a sender - n8n's sender Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("sender_id", "Sender", ConnectorConfigFieldType.Text, Required: true, Description: "The sender's id.") }),
            },
            new ConnectorOperation(ListsOperation, "Get lists", "The account's contact lists - their ids, names and how many each holds.") { Parameters = new ConnectorConfigSchema(new[] { Max() }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var key = Key(context.Credentials);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var account = await SendAsync(client, key, HttpMethod.Get, api + "/account", null, cancellationToken) as JsonObject ?? new JsonObject();
                return ConnectionTestResult.Ok($"Signed in - {Text(account["companyName"]) ?? Text(account["email"]) ?? "the account"}.");
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
            var key = Key(context.Credentials);
            var operation = context.OperationName ?? SendEmailOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Brevo has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before anything is sent.
            var max = Values.Get(parameters, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var contact = Is(operation, GetContactOperation) || Is(operation, UpdateContactOperation) || Is(operation, DeleteContactOperation)
                ? Values.Get(parameters, "contact") is { } who && (Number.IsMatch(who) || IsAddress(who)) ? Uri.EscapeDataString(who) : throw new InvalidOperationException($"'{Values.Get(parameters, "contact")}' is not a contact - its id or its e-mail address.")
                : null;
            JsonObject? body = null;
            string? url = null;
            var method = HttpMethod.Post;
            if (Is(operation, CreateContactOperation) || Is(operation, UpsertContactOperation) || Is(operation, UpdateContactOperation))
            {
                body = new JsonObject();
                if (!Is(operation, UpdateContactOperation))
                {
                    body["email"] = Values.Get(parameters, "email") is { } email && IsAddress(email) ? email : throw new InvalidOperationException($"'{Values.Get(parameters, "email")}' is not an e-mail address.");
                }

                if (Values.Get(parameters, "attributes") is { } attributes)
                {
                    body["attributes"] = ObjectOf(attributes, "attributes", "{\"FIRSTNAME\": \"Ada\"}");
                }

                if (Values.Get(parameters, "list_ids") is { } lists)
                {
                    body["listIds"] = Ids(lists, "list");
                }

                if (Is(operation, UpdateContactOperation))
                {
                    if (Values.Get(parameters, "unlink_list_ids") is { } leave)
                    {
                        body["unlinkListIds"] = Ids(leave, "list");
                    }

                    if (body.Count == 0)
                    {
                        throw new InvalidOperationException("Give something to change - attributes, lists to add or lists to leave.");
                    }

                    method = HttpMethod.Put;
                    url = $"{api}/contacts/{contact}";
                }
                else
                {
                    if (Is(operation, UpsertContactOperation))
                    {
                        body["updateEnabled"] = true;
                    }

                    url = $"{api}/contacts";
                }
            }
            else if (Is(operation, SendEmailOperation))
            {
                body = new JsonObject();
                var template = Values.Get(parameters, "template_id") is { } id ? Number.IsMatch(id) ? long.Parse(id, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{id}' is not a template's id.") : (long?)null;
                if (Values.Get(parameters, "sender") is { } sender)
                {
                    body["sender"] = Mailbox(sender, "sender");
                }
                else if (template is null)
                {
                    throw new InvalidOperationException("Give the sender - Shop <shop@example.com>.");
                }

                body["to"] = Mailboxes(Values.Get(parameters, "to") ?? throw new InvalidOperationException("Give the recipients - Ada <ada@example.com>."), "recipient");
                foreach (var (name, field) in new[] { ("cc", "cc"), ("bcc", "bcc") })
                {
                    if (Values.Get(parameters, name) is { } copies)
                    {
                        body[field] = Mailboxes(copies, name);
                    }
                }

                if (Values.Get(parameters, "reply_to") is { } replyTo)
                {
                    body["replyTo"] = Mailbox(replyTo, "reply-to");
                }

                if (Values.Get(parameters, "subject") is { } subject)
                {
                    body["subject"] = OneLine(subject, "subject");
                }

                var text = parameters.TryGetValue("text", out var plain) && !string.IsNullOrWhiteSpace(plain) ? plain : null;
                var html = parameters.TryGetValue("html", out var marked) && !string.IsNullOrWhiteSpace(marked) ? marked : null;
                if (template is { } templateId)
                {
                    body["templateId"] = templateId;
                    if (Values.Get(parameters, "params") is { } given)
                    {
                        body["params"] = Params(given);
                    }
                }
                else if (text is null && html is null)
                {
                    throw new InvalidOperationException("Give a body - text or HTML - or a template.");
                }
                else if (!body.ContainsKey("subject"))
                {
                    throw new InvalidOperationException("Give the subject.");
                }

                if (html is not null)
                {
                    body["htmlContent"] = html;
                }

                if (text is not null)
                {
                    body["textContent"] = text;
                }

                if (Values.Get(parameters, "tags") is { } tags)
                {
                    body["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
                }

                url = $"{api}/smtp/email";
            }
            else if (Is(operation, CreateAttributeOperation) || Is(operation, UpdateAttributeOperation) || Is(operation, DeleteAttributeOperation))
            {
                var category = Choice(Values.Get(parameters, "category") ?? (Is(operation, UpdateAttributeOperation) ? "calculated" : "normal"), "an attribute category", Categories5);
                if (Is(operation, UpdateAttributeOperation) && category is "normal" or "transactional")
                {
                    throw new InvalidOperationException($"A {category} attribute cannot be changed - only a calculated, global or category one.");
                }

                var name = Values.Get(parameters, "name") is { } named && AttributeName.IsMatch(named) ? named : throw new InvalidOperationException($"'{Values.Get(parameters, "name")}' is not an attribute's name - LOYALTY_TIER.");
                url = $"{api}/contacts/attributes/{category}/{Uri.EscapeDataString(name)}";
                method = Is(operation, CreateAttributeOperation) ? HttpMethod.Post : Is(operation, UpdateAttributeOperation) ? HttpMethod.Put : HttpMethod.Delete;
                if (!Is(operation, DeleteAttributeOperation))
                {
                    body = new JsonObject();
                    if (Is(operation, CreateAttributeOperation) && category == "normal")
                    {
                        body["type"] = Choice(Values.Get(parameters, "type") ?? "text", "an attribute type", Types);
                    }

                    if (category is "global" or "calculated")
                    {
                        body["value"] = Values.Get(parameters, "value") ?? throw new InvalidOperationException($"Give a {category} attribute's value - its formula.");
                    }

                    if (category == "category")
                    {
                        body["enumeration"] = Options(Values.Get(parameters, "options") ?? throw new InvalidOperationException("Give a category attribute's options - 1:Gold, 2:Silver."));
                    }
                }
            }
            else if (Is(operation, DeleteContactOperation))
            {
                url = $"{api}/contacts/{contact}";
                method = HttpMethod.Delete;
            }
            else if (Is(operation, CreateSenderOperation))
            {
                body = new JsonObject
                {
                    ["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the sender's name."), "name"),
                    ["email"] = Values.Get(parameters, "email") is { } email && IsAddress(email) ? email : throw new InvalidOperationException($"'{Values.Get(parameters, "email")}' is not an e-mail address."),
                };
                url = $"{api}/senders";
            }
            else if (Is(operation, DeleteSenderOperation))
            {
                url = $"{api}/senders/" + (Values.Get(parameters, "sender_id") is { } senderId && Number.IsMatch(senderId) ? senderId : throw new InvalidOperationException($"'{Values.Get(parameters, "sender_id")}' is not a sender's id."));
                method = HttpMethod.Delete;
            }

            var sort = Is(operation, ContactsOperation) ? Choice(Values.Get(parameters, "sort") ?? "desc", "an order", "desc", "asc") : null;
            var since = Is(operation, ContactsOperation) && Values.Get(parameters, "modified_since") is { } changed
                ? DateTimeOffset.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{changed}' is not a time (changed since) - 2026-10-01, or 2026-10-01T09:00:00+02:00.")
                : null;

            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, ContactsOperation) || Is(operation, ListsOperation))
            {
                var lists = Is(operation, ListsOperation);
                var read = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = Math.Min(lists ? 50 : PageSize, max - read);
                    var page = await SendAsync(client, key, HttpMethod.Get, lists
                        ? $"{api}/contacts/lists?limit={size.ToString(CultureInfo.InvariantCulture)}&offset={read.ToString(CultureInfo.InvariantCulture)}"
                        : $"{api}/contacts?limit={size.ToString(CultureInfo.InvariantCulture)}&offset={read.ToString(CultureInfo.InvariantCulture)}&sort={sort}{(since is null ? "" : "&modifiedSince=" + Uri.EscapeDataString(since))}", null, cancellationToken);
                    var items = page?[lists ? "lists" : "contacts"] as JsonArray ?? new JsonArray();
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        yield return lists ? ListRecord(item) : ContactRecord(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    if (items.Count < size)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, GetContactOperation))
            {
                yield return ContactRecord(await SendAsync(client, key, HttpMethod.Get, $"{api}/contacts/{contact}", null, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, AttributesOperation) || Is(operation, SendersOperation))
            {
                var senders = Is(operation, SendersOperation);
                var answer = await SendAsync(client, key, HttpMethod.Get, senders ? $"{api}/senders" : $"{api}/contacts/attributes", null, cancellationToken);
                var read = 0;
                foreach (var item in (answer?[senders ? "senders" : "attributes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    yield return senders
                        ? new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = Long(item["id"]),
                            ["name"] = Text(item["name"]),
                            ["email"] = Text(item["email"]),
                            ["active"] = item["active"] is JsonValue active && active.TryGetValue<bool>(out var on) && on,
                        })
                        : new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = Text(item["name"]),
                            ["category"] = Text(item["category"]),
                            ["type"] = Text(item["type"]),
                            ["options"] = string.Join(", ", (item["enumeration"] as JsonArray ?? new JsonArray()).Select(option => $"{Long(option?["value"])}:{Text(option?["label"])}")),
                            ["formula"] = Text(item["calculatedValue"]),
                        });
                    if (++read >= max)
                    {
                        yield break;
                    }
                }

                yield break;
            }

            var result = await SendAsync(client, key, method, url!, body, cancellationToken);
            yield return new ConnectorRecord(Is(operation, SendEmailOperation)
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["message_id"] = Text(result?["messageId"]) ?? string.Join(", ", (result?["messageIds"] as JsonArray ?? new JsonArray()).Select(Text)) }
                : Is(operation, CreateContactOperation) || Is(operation, UpsertContactOperation) || Is(operation, CreateSenderOperation)
                    ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Long(result?["id"]), [Is(operation, UpsertContactOperation) && result?["id"] is null ? "updated" : "created"] = true }
                    : new Dictionary<string, object?>(StringComparer.Ordinal) { ["done"] = true });
        }

        private static ConnectorRecord ContactRecord(JsonObject contact)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Long(contact["id"]),
                ["email"] = Text(contact["email"]),
                ["list_ids"] = string.Join(", ", (contact["listIds"] as JsonArray ?? new JsonArray()).Select(Long)),
                ["email_blacklisted"] = contact["emailBlacklisted"] is JsonValue email && email.TryGetValue<bool>(out var blocked) && blocked,
                ["sms_blacklisted"] = contact["smsBlacklisted"] is JsonValue sms && sms.TryGetValue<bool>(out var silenced) && silenced,
                ["created"] = Text(contact["createdAt"]),
                ["modified"] = Text(contact["modifiedAt"]),
            };
            foreach (var (name, value) in contact["attributes"] as JsonObject ?? new JsonObject())
            {
                fields[name] = value switch
                {
                    JsonValue text when text.TryGetValue<string>(out var s) => s,
                    JsonValue whole when whole.TryGetValue<long>(out var l) => l,
                    JsonValue real when real.TryGetValue<double>(out var d) => d,
                    JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
                    null => null,
                    _ => value.ToJsonString(Wire),
                };
            }

            return new ConnectorRecord(fields);
        }

        private static ConnectorRecord ListRecord(JsonObject list) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(list["id"]),
            ["name"] = Text(list["name"]),
            ["subscribers"] = Long(list["uniqueSubscribers"]) ?? Long(list["totalSubscribers"]),
            ["folder_id"] = Long(list["folderId"]),
        });

        // Addresses, comma-separated, each "Name <address>" or an address alone.
        private static JsonArray Mailboxes(string given, string what)
        {
            var list = new JsonArray();
            foreach (var entry in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Named.Match(entry);
                var email = match.Success ? match.Groups["email"].Value : entry;
                if (!IsAddress(email))
                {
                    throw new InvalidOperationException($"'{entry}' is not an address ({what}) - Ada <ada@example.com>, or ada@example.com.");
                }

                var mailbox = new JsonObject { ["email"] = email };
                if (match.Success && match.Groups["name"].Value.Trim('"', ' ') is { Length: > 0 } name)
                {
                    mailbox["name"] = name;
                }

                list.Add(mailbox);
            }

            return list.Count > 0 ? list : throw new InvalidOperationException($"Give an address ({what}).");
        }

        // One address, "Name <address>" or an address alone.
        private static JsonNode Mailbox(string given, string what)
        {
            var list = Mailboxes(given, what);
            return list.Count == 1 ? list[0]!.DeepClone() : throw new InvalidOperationException($"Give one address ({what}).");
        }

        private static bool IsAddress(string given) =>
            !given.Any(char.IsWhiteSpace) && given.IndexOf('@') > 0 && given.IndexOf('@') == given.LastIndexOf('@') && given.IndexOf('@') < given.Length - 1;

        private static JsonArray Ids(string given, string what)
        {
            var ids = new JsonArray();
            foreach (var part in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                ids.Add(Number.IsMatch(part) ? long.Parse(part, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{part}' is not a {what}'s id."));
            }

            return ids;
        }

        // A category attribute's options: "1:Gold, 2:Silver", each its value and its label.
        private static JsonArray Options(string given)
        {
            var options = new JsonArray();
            foreach (var part in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = part.IndexOf(':');
                options.Add(colon > 0 && Number.IsMatch(part[..colon].Trim()) && part[(colon + 1)..].Trim() is { Length: > 0 } label
                    ? new JsonObject { ["value"] = long.Parse(part[..colon].Trim(), CultureInfo.InvariantCulture), ["label"] = label }
                    : throw new InvalidOperationException($"'{part}' is not an option - 1:Gold."));
            }

            return options;
        }

        // A template's parameters: a JSON object, or n8n's name=value pairs, comma-separated.
        private static JsonObject Params(string given)
        {
            if (given.TrimStart().StartsWith('{'))
            {
                return ObjectOf(given, "template parameters", "{\"ORDER\": \"1001\"}");
            }

            var pairs = new JsonObject();
            foreach (var part in given.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.IndexOf('=');
                if (equals <= 0)
                {
                    throw new InvalidOperationException($"'{part.Trim()}' is not a parameter - ORDER=1001.");
                }

                pairs[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }

            return pairs;
        }

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

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string Key(IReadOnlyDictionary<string, string> credentials) =>
            Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Brevo connection has no API key.");

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Brevo's answer as JSON (an empty answer nothing), the key in Brevo's own header, a throttled call waiting as Brevo says; a
        // failure thrown with its status, Brevo's code and its message.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string key, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Add("api-key", key);
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
                throw new InvalidOperationException($"Brevo answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["code"]);
                var said = Text(json?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
