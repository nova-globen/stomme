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

namespace Stomme.Modules.Connectors.Zammad
{
    /// <summary>
    /// Zammad (BL-229, n8n's Zammad node: its tickets, users, organizations and groups): a help desk's REST API v1 with an
    /// access token in Zammad's own <c>Authorization: Token token=</c> header, over the host's egress. A test names whose the
    /// token is.
    /// </summary>
    /// <remarks>
    /// <c>tickets</c>, <c>ticket</c> (its articles too), <c>create_ticket</c> (its title, group, customer, state, priority and
    /// first article - a note, an e-mail, a phone call... internal or not), <c>update_ticket</c> (its title, group, state,
    /// priority, owner, customer, pending time and an internal note) and <c>delete_ticket</c>; <c>users</c> (by Zammad's search
    /// and a sort), <c>user</c>, <c>me</c>, <c>create_user</c>, <c>update_user</c> and <c>delete_user</c>;
    /// <c>organizations</c>, <c>organization</c>, <c>create_organization</c>, <c>update_organization</c> and
    /// <c>delete_organization</c>; <c>groups</c>, <c>group</c>, <c>create_group</c>, <c>update_group</c> and
    /// <c>delete_group</c>. Custom fields as a JSON object of names and values. A list is read page by page up to <c>max</c>, a
    /// short or empty page the end. A record is its fields as Zammad gives them, a list or an object as its JSON. Every id,
    /// e-mail, flag, time, kind and field held to its shape before anything is sent. A throttled call waits as Zammad says; a
    /// failure is thrown with its status and Zammad's words.
    /// </remarks>
    public sealed class ZammadConnector : IConnector, IConnectorExtractor
    {
        public const string TicketsOperation = "tickets";

        public const string TicketOperation = "ticket";

        public const string CreateTicketOperation = "create_ticket";

        public const string UpdateTicketOperation = "update_ticket";

        public const string DeleteTicketOperation = "delete_ticket";

        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string MeOperation = "me";

        public const string CreateUserOperation = "create_user";

        public const string UpdateUserOperation = "update_user";

        public const string DeleteUserOperation = "delete_user";

        public const string OrganizationsOperation = "organizations";

        public const string OrganizationOperation = "organization";

        public const string CreateOrganizationOperation = "create_organization";

        public const string UpdateOrganizationOperation = "update_organization";

        public const string DeleteOrganizationOperation = "delete_organization";

        public const string GroupsOperation = "groups";

        public const string GroupOperation = "group";

        public const string CreateGroupOperation = "create_group";

        public const string UpdateGroupOperation = "update_group";

        public const string DeleteGroupOperation = "delete_group";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[a-z][a-z0-9_]{0,59}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            TicketsOperation, TicketOperation, CreateTicketOperation, UpdateTicketOperation, DeleteTicketOperation, UsersOperation, UserOperation, MeOperation, CreateUserOperation,
            UpdateUserOperation, DeleteUserOperation, OrganizationsOperation, OrganizationOperation, CreateOrganizationOperation, UpdateOrganizationOperation,
            DeleteOrganizationOperation, GroupsOperation, GroupOperation, CreateGroupOperation, UpdateGroupOperation, DeleteGroupOperation,
        };

        private static readonly string[] ArticleTypes = { "note", "email", "phone", "chat", "fax", "sms" };

        private static readonly string[] Senders = { "Agent", "Customer", "System" };

        // Each kind's fields as Zammad names them, with whether each is a flag; the custom ones come as a JSON object.
        private static readonly (string Name, bool Flag)[] UserFields =
        {
            ("firstname", false), ("lastname", false), ("email", false), ("organization", false), ("phone", false), ("mobile", false), ("fax", false), ("department", false),
            ("web", false), ("note", false), ("street", false), ("zip", false), ("city", false), ("country", false), ("active", true), ("verified", true), ("vip", true),
        };

        private static readonly (string Name, bool Flag)[] OrganizationFields = { ("name", false), ("domain", false), ("note", false), ("shared", true), ("domain_assignment", true), ("active", true), ("vip", true) };

        private static readonly (string Name, bool Flag)[] GroupFields = { ("name", false), ("note", false), ("active", true) };

        public string ConnectorType => "zammad";

