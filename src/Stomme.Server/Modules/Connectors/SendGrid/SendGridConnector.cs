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

namespace Stomme.Modules.Connectors.SendGrid
{
    /// <summary>
    /// SendGrid (BL-229, n8n's SendGrid node: its contacts added or changed, got, found and deleted, its lists made, got, read,
    /// renamed and deleted, and a mail sent): SendGrid's v3 API (<c>https://api.sendgrid.com/v3</c>), an API key as the bearer.
    /// A test names the account.
    /// </summary>
    /// <remarks>
    /// <c>contacts</c>: those an SGQL query finds (<c>email LIKE '%@example.com'</c>), or the latest - SendGrid answers at most
    /// 50 either way, and says how many there are: each record carries that count as <c>total</c>, so a step can tell it read them
    /// all; one record each: its id, address, names, other addresses, address lines, city, region, postal code, country, lists,
    /// custom fields (JSON) and times. <c>get_contact</c> by its id or its address. <c>upsert_contacts</c>: one contact or several
    /// - an address each (comma-separated, one line), names, an address, other addresses and custom fields for one, lists to add
    /// them to; SendGrid takes them as a job and answers its id. <c>delete_contacts</c> by their ids, or every contact with
    /// <c>all</c>. <c>lists</c> page by page by SendGrid's token up to <c>max</c>; <c>get_list</c> (with a sample of its
    /// contacts), <c>create_list</c>, <c>rename_list</c>, <c>delete_list</c> (its contacts too when said). <c>send_mail</c>: from
    /// an address and a name, to, copy and blind-copy addresses, a reply-to, a subject and a body (text or HTML) - or a dynamic
    /// template and its data (a JSON object) - categories, headers (a JSON object), an IP pool, a time to send (within 72 hours)
    /// and the sandbox; SendGrid's message id is the record. A throttled call waits as SendGrid says; a failure is thrown with its
    /// status and SendGrid's first error, its field beside it.
    /// </remarks>
    public sealed class SendGridConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.sendgrid.com/v3";

        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string UpsertContactsOperation = "upsert_contacts";

        public const string DeleteContactsOperation = "delete_contacts";

        public const string ListsOperation = "lists";

        public const string GetListOperation = "get_list";

        public const string CreateListOperation = "create_list";

        public const string RenameListOperation = "rename_list";

        public const string DeleteListOperation = "delete_list";

        public const string SendMailOperation = "send_mail";

        public const int DefaultMax = 1000;

        private const int PageSize = 1000;

        private static readonly Regex Key = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

        // JSON as written: a mail's HTML and a name's accents as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { ContactsOperation, GetContactOperation, UpsertContactsOperation, DeleteContactsOperation, ListsOperation, GetListOperation, CreateListOperation, RenameListOperation, DeleteListOperation, SendMailOperation };

        private static readonly (string Parameter, string Field)[] ContactFields =
        {
            ("first_name", "first_name"), ("last_name", "last_name"), ("address_line_1", "address_line_1"), ("address_line_2", "address_line_2"),
            ("city", "city"), ("state_province_region", "state_province_region"), ("postal_code", "postal_code"), ("country", "country"),
        };

        public string ConnectorType => "sendgrid";

