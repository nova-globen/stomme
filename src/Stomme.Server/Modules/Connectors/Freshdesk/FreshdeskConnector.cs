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
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Freshdesk
{
    /// <summary>
    /// Freshdesk (n8n's Freshdesk node: tickets and contacts found, got, created, updated and deleted - and a reply and a note
    /// on a ticket): a helpdesk's API (<c>https://your-domain.freshdesk.com/api/v2</c>), an agent's API key by Freshdesk's Basic pair
    /// (the key and X). A test names the agent.
    /// </summary>
    /// <remarks>
    /// A ticket, a contact, an agent, a group, a company and a product are named by their id (digits), each held to that shape before it
    /// reaches the path; a status (open, pending, resolved, closed), a priority (low, medium, high, urgent) and a source (email, portal,
    /// phone, chat, feedback widget, outbound email) by their words or Freshdesk's numbers, written as Freshdesk's numbers and read back as
    /// words. <c>tickets</c>: the tickets a requester, a company and a change since a time keep - Freshdesk lists the last 30 days unless a
    /// time is given - sorted, page by page (100 a page, a page no bigger than what is left) up to <c>max</c> (1,000); one record each: its
    /// id, address in the agent's view, subject, status, priority, source, type, requester, agent, group, company, tags and times.
    /// <c>get_ticket</c> one, its description with it. <c>create_ticket</c>: a requester (an address, an id, a phone or an external id),
    /// a subject and a description (HTML), a status (open by default), a priority (low), a source (portal), a type, tags, copies, a group,
    /// an agent, a company, a product, due times and custom fields (JSON); <c>update_ticket</c> changes those given (something, or
    /// refused); <c>delete_ticket</c> moves one to the trash; <c>reply</c> answers the requester; <c>add_note</c> adds a note - private
    /// unless said. Contacts the same way: <c>contacts</c> by an address, a phone, a company, a state and a change; <c>create_contact</c> a
    /// name and an address (or a phone), a company, a job title, tags and custom fields; <c>delete_contact</c> deletes one (Freshdesk keeps
    /// it, restorable). A throttled call waits as Freshdesk says; a failure is thrown with its status, Freshdesk's description and its first
    /// field's error.
    /// </remarks>
    public sealed class FreshdeskConnector : IConnector, IConnectorExtractor
    {
        public const string TicketsOperation = "tickets";

        public const string GetTicketOperation = "get_ticket";

        public const string CreateTicketOperation = "create_ticket";

        public const string UpdateTicketOperation = "update_ticket";

        public const string DeleteTicketOperation = "delete_ticket";

        public const string ReplyOperation = "reply";

        public const string AddNoteOperation = "add_note";

        public const string ContactsOperation = "contacts";

        public const string GetContactOperation = "get_contact";

        public const string CreateContactOperation = "create_contact";

        public const string UpdateContactOperation = "update_contact";

        public const string DeleteContactOperation = "delete_contact";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        // A body's HTML as written - a description, a reply, a note - not escaped for a page it never reaches.
        private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly (string Word, int Number)[] Statuses = { ("open", 2), ("pending", 3), ("resolved", 4), ("closed", 5) };

        private static readonly (string Word, int Number)[] Priorities = { ("low", 1), ("medium", 2), ("high", 3), ("urgent", 4) };

        private static readonly (string Word, int Number)[] Sources = { ("email", 1), ("portal", 2), ("phone", 3), ("chat", 7), ("feedback_widget", 9), ("outbound_email", 10) };

        private static readonly HashSet<string> TicketSorts = new(StringComparer.Ordinal) { "created_at", "due_by", "updated_at", "status" };

        private static readonly HashSet<string> ContactStates = new(StringComparer.Ordinal) { "blocked", "deleted", "unverified", "verified" };

        public string ConnectorType => "freshdesk";

        public string DisplayName => "Freshdesk";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        /// <summary>The key as Freshdesk names it, with where to find it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API key (Profile settings > View API key)" };

        public string EndpointExample => "https://your-domain.freshdesk.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.freshworks.com/freshdesk/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.freshdesk.com/api/");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField TicketField = new("ticket", "Ticket", ConnectorConfigFieldType.Text, Required: true, Description: "The ticket's id (digits).");

        private static readonly ConnectorConfigField ContactField = new("contact", "Contact", ConnectorConfigFieldType.Text, Required: true, Description: "The contact's id (digits).");

        private static ConnectorConfigField[] TicketInput(bool create) => new[]
        {
            new ConnectorConfigField("email", "Requester's email", ConnectorConfigFieldType.Text, Required: false, Description: create ? "Who asked - an address (or give requester_id, phone or unique_external_id)." : "Another requester - an address."),
            new ConnectorConfigField("requester_id", "Requester", ConnectorConfigFieldType.Text, Required: false, Description: "A contact's id."),
            new ConnectorConfigField("phone", "Requester's phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46701740605"),
            new ConnectorConfigField("unique_external_id", "Requester's external id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for them."),
            new ConnectorConfigField("name", "Requester's name", ConnectorConfigFieldType.Text, Required: false, Description: "For a requester Freshdesk does not know yet."),
            new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: create, Description: "HTML."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: create ? "open, pending, resolved or closed. Empty: open." : "open, pending, resolved or closed."),
            new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Text, Required: false, Description: create ? "low, medium, high or urgent. Empty: low." : "low, medium, high or urgent."),
            new ConnectorConfigField("source", "Source", ConnectorConfigFieldType.Text, Required: false, Description: create ? "email, portal, phone, chat, feedback_widget or outbound_email. Empty: portal." : "email, portal, phone, chat, feedback_widget or outbound_email."),
            new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "One of the helpdesk's ticket types - Question, Incident."),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: create ? "Comma-separated." : "Comma-separated - they replace the ticket's."),
            new ConnectorConfigField("cc_emails", "Copies", ConnectorConfigFieldType.Text, Required: false, Description: "Addresses, comma-separated."),
            new ConnectorConfigField("group_id", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "A group's id."),
            new ConnectorConfigField("responder_id", "Agent", ConnectorConfigFieldType.Text, Required: false, Description: "An agent's id."),
            new ConnectorConfigField("company_id", "Company", ConnectorConfigFieldType.Text, Required: false, Description: "A company's id."),
            new ConnectorConfigField("product_id", "Product", ConnectorConfigFieldType.Text, Required: false, Description: "A product's id."),
            new ConnectorConfigField("due_by", "Due by", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-31T17:00:00Z."),
            new ConnectorConfigField("fr_due_by", "First response due by", ConnectorConfigFieldType.Text, Required: false, Description: "A time."),
            new ConnectorConfigField("custom_fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"cf_order\": \"1001\"}."),
        };

        private static ConnectorConfigField[] ContactInput(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: create ? "An address (or give phone, mobile or unique_external_id)." : "An address."),
            new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46701740605"),
            new ConnectorConfigField("mobile", "Mobile", ConnectorConfigFieldType.Text, Required: false, Description: "+46701740605"),
            new ConnectorConfigField("unique_external_id", "External id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for them."),
            new ConnectorConfigField("company_id", "Company", ConnectorConfigFieldType.Text, Required: false, Description: "A company's id."),
            new ConnectorConfigField("job_title", "Job title", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("address", "Address", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField("language", "Language", ConnectorConfigFieldType.Text, Required: false, Description: "Its code - en, sv."),
            new ConnectorConfigField("time_zone", "Time zone", ConnectorConfigFieldType.Text, Required: false, Description: "Stockholm"),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
            new ConnectorConfigField("other_emails", "Other addresses", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
            new ConnectorConfigField("custom_fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "JSON - {\"cf_tier\": \"gold\"}."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TicketsOperation, "Get tickets", "The helpdesk's tickets - one record each: its id, subject, status, priority and requester - n8n's ticket Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("requester_email", "Requester's email", ConnectorConfigFieldType.Text, Required: false, Description: "Only this person's."),
                    new ConnectorConfigField("requester_id", "Requester", ConnectorConfigFieldType.Text, Required: false, Description: "Only this contact's - an id."),
                    new ConnectorConfigField("company_id", "Company", ConnectorConfigFieldType.Text, Required: false, Description: "Only this company's - an id."),
                    new ConnectorConfigField("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z. Empty: Freshdesk's last 30 days."),
                    new ConnectorConfigField("order_by", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: "created_at, due_by, updated_at or status."),
                    new ConnectorConfigField("order_type", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetTicketOperation, "Get a ticket", "One ticket, its description with it - n8n's ticket Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }),
            },
            new ConnectorOperation(CreateTicketOperation, "Create a ticket", "Opens a ticket - n8n's ticket Create.")
            {
                Parameters = new ConnectorConfigSchema(TicketInput(create: true)),
            },
            new ConnectorOperation(UpdateTicketOperation, "Update a ticket", "Changes a ticket - n8n's ticket Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }.Concat(TicketInput(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteTicketOperation, "Delete a ticket", "Moves a ticket to the trash - n8n's ticket Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }),
            },
            new ConnectorOperation(ReplyOperation, "Reply to a ticket", "Answers the requester on a ticket.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField, new ConnectorConfigField("body", "Reply", ConnectorConfigFieldType.Multiline, Required: true, Description: "HTML.") }),
            },
            new ConnectorOperation(AddNoteOperation, "Add a note to a ticket", "A note on a ticket - private to agents unless said.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TicketField,
                    new ConnectorConfigField("body", "Note", ConnectorConfigFieldType.Multiline, Required: true, Description: "HTML."),
                    new ConnectorConfigField("private", "Private", ConnectorConfigFieldType.Text, Required: false, Description: "false lets the requester see it. Empty: private."),
                }),
            },
            new ConnectorOperation(ContactsOperation, "Get contacts", "The helpdesk's contacts - one record each: their name, address, phone and company - n8n's contact Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "Only this address."),
                    new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "Only this phone."),
                    new ConnectorConfigField("mobile", "Mobile", ConnectorConfigFieldType.Text, Required: false, Description: "Only this mobile."),
                    new ConnectorConfigField("company_id", "Company", ConnectorConfigFieldType.Text, Required: false, Description: "Only this company's - an id."),
                    new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Required: false, Description: "blocked, deleted, unverified or verified."),
                    new ConnectorConfigField("updated_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-01T00:00:00Z."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetContactOperation, "Get a contact", "One contact - n8n's contact Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ContactField }),
            },
            new ConnectorOperation(CreateContactOperation, "Create a contact", "Adds a contact - n8n's contact Create.")
            {
                Parameters = new ConnectorConfigSchema(ContactInput(create: true)),
            },
            new ConnectorOperation(UpdateContactOperation, "Update a contact", "Changes a contact - n8n's contact Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ContactField }.Concat(ContactInput(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteContactOperation, "Delete a contact", "Deletes a contact - Freshdesk keeps it, restorable - n8n's contact Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ContactField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Authorization(context.Credentials), HttpMethod.Get, api + "/agents/me", null, cancellationToken);
                return Text(me?["contact"]?["email"]) is { } email
                    ? ConnectionTestResult.Ok($"Signed in as {Text(me?["contact"]?["name"]) ?? email} ({email}).")
                    : ConnectionTestResult.Fail("Freshdesk answered without an agent - check the address and the API key.");
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
            var api = Api(context.Endpoint);
            var site = api[..^"/api/v2".Length];
            var authorization = Authorization(context.Credentials);
            var operation = context.OperationName ?? TicketsOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            Func<JsonObject, ConnectorRecord> ticket = item => Ticket(item, site);

            // What could be refused is refused before anything is sent.
            string url;
            HttpMethod method = HttpMethod.Get;
            JsonObject? body = null;
            var listed = false;
            var record = ticket;
            ConnectorRecord? done = null;
            if (Is(operation, TicketsOperation) || Is(operation, ContactsOperation))
            {
                var tickets = Is(operation, TicketsOperation);
                var query = new List<string>();
                void Add(string name, string value) => query.Add(name + "=" + Uri.EscapeDataString(value));
                if (tickets)
                {
                    if (Values.Get(parameters, "requester_email") is { } email)
                    {
                        Add("email", Email(email));
                    }

                    if (Values.Get(parameters, "requester_id") is { } requester)
                    {
                        Add("requester_id", Id(requester, "contact"));
                    }

                    if (Values.Get(parameters, "company_id") is { } company)
                    {
                        Add("company_id", Id(company, "company"));
                    }

                    if (Values.Get(parameters, "updated_since") is { } since)
                    {
                        Add("updated_since", Time(since));
                    }

                    if (Values.Get(parameters, "order_by") is { } sort)
                    {
                        Add("order_by", Word(sort, TicketSorts, "field to sort by"));
                    }

                    if (Values.Get(parameters, "order_type") is { } order)
                    {
                        Add("order_type", Word(order, new HashSet<string>(StringComparer.Ordinal) { "asc", "desc" }, "sort order"));
                    }
                }
                else
                {
                    if (Values.Get(parameters, "email") is { } email)
                    {
                        Add("email", Email(email));
                    }

                    foreach (var name in new[] { "phone", "mobile" })
                    {
                        if (Values.Get(parameters, name) is { } number)
                        {
                            Add(name, OneLine(number, name));
                        }
                    }

                    if (Values.Get(parameters, "company_id") is { } company)
                    {
                        Add("company_id", Id(company, "company"));
                    }

                    if (Values.Get(parameters, "state") is { } state)
                    {
                        Add("state", Word(state, ContactStates, "contact's state"));
                    }

                    if (Values.Get(parameters, "updated_since") is { } since)
                    {
                        Add("_updated_since", Time(since));
                    }
                }

                url = $"{api}/{(tickets ? "tickets" : "contacts")}" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
                listed = true;
                record = tickets ? ticket : Contact;
            }
            else if (Is(operation, GetTicketOperation) || Is(operation, DeleteTicketOperation))
            {
                var id = Id(Values.Get(parameters, "ticket"), "ticket");
                url = $"{api}/tickets/{id}";
                if (Is(operation, DeleteTicketOperation))
                {
                    method = HttpMethod.Delete;
                    done = Done(id, "deleted");
                }
            }
            else if (Is(operation, CreateTicketOperation) || Is(operation, UpdateTicketOperation))
            {
                var create = Is(operation, CreateTicketOperation);
                var id = create ? null : Id(Values.Get(parameters, "ticket"), "ticket");
                body = TicketChange(parameters, create);
                url = create ? $"{api}/tickets" : $"{api}/tickets/{id}";
                method = create ? HttpMethod.Post : HttpMethod.Put;
            }
            else if (Is(operation, ReplyOperation) || Is(operation, AddNoteOperation))
            {
                var id = Id(Values.Get(parameters, "ticket"), "ticket");
                var text = Values.Get(parameters, "body") ?? throw new InvalidOperationException(Is(operation, ReplyOperation) ? "Give the reply." : "Give the note.");
                body = new JsonObject { ["body"] = text };
                if (Is(operation, AddNoteOperation))
                {
                    body["private"] = Flag(Values.Get(parameters, "private") ?? "true", "private");
                }

                url = $"{api}/tickets/{id}/{(Is(operation, ReplyOperation) ? "reply" : "notes")}";
                method = HttpMethod.Post;
                record = Conversation;
            }
            else if (Is(operation, GetContactOperation) || Is(operation, DeleteContactOperation))
            {
                var id = Id(Values.Get(parameters, "contact"), "contact");
                url = $"{api}/contacts/{id}";
                record = Contact;
                if (Is(operation, DeleteContactOperation))
                {
                    method = HttpMethod.Delete;
                    done = Done(id, "deleted");
                }
            }
            else if (Is(operation, CreateContactOperation) || Is(operation, UpdateContactOperation))
            {
                var create = Is(operation, CreateContactOperation);
                var id = create ? null : Id(Values.Get(parameters, "contact"), "contact");
                body = ContactChange(parameters, create);
                url = create ? $"{api}/contacts" : $"{api}/contacts/{id}";
                method = create ? HttpMethod.Post : HttpMethod.Put;
                record = Contact;
            }
            else
            {
                throw new InvalidOperationException($"Freshdesk has no operation '{operation}' - tickets, get_ticket, create_ticket, update_ticket, delete_ticket, reply, add_note, contacts, get_contact, create_contact, update_contact or delete_contact.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (!listed)
            {
                var answer = await SendAsync(client, authorization, method, Is(operation, GetTicketOperation) ? url + "?include=requester" : url, body, cancellationToken);
                yield return done ?? record(answer as JsonObject ?? new JsonObject());
                yield break;
            }

            // Page by page, a page no bigger than what is left, until a short page or the most asked for.
            var read = 0;
            for (var page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = Math.Min(PageSize, max - read);
                var items = (await SendAsync(client, authorization, HttpMethod.Get, url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "per_page=" + size.ToString(CultureInfo.InvariantCulture) + "&page=" + page.ToString(CultureInfo.InvariantCulture), null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                foreach (var item in items)
                {
                    read++;
                    yield return record(item);
                    if (read >= max)
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

        // A ticket's fields as Freshdesk's: words made its numbers, ids numbers, lists lists, times in UTC; a create's status, priority and
        // source by default, its requester required.
        private static JsonObject TicketChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var ticket = new JsonObject();
            if (Values.Get(parameters, "email") is { } email)
            {
                ticket["email"] = Email(email);
            }

            if (Values.Get(parameters, "requester_id") is { } requester)
            {
                ticket["requester_id"] = Number(requester, "contact");
            }

            AddLine(ticket, "phone", Values.Get(parameters, "phone"));
            AddLine(ticket, "unique_external_id", Values.Get(parameters, "unique_external_id"));
            if (create && ticket.Count == 0)
            {
                throw new InvalidOperationException("Give the requester - an address, a requester_id, a phone or a unique_external_id.");
            }

            AddLine(ticket, "name", Values.Get(parameters, "name"));
            if (create || Values.Get(parameters, "subject") is not null)
            {
                ticket["subject"] = OneLine(Values.Get(parameters, "subject") ?? throw new InvalidOperationException("Give the ticket's subject."), "subject");
            }

            if (create || Values.Get(parameters, "description") is not null)
            {
                ticket["description"] = Values.Get(parameters, "description") ?? throw new InvalidOperationException("Give the ticket's description.");
            }

            foreach (var (name, words, what, byDefault) in new[] { ("status", Statuses, "status", "open"), ("priority", Priorities, "priority", "low"), ("source", Sources, "source", "portal") })
            {
                if (Values.Get(parameters, name) is { } given)
                {
                    ticket[name] = Code(given, words, what);
                }
                else if (create)
                {
                    ticket[name] = Code(byDefault, words, what);
                }
            }

            AddLine(ticket, "type", Values.Get(parameters, "type"));
            if (Values.Get(parameters, "tags") is { } tags)
            {
                ticket["tags"] = List(tags, "tag");
            }

            if (Values.Get(parameters, "cc_emails") is { } copies)
            {
                ticket["cc_emails"] = new JsonArray(List(copies, "copy").Select(copy => (JsonNode)JsonValue.Create(Email(copy.GetValue<string>()))!).ToArray());
            }

            foreach (var (name, what) in new[] { ("group_id", "group"), ("responder_id", "agent"), ("company_id", "company"), ("product_id", "product") })
            {
                if (Values.Get(parameters, name) is { } id)
                {
                    ticket[name] = Number(id, what);
                }
            }

            foreach (var name in new[] { "due_by", "fr_due_by" })
            {
                if (Values.Get(parameters, name) is { } time)
                {
                    ticket[name] = Time(time);
                }
            }

            if (Values.Get(parameters, "custom_fields") is { } custom)
            {
                ticket["custom_fields"] = CustomFields(custom);
            }

            return create || ticket.Count > 0 ? ticket : throw new InvalidOperationException("Give something to change on the ticket.");
        }

        private static JsonObject ContactChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var contact = new JsonObject();
            if (create || Values.Get(parameters, "name") is not null)
            {
                contact["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the contact's name."), "name");
            }

            if (Values.Get(parameters, "email") is { } email)
            {
                contact["email"] = Email(email);
            }

            foreach (var name in new[] { "phone", "mobile", "unique_external_id", "job_title", "description", "address", "language", "time_zone" })
            {
                AddLine(contact, name, Values.Get(parameters, name));
            }

            if (create && !new[] { "email", "phone", "mobile", "unique_external_id" }.Any(contact.ContainsKey))
            {
                throw new InvalidOperationException("Give the contact's address - or a phone, a mobile or a unique_external_id.");
            }

            if (Values.Get(parameters, "company_id") is { } company)
            {
                contact["company_id"] = Number(company, "company");
            }

            if (Values.Get(parameters, "tags") is { } tags)
            {
                contact["tags"] = List(tags, "tag");
            }

            if (Values.Get(parameters, "other_emails") is { } others)
            {
                contact["other_emails"] = new JsonArray(List(others, "address").Select(other => (JsonNode)JsonValue.Create(Email(other.GetValue<string>()))!).ToArray());
            }

            if (Values.Get(parameters, "custom_fields") is { } custom)
            {
                contact["custom_fields"] = CustomFields(custom);
            }

            return create || contact.Count > 0 ? contact : throw new InvalidOperationException("Give something to change on the contact.");
        }

        private static JsonObject CustomFields(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The custom fields are not JSON - {\"cf_order\": \"1001\"}.");
            }

            return parsed is JsonObject fields && fields.Count > 0 ? fields : throw new InvalidOperationException("The custom fields are a JSON object of the helpdesk's fields - {\"cf_order\": \"1001\"}.");
        }

        private static ConnectorRecord Done(string id, string what) => new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id, CultureInfo.InvariantCulture), [what] = true });

        private static ConnectorRecord Ticket(JsonObject ticket, string site) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(ticket["id"]),
            ["url"] = Long(ticket["id"]) is { } id ? $"{site}/a/tickets/{id.ToString(CultureInfo.InvariantCulture)}" : null,
            ["subject"] = Text(ticket["subject"]),
            ["status"] = Word(ticket["status"], Statuses),
            ["priority"] = Word(ticket["priority"], Priorities),
            ["source"] = Word(ticket["source"], Sources),
            ["type"] = Text(ticket["type"]),
            ["requester_id"] = Long(ticket["requester_id"]),
            ["requester_email"] = Text(ticket["requester"]?["email"]),
            ["responder_id"] = Long(ticket["responder_id"]),
            ["group_id"] = Long(ticket["group_id"]),
            ["company_id"] = Long(ticket["company_id"]),
            ["tags"] = Joined(ticket["tags"]),
            ["description"] = Text(ticket["description_text"]),
            ["custom_fields"] = ticket["custom_fields"]?.ToJsonString() ?? "{}",
            ["due_by"] = Text(ticket["due_by"]),
            ["created_at"] = Text(ticket["created_at"]),
            ["updated_at"] = Text(ticket["updated_at"]),
        });

        private static ConnectorRecord Contact(JsonObject contact) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(contact["id"]),
            ["name"] = Text(contact["name"]),
            ["email"] = Text(contact["email"]),
            ["phone"] = Text(contact["phone"]),
            ["mobile"] = Text(contact["mobile"]),
            ["company_id"] = Long(contact["company_id"]),
            ["job_title"] = Text(contact["job_title"]),
            ["active"] = contact["active"] is JsonValue active && active.TryGetValue<bool>(out var isActive) && isActive,
            ["tags"] = Joined(contact["tags"]),
            ["custom_fields"] = contact["custom_fields"]?.ToJsonString() ?? "{}",
            ["created_at"] = Text(contact["created_at"]),
            ["updated_at"] = Text(contact["updated_at"]),
        });

        private static ConnectorRecord Conversation(JsonObject conversation) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Long(conversation["id"]),
            ["ticket_id"] = Long(conversation["ticket_id"]),
            ["private"] = conversation["private"] is JsonValue hidden && hidden.TryGetValue<bool>(out var isPrivate) && isPrivate,
            ["body"] = Text(conversation["body_text"]) ?? Text(conversation["body"]),
            ["created_at"] = Text(conversation["created_at"]),
        });

        // The helpdesk's API: its own address (any page of it cut back), /api/v2.
        private static string Api(string endpoint)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint?.Trim(), out var helpdesk) || (helpdesk.Scheme != Uri.UriSchemeHttps && helpdesk.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Freshdesk helpdesk's address - https://your-domain.freshdesk.com.");
            }

            return helpdesk.GetLeftPart(UriPartial.Authority) + "/api/v2";
        }

        // Freshdesk's Basic pair: the API key and X.
        private static AuthenticationHeaderValue Authorization(IReadOnlyDictionary<string, string> credentials)
        {
            var key = credentials.TryGetValue("api-key", out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : throw new InvalidOperationException("The Freshdesk connection has no API key.");
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":X")));
        }

        // A word of Freshdesk's, or its number, as the number Freshdesk takes.
        private static int Code(string given, (string Word, int Number)[] words, string what)
        {
            var written = given.Trim().ToLowerInvariant().Replace(' ', '_');
            foreach (var (word, number) in words)
            {
                if (written == word || written == number.ToString(CultureInfo.InvariantCulture))
                {
                    return number;
                }
            }

            throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words.Select(pair => pair.Word))}.");
        }

        private static string? Word(JsonNode? node, (string Word, int Number)[] words) =>
            Long(node) is { } number ? words.Where(pair => pair.Number == number).Select(pair => pair.Word).FirstOrDefault() ?? number.ToString(CultureInfo.InvariantCulture) : null;

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Freshdesk {what}'s id (digits).");

        private static long Number(string given, string what) => long.Parse(Id(given, what), CultureInfo.InvariantCulture);

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words)}.");

        private static string Email(string given) =>
            given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address.");

        private static bool Flag(string given, string what) => given.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"'{given}' is not true or false ({what})."),
        };

        private static string Time(string given) =>
            DateTimeOffset.TryParse(given.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-01T00:00:00Z.");

        private static JsonArray List(string given, string what) =>
            new(given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => (JsonNode)JsonValue.Create(OneLine(item, what))!).ToArray());

        private static void AddLine(JsonObject input, string name, string? value)
        {
            if (value is not null)
            {
                input[name] = OneLine(value, name.Replace('_', ' '));
            }
        }

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string? Joined(JsonNode? list) => list is JsonArray items ? string.Join(", ", items.Select(Text).Where(item => item is not null)) : null;

        private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Freshdesk's answer as JSON (none for a 204); a throttled call waits as Freshdesk says; a failure thrown with its status,
        // Freshdesk's description or message, and its first field's error.
        private static async Task<JsonNode?> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Relaxed);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = authorization;
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
                throw new InvalidOperationException($"Freshdesk answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var field = (json?["errors"] as JsonArray)?.OfType<JsonObject>().Select(error => (Text(error["field"]), Text(error["message"]))).FirstOrDefault(error => error.Item2 is not null);
                var fieldError = field is { } named ? (string.IsNullOrEmpty(named.Item1) ? named.Item2 : named.Item1 + ": " + named.Item2) : null;
                var words = string.Join(" - ", new[] { Text(json?["description"]) ?? Text(json?["message"]), fieldError }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return json;
        }
    }
}