        public string DisplayName => "Zammad";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://helpdesk.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Access token (Profile > Token Access)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Crm };

        public Uri WebsiteUrl { get; } = new("https://zammad.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.zammad.org/en/latest/api/intro.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField CustomField = Field("fields", "Custom fields", "A JSON object of custom fields' names and values - {\"order_no\": \"A-17\"}.", ConnectorConfigFieldType.Multiline);

        private static ConnectorConfigField IdField(string what) => Field("id", "Id", $"The {what}'s id.", required: true);

        private static ConnectorConfigField[] Fields((string Name, bool Flag)[] fields, bool create, string first) =>
            fields.Select(field => Field(field.Name, char.ToUpperInvariant(field.Name[0]) + field.Name[1..].Replace('_', ' '), field.Flag ? "true or false." : $"Its {field.Name.Replace('_', ' ')}.", field.Flag ? ConnectorConfigFieldType.Boolean : ConnectorConfigFieldType.Text, required: create && field.Name == first)).Append(CustomField).ToArray();

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TicketsOperation, "Get tickets", "n8n's Zammad Ticket Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(TicketOperation, "Get a ticket", "With its articles - n8n's Zammad Ticket Get.") { Parameters = Schema(IdField("ticket")) },
            new ConnectorOperation(CreateTicketOperation, "Create a ticket", "With its first article - n8n's Zammad Ticket Create.")
            {
                Parameters = Schema(
                    Field("title", "Title", "Its title.", required: true),
                    Field("group", "Group", "Its group's name - Users.", required: true),
                    Field("customer", "Customer", "The customer's e-mail or id.", required: true),
                    Field("state", "State", "Its state's name or id - new, open."),
                    Field("priority", "Priority", "Its priority's name or id - 2 normal."),
                    Field("subject", "Subject", "The article's subject."),
                    Field("body", "Body", "The article's text.", ConnectorConfigFieldType.Multiline, required: true),
                    Field("type", "Type", "note (the default), email, phone, chat, fax or sms."),
                    Field("internal", "Internal", "true (the default) or false.", ConnectorConfigFieldType.Boolean),
                    Field("sender", "Sender", "Agent (the default), Customer or System."),
                    Field("to", "To", "An e-mail article's address."),
                    Field("cc", "Cc", "An e-mail article's copy."),
                    Field("reply_to", "Reply to", "An e-mail article's reply address."),
                    CustomField),
            },
            new ConnectorOperation(UpdateTicketOperation, "Update a ticket", "n8n's Zammad Ticket Update.")
            {
                Parameters = Schema(
                    IdField("ticket"),
                    Field("title", "Title", "Its title."),
                    Field("group", "Group", "Its group's name."),
                    Field("state", "State", "Its state's name or id - closed."),
                    Field("priority", "Priority", "Its priority's name or id - 3 high."),
                    Field("owner", "Owner", "Its owner's id."),
                    Field("customer", "Customer", "The customer's e-mail or id."),
                    Field("pending_time", "Pending until", "When - 2026-10-20T08:00:00Z."),
                    Field("note", "Note", "An internal note added to it.", ConnectorConfigFieldType.Multiline),
                    CustomField),
            },
            new ConnectorOperation(DeleteTicketOperation, "Delete a ticket", "n8n's Zammad Ticket Delete.") { Parameters = Schema(IdField("ticket")) },
            new ConnectorOperation(UsersOperation, "Get users", "By Zammad's search - n8n's Zammad User Get Many.")
            {
                Parameters = Schema(Field("query", "Query", "Zammad's search - email:ada@example.com."), Field("sort_by", "Sort by", "A field - created_at."), Field("order_by", "Order", "asc or desc."), MaxField),
            },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's Zammad User Get.") { Parameters = Schema(IdField("user")) },
            new ConnectorOperation(MeOperation, "Get me", "The token's user - n8n's Zammad User Get Self.") { Parameters = Schema() },
            new ConnectorOperation(CreateUserOperation, "Create a user", "n8n's Zammad User Create.") { Parameters = Schema(Fields(UserFields, true, "firstname")) },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "n8n's Zammad User Update.") { Parameters = Schema(Fields(UserFields, false, "").Prepend(IdField("user")).ToArray()) },
            new ConnectorOperation(DeleteUserOperation, "Delete a user", "n8n's Zammad User Delete.") { Parameters = Schema(IdField("user")) },
            new ConnectorOperation(OrganizationsOperation, "Get organizations", "n8n's Zammad Organization Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(OrganizationOperation, "Get an organization", "n8n's Zammad Organization Get.") { Parameters = Schema(IdField("organization")) },
            new ConnectorOperation(CreateOrganizationOperation, "Create an organization", "n8n's Zammad Organization Create.") { Parameters = Schema(Fields(OrganizationFields, true, "name")) },
            new ConnectorOperation(UpdateOrganizationOperation, "Update an organization", "n8n's Zammad Organization Update.") { Parameters = Schema(Fields(OrganizationFields, false, "").Prepend(IdField("organization")).ToArray()) },
            new ConnectorOperation(DeleteOrganizationOperation, "Delete an organization", "n8n's Zammad Organization Delete.") { Parameters = Schema(IdField("organization")) },
            new ConnectorOperation(GroupsOperation, "Get groups", "n8n's Zammad Group Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(GroupOperation, "Get a group", "n8n's Zammad Group Get.") { Parameters = Schema(IdField("group")) },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "n8n's Zammad Group Create.") { Parameters = Schema(Fields(GroupFields, true, "name")) },
            new ConnectorOperation(UpdateGroupOperation, "Update a group", "n8n's Zammad Group Update.") { Parameters = Schema(Fields(GroupFields, false, "").Prepend(IdField("group")).ToArray()) },
            new ConnectorOperation(DeleteGroupOperation, "Delete a group", "n8n's Zammad Group Delete.") { Parameters = Schema(IdField("group")) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var me = await session.SendAsync(HttpMethod.Get, "/users/me", null, cancellationToken) as JsonObject;
                var name = string.Join(" ", new[] { Text(me?["firstname"]), Text(me?["lastname"]) }.Where(part => !string.IsNullOrWhiteSpace(part)));
                var login = Text(me?["email"]) is { Length: > 0 } email ? email : Text(me?["login"]);
                return ConnectionTestResult.Ok($"Zammad: the token is {(name.Length > 0 ? name : "a user")}'s{(login is null ? "" : $" ({login})")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? TicketsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Zammad has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var kind = operation.Split('_')[^1] switch { "tickets" or "ticket" => "ticket", "users" or "user" or "me" => "user", "organizations" or "organization" => "organization", _ => "group" };
            var id = operation is TicketOperation or UpdateTicketOperation or DeleteTicketOperation or UserOperation or UpdateUserOperation or DeleteUserOperation or OrganizationOperation
                or UpdateOrganizationOperation or DeleteOrganizationOperation or GroupOperation or UpdateGroupOperation or DeleteGroupOperation
                ? Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id (id).") : throw new InvalidOperationException($"Give the {kind}'s id.")
                : null;
            bool FlagOf(string name, string given) => given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).");

            // A kind's fields as Zammad names them, the custom ones from a JSON object - a value a text, a number, true or false.
            JsonObject Body((string Name, bool Flag)[] fields)
            {
                var body = new JsonObject();
                foreach (var (name, flag) in fields)
                {
                    if (Values.Get(p, name) is { } given)
                    {
                        body[name] = flag ? FlagOf(name, given)
                            : name == "email" ? Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail (email).")
                            : given;
                    }
                }

                Custom(body);
                return body;
            }

            void Custom(JsonObject body)
            {
                if (Values.Get(p, "fields") is { } custom)
                {
                    JsonNode? parsed;
                    try
                    {
                        parsed = JsonNode.Parse(custom);
                    }
                    catch (JsonException)
                    {
                        parsed = null;
                    }

                    foreach (var (name, value) in parsed as JsonObject ?? throw new InvalidOperationException("The custom fields are not a JSON object - {\"order_no\": \"A-17\"}."))
                    {
                        body[FieldName.IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' is not a field's name - lower-case letters, digits and '_' (fields).")] =
                            value is null or JsonValue ? value?.DeepClone() : throw new InvalidOperationException($"'{name}' is not a text, a number, true or false (fields).");
                    }
                }
            }

            // A customer by e-mail or by id.
            void Customer(JsonObject body)
            {
                if (Values.Get(p, "customer") is { } customer)
                {
                    if (Id.IsMatch(customer))
                    {
                        body["customer_id"] = long.Parse(customer, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        body["customer"] = Email.IsMatch(customer) ? customer : throw new InvalidOperationException($"'{customer}' is not an e-mail or an id (customer).");
                    }
                }
            }

            // A state or a priority by its name - open, 2 normal - or by its id.
            void Named(JsonObject body, params string[] names)
            {
                foreach (var name in names)
                {
                    if (Values.Get(p, name) is { } value)
                    {
                        if (Id.IsMatch(value))
                        {
                            body[name + "_id"] = long.Parse(value, CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            body[name] = value;
                        }
                    }
                }
            }

            JsonObject? payload = null;
            switch (operation)
            {
                case CreateTicketOperation:
                    {
                        payload = new JsonObject
                        {
                            ["title"] = Values.Get(p, "title") ?? throw new InvalidOperationException("Give the ticket's title."),
                            ["group"] = Values.Get(p, "group") ?? throw new InvalidOperationException("Give the ticket's group."),
                        };
                        if (Values.Get(p, "customer") is null)
                        {
                            throw new InvalidOperationException("Give the ticket's customer - an e-mail or an id.");
                        }

                        Customer(payload);
                        Named(payload, "state", "priority");

                        var type = Values.Get(p, "type") is { } kindGiven ? ArticleTypes.Contains(kindGiven.ToLowerInvariant()) ? kindGiven.ToLowerInvariant() : throw new InvalidOperationException($"'{kindGiven}' is not note, email, phone, chat, fax or sms (type).") : "note";
                        var article = new JsonObject
                        {
                            ["body"] = Values.Get(p, "body") ?? throw new InvalidOperationException("Give the article's body."),
                            ["type"] = type,
                            ["internal"] = Values.Get(p, "internal") is { } internalGiven ? FlagOf("internal", internalGiven) : true,
                            ["sender"] = Values.Get(p, "sender") is { } sender ? Senders.FirstOrDefault(known => string.Equals(known, sender, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{sender}' is not Agent, Customer or System (sender).") : "Agent",
                        };
                        if (Values.Get(p, "subject") is { } subject)
                        {
                            article["subject"] = subject;
                        }

                        foreach (var name in new[] { "to", "cc", "reply_to" })
                        {
                            if (Values.Get(p, name) is { } address)
                            {
                                article[name] = type == "email" ? address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(Email.IsMatch) ? address : throw new InvalidOperationException($"'{address}' is not an e-mail or a comma-separated list of them ({name}).")
                                    : throw new InvalidOperationException($"Only an e-mail article has a {name.Replace('_', ' ')}.");
                            }
                        }

                        if (type == "email" && !article.ContainsKey("to"))
                        {
                            throw new InvalidOperationException("Give an e-mail article's address (to).");
                        }

                        Custom(payload);
                        payload["article"] = article;
                        break;
                    }

                case UpdateTicketOperation:
                    {
                        payload = new JsonObject();
                        foreach (var name in new[] { "title", "group" })
                        {
                            if (Values.Get(p, name) is { } value)
                            {
                                payload[name] = value;
                            }
                        }

                        Named(payload, "state", "priority");

                        if (Values.Get(p, "owner") is { } owner)
                        {
                            payload["owner_id"] = Id.IsMatch(owner) ? long.Parse(owner, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{owner}' is not an id (owner).");
                        }

                        Customer(payload);
                        if (Values.Get(p, "pending_time") is { } pending)
                        {
                            payload["pending_time"] = DateTimeOffset.TryParse(pending, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && pending.Contains('T')
                                ? when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                                : throw new InvalidOperationException($"'{pending}' is not a time - 2026-10-20T08:00:00Z (pending_time).");
                        }

                        Custom(payload);
                        if (Values.Get(p, "note") is { } note)
                        {
                            payload["article"] = new JsonObject { ["body"] = note, ["type"] = "note", ["internal"] = true, ["content_type"] = "text/html" };
                        }

                        if (payload.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - its title, group, state, priority, owner, customer, pending time, a note or fields.");
                        }

                        break;
                    }

                case CreateUserOperation or UpdateUserOperation:
                    payload = Body(UserFields);
                    if (operation == CreateUserOperation && !payload.ContainsKey("firstname"))
                    {
                        throw new InvalidOperationException("Give the user's first name.");
                    }

                    break;
                case CreateOrganizationOperation or UpdateOrganizationOperation:
                    payload = Body(OrganizationFields);
                    if (operation == CreateOrganizationOperation && !payload.ContainsKey("name"))
                    {
                        throw new InvalidOperationException("Give the organization's name.");
                    }

                    break;
                case CreateGroupOperation or UpdateGroupOperation:
                    payload = Body(GroupFields);
                    if (operation == CreateGroupOperation && !payload.ContainsKey("name"))
                    {
                        throw new InvalidOperationException("Give the group's name.");
                    }

                    break;
            }

            if (operation.StartsWith("update_", StringComparison.Ordinal) && payload is { Count: 0 })
            {
                throw new InvalidOperationException($"Give what to change - a field of the {kind}'s.");
            }

            var collection = "/" + kind + "s";
            var query = new List<string>();
            if (operation == UsersOperation)
            {
                if (Values.Get(p, "query") is { } search)
                {
                    collection = "/users/search";
                    query.Add("query=" + Uri.EscapeDataString(search));
                }

                if (Values.Get(p, "sort_by") is { } sortBy)
                {
                    query.Add("sort_by=" + (FieldName.IsMatch(sortBy) ? sortBy : throw new InvalidOperationException($"'{sortBy}' is not a field's name - created_at (sort_by).")));
                }

                if (Values.Get(p, "order_by") is { } orderBy)
                {
                    query.Add("order_by=" + (orderBy.ToLowerInvariant() is "asc" or "desc" ? orderBy.ToLowerInvariant() : throw new InvalidOperationException($"'{orderBy}' is not asc or desc (order_by).")));
                }
            }

            using var client = session.Client;
            switch (operation)
            {
                case TicketsOperation or UsersOperation or OrganizationsOperation or GroupsOperation:
                    {
                        // Page by page from the first, the size fixed at the start: a short or empty page the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, collection + "?" + string.Join("&", query.Append($"page={page}").Append($"per_page={size}")), null, cancellationToken);
                            var items = (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
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

                case DeleteTicketOperation or DeleteUserOperation or DeleteOrganizationOperation or DeleteGroupOperation:
                    await session.SendAsync(HttpMethod.Delete, $"{collection}/{id}", null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = long.Parse(id!, CultureInfo.InvariantCulture), ["deleted"] = true });
                    yield break;
                case TicketOperation:
                    {
                        var ticket = await session.SendAsync(HttpMethod.Get, $"/tickets/{id}", null, cancellationToken) as JsonObject ?? new JsonObject();
                        ticket["articles"] = await session.SendAsync(HttpMethod.Get, $"/ticket_articles/by_ticket/{id}", null, cancellationToken) as JsonArray ?? new JsonArray();
                        yield return Record(ticket);
                        yield break;
                    }

                default:
                    {
                        var (method, path) = operation switch
                        {
                            MeOperation => (HttpMethod.Get, "/users/me"),
                            UserOperation or OrganizationOperation or GroupOperation => (HttpMethod.Get, $"{collection}/{id}"),
                            CreateTicketOperation or CreateUserOperation or CreateOrganizationOperation or CreateGroupOperation => (HttpMethod.Post, collection),
                            _ => (HttpMethod.Put, $"{collection}/{id}"),
                        };
                        yield return Record(await session.SendAsync(method, path, payload, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }
            }
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: the help desk's API v1 with the token in Zammad's Token header; a throttled call waiting as
        // Zammad says; a failure thrown with its status and Zammad's words.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string token;

            private Session(string baseUrl, string token, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.token = token;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give the help desk's address - https://helpdesk.example.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^7] : baseUrl;
                return new Session(baseUrl + "/api/v1", Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Zammad connection has no access token."), egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Token", "token=" + token);
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
                    // A page that is not the help desk's - a sign-in page, a proxy's - read as no tickets would hide that it never was.
                    throw new InvalidOperationException($"Zammad answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var said = Text(error?["error_human"]) ?? Text(error?["error"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
