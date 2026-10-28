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

namespace Stomme.Modules.Connectors.Zendesk
{
    /// <summary>
    /// Zendesk (BL-229, n8n's Zendesk node: tickets found, got, created, updated and deleted, users and organizations found, got
    /// and created): an account's Support API (<c>https://your-subdomain.zendesk.com/api/v2</c>), an agent's email and an API
    /// token by Zendesk's own Basic pair (<c>{email}/token</c> and the token). A test names the agent.
    /// </summary>
    /// <remarks>
    /// A ticket, a user, an organization and a group are named by their id (digits), each held to that shape before it reaches the
    /// path. <c>tickets</c>: the tickets Zendesk's search finds - its own <c>query</c> syntax, plus a status, a priority, a group and
    /// a requester's address - sorted as asked, page by page by Zendesk's <c>next_page</c> (only while it stays the account's own
    /// search) up to <c>max</c>; one record each: its id, its address in the agent's view, subject, description, status, priority,
    /// type, requester, assignee, group and organization ids, tags (comma-separated), external id, channel, custom fields (JSON)
    /// and its times. Zendesk's search answers at most 1,000. <c>get_ticket</c> one. <c>create_ticket</c>: a subject and the first
    /// comment, a requester by address (with a name), a status, a priority, a type, tags, a group, an assignee by address, an
    /// external id and custom fields (JSON, Zendesk's id and value pairs); <c>update_ticket</c> changes those given, adding a
    /// public reply or an internal note (something, or refused); <c>delete_ticket</c> removes one. <c>users</c>: the users Zendesk's
    /// user search finds; <c>get_user</c>, <c>create_user</c> (a name, an address, a role, a phone, an organization and an external
    /// id). <c>organizations</c>: the account's organizations; <c>get_organization</c>, <c>create_organization</c> (a name, domains
    /// and tags). A throttled call waits as Zendesk says; a failure is thrown with its status and Zendesk's error, its details
    /// beside it.
    /// </remarks>
    public sealed class ZendeskConnector : IConnector, IConnectorExtractor
    {
        public const string TicketsOperation = "tickets";

        public const string GetTicketOperation = "get_ticket";

        public const string CreateTicketOperation = "create_ticket";

        public const string UpdateTicketOperation = "update_ticket";

        public const string DeleteTicketOperation = "delete_ticket";

        public const string UsersOperation = "users";

        public const string GetUserOperation = "get_user";

        public const string CreateUserOperation = "create_user";

        public const string OrganizationsOperation = "organizations";

        public const string GetOrganizationOperation = "get_organization";

        public const string CreateOrganizationOperation = "create_organization";

        public const int DefaultMax = 1000;

        private static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "new", "open", "pending", "hold", "solved", "closed" };

        private static readonly HashSet<string> Priorities = new(StringComparer.Ordinal) { "low", "normal", "high", "urgent" };