        public string DisplayName => "SendGrid";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The key as SendGrid names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "API key (Settings > API Keys), SG.…" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Marketing };

        public Uri WebsiteUrl { get; } = new("https://sendgrid.com");

        public Uri ApiDocumentationUrl { get; } = new("https://www.twilio.com/docs/sendgrid/api-reference");

        private static readonly ConnectorConfigField ListField = new("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id.");

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ContactsOperation, "Get contacts", "The contacts an SGQL query finds - or the latest - at most 50, each with how many there are - n8n's contact Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "SGQL query", ConnectorConfigFieldType.Text, Required: false, Description: "email LIKE '%@example.com' AND CONTAINS(list_ids, 'a1b2'). Empty: the latest."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most - SendGrid answers 50 at most.") { Default = "50" },
                }),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact, by its id or its address - n8n's contact Get.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("contact", "Contact", ConnectorConfigFieldType.Text, Required: false, Description: "Its id."),
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "Or its address."),
                }),
            },
            new ConnectorOperation(UpsertContactsOperation, "Add or update contacts", "Adds contacts, or changes those SendGrid has by their address - n8n's contact Create or Update.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: true, Description: "An address - several comma-separated."),
                    new ConnectorConfigField("first_name", "First name", ConnectorConfigFieldType.Text, Required: false, Description: "One contact's."),
                    new ConnectorConfigField("last_name", "Last name", ConnectorConfigFieldType.Text, Required: false, Description: "One contact's."),
                    new ConnectorConfigField("alternate_emails", "Other addresses", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated, up to 5."),
                    new ConnectorConfigField("address_line_1", "Address", ConnectorConfigFieldType.Text, Required: false, Description: "Its first line."),
                    new ConnectorConfigField("address_line_2", "Address, second line", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("city", "City", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("state_province_region", "Region", ConnectorConfigFieldType.Text, Required: false, Description: "A state, a province or a region."),
                    new ConnectorConfigField("postal_code", "Postal code", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("country", "Country", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
                    new ConnectorConfigField("custom_fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON of custom fields' ids and values - {\"e1_T\": \"Gold\"}."),
                    new ConnectorConfigField("lists", "Lists", ConnectorConfigFieldType.Text, Required: false, Description: "Lists' ids to add them to, comma-separated."),
                }),
            },
            new ConnectorOperation(DeleteContactsOperation, "Delete contacts", "Deletes contacts by their ids - or every contact - n8n's contact Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("contacts", "Contacts", ConnectorConfigFieldType.Text, Required: false, Description: "Their ids, comma-separated."),
                    new ConnectorConfigField("all", "Every contact", ConnectorConfigFieldType.Boolean, Required: false, Description: "Delete every contact the account has.") { Default = "false" },
                }),
            },
            new ConnectorOperation(ListsOperation, "Get lists", "The contact lists - their ids, names and how many each holds - n8n's list Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation(GetListOperation, "Get a list", "One list - with a sample of its contacts when asked - n8n's list Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, new ConnectorConfigField("contact_sample", "With a sample", ConnectorConfigFieldType.Boolean, Required: false, Description: "Up to 50 of its contacts too.") { Default = "false" } }),
            },
            new ConnectorOperation(CreateListOperation, "Create a list", "Makes a contact list - n8n's list Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line.") }),
            },
            new ConnectorOperation(RenameListOperation, "Rename a list", "Gives a list a new name - n8n's list Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line.") }),
            },
            new ConnectorOperation(DeleteListOperation, "Delete a list", "Deletes a list - its contacts too when said - n8n's list Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, new ConnectorConfigField("delete_contacts", "Its contacts too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Delete the contacts it holds.") { Default = "false" } }),
            },
            new ConnectorOperation(SendMailOperation, "Send a mail", "Sends a mail - a body, or a dynamic template and its data - n8n's mail Send.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("from", "From", ConnectorConfigFieldType.Text, Required: true, Description: "A verified sender's address."),
                    new ConnectorConfigField("from_name", "From name", ConnectorConfigFieldType.Text, Required: false, Description: "As readers see it."),
                    new ConnectorConfigField("to", "To", ConnectorConfigFieldType.Text, Required: true, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("cc", "Copy to", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("bcc", "Blind copy to", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("reply_to", "Reply to", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
                    new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line - not with a template."),
                    new ConnectorConfigField("body", "Body", ConnectorConfigFieldType.Multiline, Required: false, Description: "The text, or HTML with html = true."),
                    new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Boolean, Required: false, Description: "The body is HTML.") { Default = "false" },
                    new ConnectorConfigField("template", "Dynamic template", ConnectorConfigFieldType.Text, Required: false, Description: "Its id, d-…, instead of a subject and body."),
                    new ConnectorConfigField("template_data", "Template data", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object - {\"name\": \"Ada\"}."),
                    new ConnectorConfigField("categories", "Categories", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated, up to 10."),
                    new ConnectorConfigField("headers", "Headers", ConnectorConfigFieldType.Multiline, Required: false, Description: "A JSON object of header names and values."),
                    new ConnectorConfigField("ip_pool", "IP pool", ConnectorConfigFieldType.Text, Required: false, Description: "Its name."),
                    new ConnectorConfigField("send_at", "Send at", ConnectorConfigFieldType.Text, Required: false, Description: "A time within 72 hours - 2026-10-13T09:00:00Z."),
                    new ConnectorConfigField("sandbox", "Sandbox", ConnectorConfigFieldType.Boolean, Required: false, Description: "Checked by SendGrid, never delivered.") { Default = "false" },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                // The key's own scopes: every key may read them, a send-only one too - a profile it may not.
                var (granted, _) = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/scopes", null, cancellationToken);
                var scopes = (granted?["scopes"] as JsonArray ?? new JsonArray()).Select(Text).Where(scope => scope is not null).ToList();
                return scopes.Count > 0
                    ? ConnectionTestResult.Ok($"The key works - {scopes.Count} scope{(scopes.Count == 1 ? "" : "s")}{(scopes.Contains("mail.send") ? ", mail.send among them" : ", not mail.send")}{(scopes.Any(scope => scope!.StartsWith("marketing.", StringComparison.Ordinal)) ? ", marketing" : "")}.")
                    : ConnectionTestResult.Fail("SendGrid answered with no scopes - check the API key.");
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
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? ContactsOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"SendGrid has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, ContactsOperation))
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : 50;
                var (found, _) = Values.Get(parameters, "query") is { } query
                    ? await SendAsync(client, token, HttpMethod.Post, api + "/marketing/contacts/search", new JsonObject { ["query"] = OneLine(query, "query") }, cancellationToken)
                    : await SendAsync(client, token, HttpMethod.Get, api + "/marketing/contacts", null, cancellationToken);
                var total = found?["contact_count"] is JsonValue count && count.TryGetValue<long>(out var all) ? all : (long?)null;
                foreach (var contact in (found?["result"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                {
                    var fields = ContactRecord(contact);
                    fields["total"] = total;
                    yield return new ConnectorRecord(fields);
                }

                yield break;
            }

            if (Is(operation, GetContactOperation))
            {
                if (Values.Get(parameters, "contact") is { } id)
                {
                    var (contact, _) = await SendAsync(client, token, HttpMethod.Get, $"{api}/marketing/contacts/{Id(id, "contact")}", null, cancellationToken);
                    yield return new ConnectorRecord(ContactRecord(contact as JsonObject ?? new JsonObject()));
                    yield break;
                }

                var email = Address(Values.Get(parameters, "email") ?? throw new InvalidOperationException("Give the contact's id - or its address."));
                var (byEmail, _) = await SendAsync(client, token, HttpMethod.Post, api + "/marketing/contacts/search/emails", new JsonObject { ["emails"] = new JsonArray(JsonValue.Create(email.ToLowerInvariant())) }, cancellationToken);
                var held = byEmail?["result"]?[email.ToLowerInvariant()]?["contact"] as JsonObject ?? throw new InvalidOperationException($"SendGrid has no contact {email}.");
                yield return new ConnectorRecord(ContactRecord(held));
                yield break;
            }

            if (Is(operation, UpsertContactsOperation))
            {
                var emails = Addresses(Values.Get(parameters, "email") ?? throw new InvalidOperationException("Give the contact's address - several comma-separated."), "email");
                var details = new JsonObject();
                foreach (var (parameter, field) in ContactFields)
                {
                    if (Values.Get(parameters, parameter) is { } value)
                    {
                        details[field] = OneLine(value, parameter.Replace('_', ' '));
                    }
                }

                if (Values.Get(parameters, "alternate_emails") is { } others)
                {
                    details["alternate_emails"] = new JsonArray(Addresses(others, "alternate_emails").Select(other => (JsonNode)JsonValue.Create(other)!).ToArray());
                }

                if (Values.Get(parameters, "custom_fields") is { } custom)
                {
                    details["custom_fields"] = Object(custom, "custom fields");
                }

                if (details.Count > 0 && emails.Count > 1)
                {
                    throw new InvalidOperationException("Names, an address and custom fields are one contact's - give one address with them.");
                }

                var contacts = new JsonArray(emails.Select(email =>
                {
                    var contact = new JsonObject { ["email"] = email };
                    foreach (var (name, value) in details)
                    {
                        contact[name] = value?.DeepClone();
                    }

                    return (JsonNode)contact;
                }).ToArray());
                var body = new JsonObject { ["contacts"] = contacts };
                if (Values.Get(parameters, "lists") is { } lists)
                {
                    body["list_ids"] = new JsonArray(Words(lists).Select(list => (JsonNode)JsonValue.Create(Id(list, "list"))!).ToArray());
                }

                var (job, _) = await SendAsync(client, token, HttpMethod.Put, api + "/marketing/contacts", body, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["job_id"] = Text(job?["job_id"]), ["contacts"] = emails.Count, ["emails"] = string.Join(", ", emails) });
                yield break;
            }

            if (Is(operation, DeleteContactsOperation))
            {
                var everyone = Values.GetBool(parameters, "all", false);
                var ids = Values.Get(parameters, "contacts") is { } given ? Words(given).Select(id => Id(id, "contact")).ToList() : new List<string>();
                if (everyone == (ids.Count > 0))
                {
                    throw new InvalidOperationException("Give the contacts' ids - or all = true for every contact - not both.");
                }

                var (job, _) = await SendAsync(client, token, HttpMethod.Delete, api + "/marketing/contacts?" + (everyone ? "delete_all_contacts=true" : "ids=" + string.Join(",", ids)), null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["job_id"] = Text(job?["job_id"]), ["deleted"] = everyone ? "all" : string.Join(", ", ids) });
                yield break;
            }

            if (Is(operation, ListsOperation))
            {
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var read = 0;
                string? pageToken = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = $"{api}/marketing/lists?page_size={Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)}{(pageToken is null ? "" : "&page_token=" + Uri.EscapeDataString(pageToken))}";
                    var (page, _) = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var list in (page?["result"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        read++;
                        yield return ListRecord(list);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    // SendGrid's next page is an address; its page_token is all it takes.
                    pageToken = Text(page?["_metadata"]?["next"]) is { } next && Regex.Match(next, "[?&]page_token=([^&]+)") is { Success: true } match
                        ? Uri.UnescapeDataString(match.Groups[1].Value)
                        : null;
                }
                while (!string.IsNullOrEmpty(pageToken));

                yield break;
            }

            if (Is(operation, CreateListOperation))
            {
                var (made, _) = await SendAsync(client, token, HttpMethod.Post, api + "/marketing/lists", new JsonObject { ["name"] = Name(parameters) }, cancellationToken);
                yield return ListRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, SendMailOperation))
            {
                var (_, messageId) = await SendAsync(client, token, HttpMethod.Post, api + "/mail/send", Mail(parameters, out var to, out var subject), cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["message_id"] = messageId, ["to"] = to, ["subject"] = subject });
                yield break;
            }

            var listId = Id(Values.Get(parameters, "list"), "list");
            if (Is(operation, GetListOperation))
            {
                var (list, _) = await SendAsync(client, token, HttpMethod.Get, $"{api}/marketing/lists/{listId}?contact_sample={(Values.GetBool(parameters, "contact_sample", false) ? "true" : "false")}", null, cancellationToken);
                var fields = ListFields(list as JsonObject ?? new JsonObject());
                if (list?["contact_sample"] is JsonArray sample)
                {
                    fields["sample"] = string.Join(", ", sample.Select(contact => Text(contact?["email"])).Where(email => email is not null));
                }

                yield return new ConnectorRecord(fields);
                yield break;
            }

            if (Is(operation, RenameListOperation))
            {
                var (renamed, _) = await SendAsync(client, token, new HttpMethod("PATCH"), $"{api}/marketing/lists/{listId}", new JsonObject { ["name"] = Name(parameters) }, cancellationToken);
                yield return ListRecord(renamed as JsonObject ?? new JsonObject());
                yield break;
            }

            // A list deleted, its contacts too when said.
            var alsoContacts = Values.GetBool(parameters, "delete_contacts", false);
            await SendAsync(client, token, HttpMethod.Delete, $"{api}/marketing/lists/{listId}?delete_contacts={(alsoContacts ? "true" : "false")}", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = listId, ["deleted"] = true, ["contacts_deleted"] = alsoContacts });
        }

        // A mail as SendGrid's /mail/send takes it: one personalization (to, copies, a subject or a template's data, a time to send),
        // from, reply-to, a body or a template, categories, headers, a pool and the sandbox.
        private static JsonObject Mail(IReadOnlyDictionary<string, string> parameters, out string to, out string? subject)
        {
            JsonArray People(IEnumerable<string> emails) => new(emails.Select(email => (JsonNode)new JsonObject { ["email"] = email }).ToArray());
            var recipients = Addresses(Values.Get(parameters, "to") ?? throw new InvalidOperationException("Give who it goes to - addresses, comma-separated."), "to");
            to = string.Join(", ", recipients);
            var personalization = new JsonObject { ["to"] = People(recipients) };
            foreach (var name in new[] { "cc", "bcc" })
            {
                if (Values.Get(parameters, name) is { } copies)
                {
                    personalization[name] = People(Addresses(copies, name));
                }
            }

            var from = new JsonObject { ["email"] = Address(Values.Get(parameters, "from") ?? throw new InvalidOperationException("Give the sender's address - a verified sender.")) };
            if (Values.Get(parameters, "from_name") is { } fromName)
            {
                from["name"] = OneLine(fromName, "from name");
            }

            var mail = new JsonObject { ["personalizations"] = new JsonArray(personalization), ["from"] = from };
            if (Values.Get(parameters, "reply_to") is { } replyTo)
            {
                mail["reply_to_list"] = People(Addresses(replyTo, "reply_to"));
            }

            subject = Values.Get(parameters, "subject") is { } given ? OneLine(given, "subject") : null;
            if (Values.Get(parameters, "template") is { } template)
            {
                if (subject is not null || Values.Get(parameters, "body") is not null)
                {
                    throw new InvalidOperationException("A dynamic template is the subject and the body - give the template, or a subject and a body.");
                }

                mail["template_id"] = Regex.IsMatch(template, "^d-[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? template : throw new InvalidOperationException($"'{template}' is not a dynamic template's id - d- and 32 hex digits.");
                if (Values.Get(parameters, "template_data") is { } data)
                {
                    personalization["dynamic_template_data"] = Object(data, "template data");
                }
            }
            else
            {
                var body = parameters.TryGetValue("body", out var text) && !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidOperationException("Give the body - or a dynamic template.");
                personalization["subject"] = subject ?? throw new InvalidOperationException("Give the subject - or a dynamic template.");
                mail["content"] = new JsonArray(new JsonObject { ["type"] = Values.GetBool(parameters, "html", false) ? "text/html" : "text/plain", ["value"] = body });
            }

            if (Values.Get(parameters, "categories") is { } categories)
            {
                var named = Words(categories).Select(category => category.Length <= 255 ? category : throw new InvalidOperationException($"The category '{category[..20]}…' is over 255 characters.")).ToList();
                mail["categories"] = named.Count <= 10 ? new JsonArray(named.Select(category => (JsonNode)JsonValue.Create(category)!).ToArray()) : throw new InvalidOperationException("Up to 10 categories.");
            }

            if (Values.Get(parameters, "headers") is { } headers)
            {
                var given2 = Object(headers, "headers");
                foreach (var (name, value) in given2)
                {
                    if (value is not JsonValue || !Regex.IsMatch(name, "^[A-Za-z0-9-]{1,100}$"))
                    {
                        throw new InvalidOperationException($"The header '{name}' is not a header's name and text.");
                    }
                }

                mail["headers"] = given2;
            }

            if (Values.Get(parameters, "ip_pool") is { } pool)
            {
                mail["ip_pool_name"] = OneLine(pool, "IP pool");
            }

            if (Values.Get(parameters, "send_at") is { } sendAt)
            {
                personalization["send_at"] = DateTimeOffset.TryParse(sendAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? moment.ToUnixTimeSeconds()
                    : throw new InvalidOperationException($"'{sendAt}' is not a time - 2026-10-13T09:00:00Z.");
            }

            if (Values.GetBool(parameters, "sandbox", false))
            {
                mail["mail_settings"] = new JsonObject { ["sandbox_mode"] = new JsonObject { ["enable"] = true } };
            }

            return mail;
        }

        private static Dictionary<string, object?> ContactRecord(JsonObject contact) => new(StringComparer.Ordinal)
        {
            ["id"] = Text(contact["id"]),
            ["email"] = Text(contact["email"]),
            ["first_name"] = Text(contact["first_name"]),
            ["last_name"] = Text(contact["last_name"]),
            ["alternate_emails"] = string.Join(", ", (contact["alternate_emails"] as JsonArray ?? new JsonArray()).Select(Text).Where(email => email is not null)),
            ["address_line_1"] = Text(contact["address_line_1"]),
            ["address_line_2"] = Text(contact["address_line_2"]),
            ["city"] = Text(contact["city"]),
            ["state_province_region"] = Text(contact["state_province_region"]),
            ["postal_code"] = Text(contact["postal_code"]),
            ["country"] = Text(contact["country"]),
            ["list_ids"] = string.Join(", ", (contact["list_ids"] as JsonArray ?? new JsonArray()).Select(Text).Where(id => id is not null)),
            ["custom_fields"] = contact["custom_fields"] is JsonObject custom ? custom.ToJsonString(Wire) : null,
            ["created_at"] = Text(contact["created_at"]),
            ["updated_at"] = Text(contact["updated_at"]),
        };

        private static ConnectorRecord ListRecord(JsonObject list) => new(ListFields(list));

        private static Dictionary<string, object?> ListFields(JsonObject list) => new(StringComparer.Ordinal)
        {
            ["id"] = Text(list["id"]),
            ["name"] = Text(list["name"]),
            ["contact_count"] = list["contact_count"] is JsonValue count && count.TryGetValue<long>(out var held) ? held : null,
        };

        private static string Name(IReadOnlyDictionary<string, string> parameters) =>
            OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the list's name."), "name");

        private static JsonObject Object(string given, string what)
        {
            try
            {
                return JsonNode.Parse(given) as JsonObject ?? throw new InvalidOperationException($"The {what} are not a JSON object - {{\"name\": \"Ada\"}}.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The {what} are not JSON - {{\"name\": \"Ada\"}}.");
            }
        }

        private static List<string> Addresses(string given, string what)
        {
            var addresses = Words(given).Select(Address).ToList();
            return addresses.Count > 0 ? addresses : throw new InvalidOperationException($"Give an address ({what}).");
        }

        private static string Address(string given) =>
            given.Trim() is var email && !email.Any(char.IsWhiteSpace) && email.IndexOf('@') > 0 && email.IndexOf('@') < email.Length - 1
                ? email
                : throw new InvalidOperationException($"'{given}' is not an address.");

        private static string Id(string? given, string what) =>
            given is not null && Key.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a SendGrid {what}'s id.");

        private static IEnumerable<string> Words(string given) => given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The SendGrid connection has no API key.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // SendGrid's answer and its message id; a throttled call waits as SendGrid says; a failure thrown with its status and
        // SendGrid's first error, its field beside it.
        private static async Task<(JsonNode? Data, string? MessageId)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
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
                throw new InvalidOperationException($"SendGrid answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = (json?["errors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var said = error is null ? null : Text(error["message"]) + (Text(error["field"]) is { Length: > 0 } field ? $" ({field})" : "");
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            var messageId = response.Headers.TryGetValues("X-Message-Id", out var values) ? values.FirstOrDefault() : null;
            return (json, messageId);
        }
    }
}
