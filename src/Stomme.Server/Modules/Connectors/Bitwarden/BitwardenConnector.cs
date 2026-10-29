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

namespace Stomme.Modules.Connectors.Bitwarden
{
    /// <summary>
    /// Bitwarden (n8n's Bitwarden node: an organization's collections, events, groups and members): Bitwarden's public
    /// API with an organization API key's client credentials, over the host's egress - Bitwarden's US or EU cloud, or a
    /// self-hosted server. A test names the organization's member count.
    /// </summary>
    /// <remarks>
    /// <c>collections</c>, <c>collection</c>, <c>update_collection</c> (its groups and external id) and <c>delete_collection</c>;
    /// <c>events</c> (between two moments, by a member or an item, page by page by Bitwarden's continuation token);
    /// <c>groups</c>, <c>group</c>, <c>create_group</c>, <c>update_group</c>, <c>delete_group</c>, <c>group_members</c> and
    /// <c>set_group_members</c>; <c>members</c>, <c>member</c>, <c>invite_member</c> (an e-mail and a role), <c>update_member</c>,
    /// <c>delete_member</c>, <c>member_groups</c> and <c>set_member_groups</c>. Bitwarden replaces a collection, group or member
    /// with what an update sends, so an update reads it first and keeps what is not given - n8n's dropped a group's
    /// collections when it named none. A collection's, group's or member's access is to collections or groups by their ids,
    /// read-only when it says so. A record is its fields as Bitwarden gives them, a list or an object as its JSON. Every id,
    /// e-mail, role, moment and flag held to its shape before anything is sent. A throttled call waits as Bitwarden says; a
    /// failure is thrown with its status and Bitwarden's message and errors.
    /// </remarks>
    public sealed class BitwardenConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string CollectionsOperation = "collections";

        public const string CollectionOperation = "collection";

        public const string UpdateCollectionOperation = "update_collection";

        public const string DeleteCollectionOperation = "delete_collection";

        public const string EventsOperation = "events";

        public const string GroupsOperation = "groups";

        public const string GroupOperation = "group";

        public const string CreateGroupOperation = "create_group";

        public const string UpdateGroupOperation = "update_group";

        public const string DeleteGroupOperation = "delete_group";

        public const string GroupMembersOperation = "group_members";

        public const string SetGroupMembersOperation = "set_group_members";

        public const string MembersOperation = "members";

        public const string MemberOperation = "member";

        public const string InviteMemberOperation = "invite_member";

        public const string UpdateMemberOperation = "update_member";

        public const string DeleteMemberOperation = "delete_member";

        public const string MemberGroupsOperation = "member_groups";

        public const string SetMemberGroupsOperation = "set_member_groups";

        public const int DefaultMax = 1000;