        private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "question", "incident", "problem", "task" };

        private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "end-user", "agent", "admin" };

        private static readonly HashSet<string> SortFields = new(StringComparer.Ordinal) { "created_at", "updated_at", "priority", "status", "ticket_type" };

        public string ConnectorType => "zendesk";

        public string DisplayName => "Zendesk";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        /// <summary>The Basic pair as Zendesk names it: an agent's address and an API token.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "Agent's email address", ["password"] = "API token" };

        public string EndpointExample => "https://your-subdomain.zendesk.com";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://www.zendesk.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.zendesk.com/api-reference/ticketing/introduction/");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField TicketField = new("ticket", "Ticket", ConnectorConfigFieldType.Text, Required: true, Description: "The ticket's id (digits).");

        private static ConnectorConfigField[] TicketInput(bool create) => new[]
        {
            new ConnectorConfigField("subject", "Subject", ConnectorConfigFieldType.Text, Required: false, Description: "One line."),
            new ConnectorConfigField(create ? "description" : "comment", create ? "Description" : "Public reply", ConnectorConfigFieldType.Multiline, Required: create,
                Description: create ? "The ticket's first comment." : "A reply the requester sees."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "new, open, pending, hold, solved or closed."),
            new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Text, Required: false, Description: "low, normal, high or urgent."),
            new ConnectorConfigField("type", "Type", ConnectorConfigFieldType.Text, Required: false, Description: "question, incident, problem or task."),
            new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: create ? "Comma-separated." : "Comma-separated - replacing the ticket's."),
            new ConnectorConfigField("group_id", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "A group's id."),
            new ConnectorConfigField("assignee_email", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "An agent's address."),
            new ConnectorConfigField("external_id", "External id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for it."),
            new ConnectorConfigField("custom_fields", "Custom fields", ConnectorConfigFieldType.Multiline, Required: false, Description: "Zendesk's JSON - [{\"id\": 360001, \"value\": \"gold\"}]."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TicketsOperation, "Get tickets", "The tickets Zendesk's search finds - one record each: its id, subject, status, priority, requester and tags - n8n's ticket Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: false, Description: "Zendesk's search syntax - tags:vip created>2026-10-01."),
                    new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "new, open, pending, hold, solved or closed."),
                    new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Text, Required: false, Description: "low, normal, high or urgent."),
                    new ConnectorConfigField("group_id", "Group", ConnectorConfigFieldType.Text, Required: false, Description: "A group's id."),
                    new ConnectorConfigField("requester", "Requester", ConnectorConfigFieldType.Text, Required: false, Description: "Only this person's - an address."),
                    new ConnectorConfigField("sort_by", "Sort by", ConnectorConfigFieldType.Text, Required: false, Description: "created_at, updated_at, priority, status or ticket_type."),
                    new ConnectorConfigField("sort_order", "Order", ConnectorConfigFieldType.Text, Required: false, Description: "asc or desc."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetTicketOperation, "Get a ticket", "One ticket - n8n's ticket Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }),
            },
            new ConnectorOperation(CreateTicketOperation, "Create a ticket", "Adds a ticket - one record: the ticket - n8n's ticket Create.")
            {
                Parameters = new ConnectorConfigSchema(TicketInput(create: true).Concat(new[]
                {
                    new ConnectorConfigField("requester_email", "Requester", ConnectorConfigFieldType.Text, Required: false, Description: "Who asked - an address. Empty: you."),
                    new ConnectorConfigField("requester_name", "Requester's name", ConnectorConfigFieldType.Text, Required: false, Description: "Their name, for a requester Zendesk does not know yet."),
                }).ToArray()),
            },
            new ConnectorOperation(UpdateTicketOperation, "Update a ticket", "Changes a ticket - its fields, a public reply or an internal note - n8n's ticket Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }.Concat(TicketInput(create: false)).Concat(new[]
                {
                    new ConnectorConfigField("internal_note", "Internal note", ConnectorConfigFieldType.Multiline, Required: false, Description: "A note only agents see."),
                }).ToArray()),
            },
            new ConnectorOperation(DeleteTicketOperation, "Delete a ticket", "Removes a ticket - n8n's ticket Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TicketField }),
            },
            new ConnectorOperation(UsersOperation, "Get users", "The users Zendesk's user search finds - an address, a name or its syntax - n8n's user Search.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("query", "Search", ConnectorConfigFieldType.Text, Required: true, Description: "ada@example.com, a name, or role:agent."), MaxField }),
            },
            new ConnectorOperation(GetUserOperation, "Get a user", "One user - n8n's user Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: true, Description: "The user's id (digits).") }),
            },
            new ConnectorOperation(CreateUserOperation, "Create a user", "Adds a user - n8n's user Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("email", "Email", ConnectorConfigFieldType.Text, Required: false, Description: "An address."),
                    new ConnectorConfigField("role", "Role", ConnectorConfigFieldType.Text, Required: false, Description: "end-user, agent or admin."),
                    new ConnectorConfigField("phone", "Phone", ConnectorConfigFieldType.Text, Required: false, Description: "+46701740605"),
                    new ConnectorConfigField("organization_id", "Organization", ConnectorConfigFieldType.Text, Required: false, Description: "An organization's id."),
                    new ConnectorConfigField("external_id", "External id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for them."),
                }),
            },
            new ConnectorOperation(OrganizationsOperation, "Get organizations", "The account's organizations - n8n's organization Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { MaxField }),
            },
            new ConnectorOperation(GetOrganizationOperation, "Get an organization", "One organization - n8n's organization Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("organization", "Organization", ConnectorConfigFieldType.Text, Required: true, Description: "The organization's id (digits).") }),
            },
            new ConnectorOperation(CreateOrganizationOperation, "Create an organization", "Adds an organization - n8n's organization Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line."),
                    new ConnectorConfigField("domain_names", "Domains", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated - acme.com."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Comma-separated."),
                    new ConnectorConfigField("external_id", "External id", ConnectorConfigFieldType.Text, Required: false, Description: "Your own id for it."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Authorization(context.Credentials), HttpMethod.Get, api + "/users/me.json", null, cancellationToken);
                var user = me?["user"];
                return user?["id"] is JsonValue id && id.TryGetValue<long>(out var number) && number > 0
                    ? ConnectionTestResult.Ok($"Signed in as {Text(user["name"])} ({Text(user["role"])}).")
                    : ConnectionTestResult.Fail("Zendesk answered as an anonymous user - check the address and the API token.");
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
            var authorization = Authorization(context.Credentials);
            var operation = context.OperationName ?? TicketsOperation;
            var parameters = context.Parameters;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;

            // What could be refused is refused before anything is sent.
            string url;
            HttpMethod method = HttpMethod.Get;
            JsonObject? body = null;
            string? listed = null;
            Func<JsonObject, ConnectorRecord> record = Ticket;
            if (Is(operation, TicketsOperation))
            {
                url = $"{api}/search.json?query={Uri.EscapeDataString(TicketSearch(parameters))}";
                if (Values.Get(parameters, "sort_by") is { } sortBy)
                {
                    url += "&sort_by=" + Word(sortBy, SortFields, "field to sort by");
                }

                if (Values.Get(parameters, "sort_order") is { } order)
                {
                    url += "&sort_order=" + Word(order, new HashSet<string>(StringComparer.Ordinal) { "asc", "desc" }, "sort order");
                }

                listed = "results";
            }
            else if (Is(operation, GetTicketOperation) || Is(operation, DeleteTicketOperation))
            {
                url = $"{api}/tickets/{Id(Values.Get(parameters, "ticket"), "ticket")}.json";
                method = Is(operation, DeleteTicketOperation) ? HttpMethod.Delete : HttpMethod.Get;
            }
            else if (Is(operation, CreateTicketOperation))
            {
                var ticket = TicketChange(parameters, create: true);
                url = $"{api}/tickets.json";
                method = HttpMethod.Post;
                body = new JsonObject { ["ticket"] = ticket };
            }
            else if (Is(operation, UpdateTicketOperation))
            {
                var id = Id(Values.Get(parameters, "ticket"), "ticket");
                var ticket = TicketChange(parameters, create: false);
                if (ticket.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - a field, a public reply or an internal note.");
                }

                url = $"{api}/tickets/{id}.json";
                method = HttpMethod.Put;
                body = new JsonObject { ["ticket"] = ticket };
            }
            else if (Is(operation, UsersOperation))
            {
                var query = Values.Get(parameters, "query") ?? throw new InvalidOperationException("Give what to search for - an address, a name or Zendesk's syntax.");
                url = $"{api}/users/search.json?query={Uri.EscapeDataString(query.Trim())}";
                listed = "users";
                record = User;
            }
            else if (Is(operation, GetUserOperation))
            {
                url = $"{api}/users/{Id(Values.Get(parameters, "user"), "user")}.json";
                record = User;
            }
            else if (Is(operation, CreateUserOperation))
            {
                var user = new JsonObject { ["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the user's name."), "name") };
                if (Values.Get(parameters, "email") is { } email)
                {
                    user["email"] = Email(email);
                }

                if (Values.Get(parameters, "role") is { } role)
                {
                    user["role"] = Word(role, Roles, "role");
                }

                AddLine(user, "phone", Values.Get(parameters, "phone"));
                AddLine(user, "external_id", Values.Get(parameters, "external_id"));
                if (Values.Get(parameters, "organization_id") is { } organization)
                {
                    user["organization_id"] = long.Parse(Id(organization, "organization"), CultureInfo.InvariantCulture);
                }

                url = $"{api}/users.json";
                method = HttpMethod.Post;
                body = new JsonObject { ["user"] = user };
                record = User;
            }
            else if (Is(operation, OrganizationsOperation))
            {
                url = $"{api}/organizations.json";
                listed = "organizations";
                record = Organization;
            }
            else if (Is(operation, GetOrganizationOperation))
            {
                url = $"{api}/organizations/{Id(Values.Get(parameters, "organization"), "organization")}.json";
                record = Organization;
            }
            else if (Is(operation, CreateOrganizationOperation))
            {
                var organization = new JsonObject { ["name"] = OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the organization's name."), "name") };
                if (Values.Get(parameters, "domain_names") is { } domains)
                {
                    organization["domain_names"] = List(domains, "domain");
                }

                if (Values.Get(parameters, "tags") is { } tags)
                {
                    organization["tags"] = List(tags, "tag");
                }

                AddLine(organization, "external_id", Values.Get(parameters, "external_id"));
                url = $"{api}/organizations.json";
                method = HttpMethod.Post;
                body = new JsonObject { ["organization"] = organization };
                record = Organization;
            }
            else
            {
                throw new InvalidOperationException($"Zendesk has no operation '{operation}' - tickets, get_ticket, create_ticket, update_ticket, delete_ticket, users, get_user, create_user, organizations, get_organization or create_organization.");
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (listed is null)
            {
                var answer = await SendAsync(client, authorization, method, url, body, cancellationToken);
                if (method == HttpMethod.Delete)
                {
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(Values.Get(parameters, "ticket")!.Trim(), CultureInfo.InvariantCulture), ["deleted"] = true });
                    yield break;
                }

                var found = (answer?["ticket"] ?? answer?["user"] ?? answer?["organization"]) as JsonObject;
                yield return record(found ?? new JsonObject());
                yield break;
            }

            // Zendesk's next page - the same resource under the account's own address, so the credentials go nowhere else.
            var resource = new Uri(url).GetLeftPart(UriPartial.Path);
            var read = 0;
            string? next = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "per_page=" + Math.Min(100, max).ToString(CultureInfo.InvariantCulture);
            while (next is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await SendAsync(client, authorization, HttpMethod.Get, next, null, cancellationToken);
                foreach (var item in (page?[listed] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (listed == "results" && Text(item["result_type"]) is { } kind && kind != "ticket")
                    {
                        continue;
                    }

                    read++;
                    yield return record(item);
                    if (read >= max)
                    {
                        yield break;
                    }
                }

                next = Text(page?["next_page"]) is { } link && link.StartsWith(resource + "?", StringComparison.Ordinal) ? link : null;
            }
        }

        // Zendesk's search for tickets: type:ticket, the query as written, and each filter given as a term - a status and a
        // priority held to Zendesk's words, a group to digits, a requester's address checked.
        private static string TicketSearch(IReadOnlyDictionary<string, string> parameters)
        {
            var terms = new List<string> { "type:ticket" };
            if (Values.Get(parameters, "query") is { } written)
            {
                terms.Add(written.Trim());
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                terms.Add("status:" + Word(status, Statuses, "status"));
            }

            if (Values.Get(parameters, "priority") is { } priority)
            {
                terms.Add("priority:" + Word(priority, Priorities, "priority"));
            }

            if (Values.Get(parameters, "group_id") is { } group)
            {
                terms.Add("group:" + Id(group, "group"));
            }

            if (Values.Get(parameters, "requester") is { } requester)
            {
                terms.Add("requester:" + Email(requester));
            }

            return string.Join(" ", terms);
        }

        // A ticket's fields as Zendesk's: those given, its first comment or a reply and a note as comments, words held to Zendesk's.
        private static JsonObject TicketChange(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var ticket = new JsonObject();
            AddLine(ticket, "subject", Values.Get(parameters, "subject"));
            if (create)
            {
                ticket["comment"] = new JsonObject { ["body"] = Values.Get(parameters, "description") ?? throw new InvalidOperationException("Give the ticket's description - its first comment.") };
                if (Values.Get(parameters, "requester_email") is { } requester)
                {
                    var who = new JsonObject { ["email"] = Email(requester) };
                    AddLine(who, "name", Values.Get(parameters, "requester_name"));
                    ticket["requester"] = who;
                }
            }
            else if (Values.Get(parameters, "comment") is not null && Values.Get(parameters, "internal_note") is not null)
            {
                throw new InvalidOperationException("Give a public reply or an internal note - one change adds one comment.");
            }
            else if (Values.Get(parameters, "comment") is { } publicReply)
            {
                ticket["comment"] = new JsonObject { ["body"] = publicReply, ["public"] = true };
            }
            else if (Values.Get(parameters, "internal_note") is { } note)
            {
                ticket["comment"] = new JsonObject { ["body"] = note, ["public"] = false };
            }

            foreach (var (field, words, what) in new[] { ("status", Statuses, "status"), ("priority", Priorities, "priority"), ("type", Types, "type") })
            {
                if (Values.Get(parameters, field) is { } value)
                {
                    ticket[field] = Word(value, words, what);
                }
            }

            if (Values.Get(parameters, "tags") is { } tags)
            {
                ticket["tags"] = List(tags, "tag");
            }

            if (Values.Get(parameters, "group_id") is { } group)
            {
                ticket["group_id"] = long.Parse(Id(group, "group"), CultureInfo.InvariantCulture);
            }

            if (Values.Get(parameters, "assignee_email") is { } assignee)
            {
                ticket["assignee_email"] = Email(assignee);
            }

            AddLine(ticket, "external_id", Values.Get(parameters, "external_id"));
            if (Values.Get(parameters, "custom_fields") is { } custom)
            {
                ticket["custom_fields"] = CustomFields(custom);
            }

            return ticket;
        }

        // Zendesk's custom fields: a JSON list of objects, each its id (a number) and its value.
        private static JsonArray CustomFields(string given)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(given);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The custom fields are not JSON - [{\"id\": 360001, \"value\": \"gold\"}].");
            }

            if (parsed is not JsonArray list || list.Any(item => item is not JsonObject field || field["id"] is not JsonValue id || !id.TryGetValue<long>(out _) || !field.ContainsKey("value")))
            {
                throw new InvalidOperationException("The custom fields are a list of an id (a number) and a value each - [{\"id\": 360001, \"value\": \"gold\"}].");
            }

            return list;
        }

        private static ConnectorRecord Ticket(JsonObject ticket) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(ticket["id"]),
            ["url"] = Text(ticket["url"]) is { } api && Number(ticket["id"]) is { } id ? api[..api.IndexOf("/api/v2/", StringComparison.Ordinal)] + "/agent/tickets/" + id.ToString(CultureInfo.InvariantCulture) : null,
            ["subject"] = Text(ticket["subject"]),
            ["description"] = Text(ticket["description"]),
            ["status"] = Text(ticket["status"]),
            ["priority"] = Text(ticket["priority"]),
            ["type"] = Text(ticket["type"]),
            ["requester_id"] = Number(ticket["requester_id"]),
            ["assignee_id"] = Number(ticket["assignee_id"]),
            ["group_id"] = Number(ticket["group_id"]),
            ["organization_id"] = Number(ticket["organization_id"]),
            ["tags"] = Joined(ticket["tags"]),
            ["external_id"] = Text(ticket["external_id"]),
            ["channel"] = Text(ticket["via"]?["channel"]),
            ["custom_fields"] = ticket["custom_fields"]?.ToJsonString() ?? "[]",
            ["created_at"] = Text(ticket["created_at"]),
            ["updated_at"] = Text(ticket["updated_at"]),
        });

        private static ConnectorRecord User(JsonObject user) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(user["id"]),
            ["name"] = Text(user["name"]),
            ["email"] = Text(user["email"]),
            ["role"] = Text(user["role"]),
            ["phone"] = Text(user["phone"]),
            ["organization_id"] = Number(user["organization_id"]),
            ["external_id"] = Text(user["external_id"]),
            ["tags"] = Joined(user["tags"]),
            ["active"] = user["active"] is JsonValue active && active.TryGetValue<bool>(out var isActive) && isActive,
            ["created_at"] = Text(user["created_at"]),
            ["updated_at"] = Text(user["updated_at"]),
        });

        private static ConnectorRecord Organization(JsonObject organization) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Number(organization["id"]),
            ["name"] = Text(organization["name"]),
            ["domain_names"] = Joined(organization["domain_names"]),
            ["tags"] = Joined(organization["tags"]),
            ["external_id"] = Text(organization["external_id"]),
            ["created_at"] = Text(organization["created_at"]),
            ["updated_at"] = Text(organization["updated_at"]),
        });

        // The account's Support API: its own address, /api/v2.
        private static string Api(string endpoint)
        {
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var account) || (account.Scheme != Uri.UriSchemeHttps && account.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not a Zendesk account's address - https://your-subdomain.zendesk.com.");
            }

            return account.GetLeftPart(UriPartial.Authority) + "/api/v2";
        }

        // Zendesk's API-token pair: the agent's address with /token, and the token.
        private static AuthenticationHeaderValue Authorization(IReadOnlyDictionary<string, string> credentials)
        {
            var email = Values.Get(credentials, "username")?.Trim() ?? throw new InvalidOperationException("The Zendesk connection has no agent's address.");
            var token = credentials.TryGetValue("password", out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : throw new InvalidOperationException("The Zendesk connection has no API token.");
            var user = email.EndsWith("/token", StringComparison.OrdinalIgnoreCase) ? email : email + "/token";
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + token)));
        }

        private static string Id(string? given, string what) =>
            given is not null && Digits.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a Zendesk {what}'s id (digits).");

        private static string Word(string given, HashSet<string> words, string what) =>
            words.Contains(given.Trim().ToLowerInvariant()) ? given.Trim().ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not a {what} - {string.Join(", ", words)}.");

        private static string Email(string given) =>
            given.Contains('@', StringComparison.Ordinal) && !given.Trim().Any(c => char.IsWhiteSpace(c) || c == '"') ? given.Trim() : throw new InvalidOperationException($"'{given}' is not an address.");

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
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value;

        private static string? Joined(JsonNode? list) => list is JsonArray items ? string.Join(", ", items.Select(Text).Where(item => item is not null)) : null;

        private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Zendesk's answer as JSON; a throttled call waits as Zendesk says; a failure thrown with its status, Zendesk's error and the
        // details beside it.
        private static async Task<JsonNode?> SendAsync(HttpClient client, AuthenticationHeaderValue authorization, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
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
                throw new InvalidOperationException($"Zendesk answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = json?["error"];
                var said = error is JsonObject titled ? Text(titled["message"]) ?? Text(titled["title"]) : Text(error);
                var description = Text(json?["description"]);
                var details = (json?["details"] as JsonObject)?.Select(pair => (pair.Value as JsonArray)?.OfType<JsonObject>().Select(item => Text(item["description"])).FirstOrDefault(text => text is not null)).FirstOrDefault(text => text is not null);
                var words = string.Join(" - ", new[] { said, description, details }.Where(part => !string.IsNullOrEmpty(part)).Distinct());
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(words.Length == 0 ? "." : ": " + (words.Length > 300 ? words[..300] : words))}");
            }

            return json;
        }
    }
}