        private const string DefaultEndpoint = "https://api.bitwarden.com";

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Moment = new("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2}(\\.\\d{1,7})?)?(Z|[+\\-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);

        private static readonly Regex ExternalId = new("^[^\\x00-\\x1F]{1,300}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // A member's role by its name and Bitwarden's number.
        private static readonly (string Name, int Number)[] Roles = { ("owner", 0), ("admin", 1), ("user", 2), ("manager", 3), ("custom", 4) };

        private static readonly string[] Known =
        {
            CollectionsOperation, CollectionOperation, UpdateCollectionOperation, DeleteCollectionOperation, EventsOperation, GroupsOperation, GroupOperation,
            CreateGroupOperation, UpdateGroupOperation, DeleteGroupOperation, GroupMembersOperation, SetGroupMembersOperation, MembersOperation, MemberOperation,
            InviteMemberOperation, UpdateMemberOperation, DeleteMemberOperation, MemberGroupsOperation, SetMemberGroupsOperation,
        };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "bitwarden";

        public string DisplayName => "Bitwarden";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Client id (Admin Console > Settings > Organization info > API key - organization.…)",
            ["client-secret"] = "Client secret (the same page)",
            ["token-url"] = "Token endpoint (empty: the cloud's or your server's own)",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://bitwarden.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://bitwarden.com/help/public-api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField IdField = Field("id", "Id", "Its id.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField ExternalIdField = Field("external_id", "External id", "An id of your own.");

        private static readonly ConnectorConfigField CollectionsField = Field("collections", "Collections", "Comma-separated collection ids - these and no others; ':read-only' after an id for read-only.");

        private static readonly ConnectorConfigField AccessAllField = Field("access_all", "All collections", "true: every collection, now and later.", ConnectorConfigFieldType.Boolean);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(CollectionsOperation, "Get collections", "n8n's Bitwarden Collection Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(CollectionOperation, "Get a collection", "With its groups - n8n's Bitwarden Collection Get.") { Parameters = Schema(IdField) },
            new ConnectorOperation(UpdateCollectionOperation, "Update a collection", "Its groups or external id, the rest kept - n8n's Bitwarden Collection Update.")
            {
                Parameters = Schema(IdField, Field("groups", "Groups", "Comma-separated group ids - these and no others; ':read-only' after an id for read-only."), ExternalIdField),
            },
            new ConnectorOperation(DeleteCollectionOperation, "Delete a collection", "n8n's Bitwarden Collection Delete.") { Parameters = Schema(IdField) },
            new ConnectorOperation(EventsOperation, "Get events", "The organization's event log - n8n's Bitwarden Event Get Many.")
            {
                Parameters = Schema(Field("start", "From", "A moment with its offset - 2026-10-01T00:00:00Z."), Field("end", "To", "A moment with its offset."), Field("member", "By member", "The acting member's user id."), Field("item", "Of item", "A vault item's id."), MaxField),
            },
            new ConnectorOperation(GroupsOperation, "Get groups", "n8n's Bitwarden Group Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(GroupOperation, "Get a group", "With its collections - n8n's Bitwarden Group Get.") { Parameters = Schema(IdField) },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "n8n's Bitwarden Group Create.") { Parameters = Schema(Field("name", "Name", "The group's name.", required: true), AccessAllField, CollectionsField, ExternalIdField) },
            new ConnectorOperation(UpdateGroupOperation, "Update a group", "What is given, the rest kept - n8n's Bitwarden Group Update.") { Parameters = Schema(IdField, Field("name", "Name", "The group's name."), AccessAllField, CollectionsField, ExternalIdField) },
            new ConnectorOperation(DeleteGroupOperation, "Delete a group", "n8n's Bitwarden Group Delete.") { Parameters = Schema(IdField) },
            new ConnectorOperation(GroupMembersOperation, "Get a group's members", "Their ids - n8n's Bitwarden Group Get Members.") { Parameters = Schema(IdField) },
            new ConnectorOperation(SetGroupMembersOperation, "Set a group's members", "These and no others - n8n's Bitwarden Group Update Members.") { Parameters = Schema(IdField, Field("members", "Members", "Comma-separated member ids; empty: none.", required: true)) },
            new ConnectorOperation(MembersOperation, "Get members", "n8n's Bitwarden Member Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(MemberOperation, "Get a member", "With their collections - n8n's Bitwarden Member Get.") { Parameters = Schema(IdField) },
            new ConnectorOperation(InviteMemberOperation, "Invite a member", "Bitwarden e-mails them - n8n's Bitwarden Member Create.")
            {
                Parameters = Schema(Field("email", "E-mail", "Who is invited.", required: true), Field("role", "Role", "owner, admin, user (the default), manager or custom."), AccessAllField, CollectionsField, ExternalIdField),
            },
            new ConnectorOperation(UpdateMemberOperation, "Update a member", "What is given, the rest kept - n8n's Bitwarden Member Update.") { Parameters = Schema(IdField, Field("role", "Role", "owner, admin, user, manager or custom."), AccessAllField, CollectionsField, ExternalIdField) },
            new ConnectorOperation(DeleteMemberOperation, "Remove a member", "From the organization - n8n's Bitwarden Member Delete.") { Parameters = Schema(IdField) },
            new ConnectorOperation(MemberGroupsOperation, "Get a member's groups", "Their ids - n8n's Bitwarden Member Get Groups.") { Parameters = Schema(IdField) },
            new ConnectorOperation(SetMemberGroupsOperation, "Set a member's groups", "These and no others - n8n's Bitwarden Member Update Groups.") { Parameters = Schema(IdField, Field("groups", "Groups", "Comma-separated group ids; empty: none.", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = await Session.OfAsync(tokens, context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                using var client = session.Client;
                var members = (await session.SendAsync(HttpMethod.Get, "/members", null, cancellationToken))?["data"] as JsonArray;
                return ConnectionTestResult.Ok($"Bitwarden: the organization has {members?.Count ?? 0} member{(members?.Count == 1 ? "" : "s")}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? MembersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Bitwarden has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is sent - the token included.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string name = "id", string what = "its") => Values.Get(p, name) is { } given ? Guid.TryParseExact(given, "D", out var id) ? id.ToString("D") : throw new InvalidOperationException($"'{given}' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 ({name}).") : throw new InvalidOperationException($"Give {what} id ({name}).");
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            int? RoleOf() => Values.Get(p, "role") is { } given ? Roles.FirstOrDefault(role => string.Equals(role.Name, given, StringComparison.OrdinalIgnoreCase) || role.Number.ToString(CultureInfo.InvariantCulture) == given) is { Name: not null } found ? found.Number : throw new InvalidOperationException($"'{given}' is not owner, admin, user, manager or custom (role).") : null;
            string? ExternalOf() => Values.Get(p, "external_id") is { } given ? ExternalId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an external id - 300 characters at most, no control characters (external_id).") : null;
            string? MomentOf(string name) => Values.Get(p, name) is { } given ? Moment.IsMatch(given) && DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not a moment with its offset - 2026-10-01T00:00:00Z ({name}).") : null;

            // A list of ids, each read-only when ':read-only' follows it - an access list as Bitwarden takes one; empty: none.
            JsonArray? AccessOf(string name, bool required = false)
            {
                var given = p.TryGetValue(name, out var raw) ? raw?.Trim() : null;
                if (given is null || (given.Length == 0 && !required))
                {
                    return required ? throw new InvalidOperationException($"Give the {name} - comma-separated ids; empty for none ({name}).") : null;
                }

                var list = new JsonArray();
                foreach (var part in given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var readOnly = part.EndsWith(":read-only", StringComparison.OrdinalIgnoreCase);
                    var id = readOnly ? part[..^":read-only".Length].Trim() : part;
                    list.Add(new JsonObject { ["id"] = Guid.TryParseExact(id, "D", out var guid) ? guid.ToString("D") : throw new InvalidOperationException($"'{id}' is not an id - 3fa85f64-5717-4562-b3fc-2c963f66afa6 ({name})."), ["readOnly"] = readOnly });
                }

                return list;
            }

            JsonArray IdsOf(string name)
            {
                var list = new JsonArray();
                foreach (var entry in AccessOf(name, required: true)!.OfType<JsonObject>())
                {
                    list.Add((string?)entry["id"] is { } id && entry["readOnly"]?.GetValue<bool>() != true ? id : throw new InvalidOperationException($"A {name[..^1]} has no read-only - give its id alone ({name})."));
                }

                return list;
            }

            string path;
            var query = new List<string>();
            JsonObject? body = null;
            Func<JsonObject, JsonObject>? keep = null;
            switch (operation)
            {
                case CollectionsOperation:
                    path = "/collections";
                    break;
                case CollectionOperation or DeleteCollectionOperation:
                    path = "/collections/" + IdOf(what: "the collection's");
                    break;
                case UpdateCollectionOperation:
                    {
                        path = "/collections/" + IdOf(what: "the collection's");
                        var groups = AccessOf("groups");
                        var external = ExternalOf();
                        if (groups is null && external is null)
                        {
                            throw new InvalidOperationException("Give what to change - its groups or external id.");
                        }

                        keep = current => new JsonObject { ["externalId"] = external ?? current["externalId"]?.DeepClone(), ["groups"] = groups ?? Kept(current["groups"]) };
                        break;
                    }

                case EventsOperation:
                    path = "/events";
                    foreach (var (name, wire) in new[] { ("start", "start"), ("end", "end") })
                    {
                        if (MomentOf(name) is { } at)
                        {
                            query.Add(wire + "=" + Uri.EscapeDataString(at));
                        }
                    }

                    if (Values.Get(p, "member") is not null)
                    {
                        query.Add("actingUserId=" + IdOf("member", "the member's"));
                    }

                    if (Values.Get(p, "item") is not null)
                    {
                        query.Add("itemId=" + IdOf("item", "the item's"));
                    }

                    break;
                case GroupsOperation:
                    path = "/groups";
                    break;
                case GroupOperation or DeleteGroupOperation:
                    path = "/groups/" + IdOf(what: "the group's");
                    break;
                case CreateGroupOperation or UpdateGroupOperation:
                    {
                        var create = operation == CreateGroupOperation;
                        path = create ? "/groups" : "/groups/" + IdOf(what: "the group's");
                        var name = Values.Get(p, "name");
                        if (create && name is null)
                        {
                            throw new InvalidOperationException("Give the group's name (name).");
                        }

                        if (name is { Length: > 100 })
                        {
                            throw new InvalidOperationException($"'{name}' is not a group's name - 100 characters at most (name).");
                        }

                        var accessAll = FlagOf("access_all");
                        var collections = AccessOf("collections");
                        var external = ExternalOf();
                        if (!create && name is null && accessAll is null && collections is null && external is null)
                        {
                            throw new InvalidOperationException("Give what to change - its name, all collections, its collections or external id.");
                        }

                        JsonObject Group(JsonObject current) => new()
                        {
                            ["name"] = name ?? current["name"]?.DeepClone(),
                            ["accessAll"] = accessAll ?? current["accessAll"]?.GetValue<bool>() ?? false,
                            ["externalId"] = external ?? current["externalId"]?.DeepClone(),
                            ["collections"] = collections ?? Kept(current["collections"]),
                        };
                        if (create)
                        {
                            body = Group(new JsonObject());
                        }
                        else
                        {
                            keep = Group;
                        }

                        break;
                    }

                case GroupMembersOperation:
                    path = $"/groups/{IdOf(what: "the group's")}/member-ids";
                    break;
                case SetGroupMembersOperation:
                    path = $"/groups/{IdOf(what: "the group's")}/member-ids";
                    body = new JsonObject { ["memberIds"] = IdsOf("members") };
                    break;
                case MembersOperation:
                    path = "/members";
                    break;
                case MemberOperation or DeleteMemberOperation:
                    path = "/members/" + IdOf(what: "the member's");
                    break;
                case InviteMemberOperation or UpdateMemberOperation:
                    {
                        var invite = operation == InviteMemberOperation;
                        path = invite ? "/members" : "/members/" + IdOf(what: "the member's");
                        var email = invite ? Values.Get(p, "email") is { } given ? Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail (email).") : throw new InvalidOperationException("Give the member's e-mail (email).") : null;
                        var role = RoleOf();
                        var accessAll = FlagOf("access_all");
                        var collections = AccessOf("collections");
                        var external = ExternalOf();
                        if (!invite && role is null && accessAll is null && collections is null && external is null)
                        {
                            throw new InvalidOperationException("Give what to change - their role, all collections, their collections or external id.");
                        }

                        JsonObject Member(JsonObject current)
                        {
                            var member = new JsonObject
                            {
                                ["type"] = role ?? current["type"]?.GetValue<int>() ?? 2,
                                ["accessAll"] = accessAll ?? current["accessAll"]?.GetValue<bool>() ?? false,
                                ["externalId"] = external ?? current["externalId"]?.DeepClone(),
                                ["collections"] = collections ?? Kept(current["collections"]),
                            };
                            if (email is not null)
                            {
                                member["email"] = email;
                            }

                            // A custom role's permissions kept as they were.
                            if (current["permissions"] is JsonObject permissions)
                            {
                                member["permissions"] = permissions.DeepClone();
                            }

                            return member;
                        }

                        if (invite)
                        {
                            body = Member(new JsonObject());
                        }
                        else
                        {
                            keep = Member;
                        }

                        break;
                    }

                case MemberGroupsOperation:
                    path = $"/members/{IdOf(what: "the member's")}/group-ids";
                    break;
                default:
                    path = $"/members/{IdOf(what: "the member's")}/group-ids";
                    body = new JsonObject { ["groupIds"] = IdsOf("groups") };
                    break;
            }

            var session = await Session.OfAsync(tokens, context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            using var client = session.Client;
            switch (operation)
            {
                case CollectionsOperation or GroupsOperation or MembersOperation or EventsOperation:
                    {
                        // Bitwarden answers a list whole, the events page by page by its continuation token.
                        var read = 0;
                        string? next = null;
                        while (true)
                        {
                            var answer = await session.SendAsync(HttpMethod.Get, path + (query.Count == 0 && next is null ? "" : "?" + string.Join("&", next is null ? query : query.Append("continuationToken=" + Uri.EscapeDataString(next)))), null, cancellationToken);
                            var items = (answer?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            next = operation == EventsOperation && answer?["continuationToken"] is JsonValue token && token.TryGetValue<string>(out var t) && t.Length > 0 && t != next ? t : null;
                            if (items.Count == 0 || next is null)
                            {
                                yield break;
                            }
                        }
                    }

                case GroupMembersOperation or MemberGroupsOperation:
                    {
                        var name = operation == GroupMembersOperation ? "member" : "group";
                        foreach (var id in (await session.SendAsync(HttpMethod.Get, path, null, cancellationToken) as JsonArray ?? new JsonArray()).Select(node => Value(node)))
                        {
                            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = id });
                        }

                        yield break;
                    }

                case DeleteCollectionOperation or DeleteGroupOperation or DeleteMemberOperation:
                    await session.SendAsync(HttpMethod.Delete, path, null, cancellationToken);
                    yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = path[(path.LastIndexOf('/') + 1)..], ["deleted"] = true });
                    yield break;
                case SetGroupMembersOperation or SetMemberGroupsOperation:
                    {
                        await session.SendAsync(HttpMethod.Put, path, body, cancellationToken);
                        var ids = (JsonArray)body![operation == SetGroupMembersOperation ? "memberIds" : "groupIds"]!;
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = path.Split('/')[2], [operation == SetGroupMembersOperation ? "members" : "groups"] = ids.ToJsonString(Wire), ["set"] = true });
                        yield break;
                    }

                default:
                    {
                        if (keep is not null)
                        {
                            // Bitwarden replaces what an update sends: read it first, keep what is not given.
                            var current = await session.SendAsync(HttpMethod.Get, path, null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Bitwarden has none with that id.");
                            body = keep(current);
                        }

                        var method = keep is not null ? HttpMethod.Put : body is not null ? HttpMethod.Post : HttpMethod.Get;
                        yield return Record(await session.SendAsync(method, path, body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }
            }
        }

        // An access list as Bitwarden gave it, sent back as it was - its read-only, hidden passwords and management kept.
        private static JsonArray Kept(JsonNode? given) => given is JsonArray list ? (JsonArray)list.DeepClone() : new JsonArray();

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => pair.Key is not "object").ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        // One connection's calls: the organization's public API with a token from its client credentials - the US or EU cloud,
        // or a self-hosted server's /api and /identity; a throttled call waiting as Bitwarden says; a failure thrown with its
        // status and Bitwarden's message and errors.
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

            public static async Task<Session> OfAsync(OAuth2TokenProvider tokens, string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                var address = Values.BaseUrl(endpoint, DefaultEndpoint);
                var host = new Uri(address).Host;
                var (api, tokenUrl) = host.ToLowerInvariant() switch
                {
                    "api.bitwarden.com" => ("https://api.bitwarden.com", "https://identity.bitwarden.com/connect/token"),
                    "api.bitwarden.eu" => ("https://api.bitwarden.eu", "https://identity.bitwarden.eu/connect/token"),
                    _ => address.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? (address, address[..^4] + "/identity/connect/token") : (address + "/api", address + "/identity/connect/token"),
                };
                var clientId = Values.Get(credentials, "client-id") ?? throw new InvalidOperationException("The Bitwarden connection has no client id.");
                if (!clientId.StartsWith("organization.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The client id is not an organization's - organization.… from Admin Console > Settings > Organization info > API key.");
                }

                var issued = await tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(Values.Get(credentials, "token-url") ?? tokenUrl, clientId, Values.Get(credentials, "client-secret") ?? throw new InvalidOperationException("The Bitwarden connection has no client secret."), "api.organization") { Timeout = timeout }, cancellationToken);
                return new Session(api + "/public", issued, egress.CreateHttpClient(timeout));
            }

            public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                    // A page that is not Bitwarden's - a sign-in page, a proxy's - read as nothing would hide that it never was.
                    throw new InvalidOperationException($"Bitwarden answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var errors = (error?["errors"] as JsonObject)?.SelectMany(pair => (pair.Value as JsonArray ?? new JsonArray()).Select(item => Text(item))).OfType<string>() ?? Enumerable.Empty<string>();
                    var said = string.Join("; ", new[] { Text(error?["message"]) }.Concat(errors).Where(part => !string.IsNullOrEmpty(part)).Distinct());
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
