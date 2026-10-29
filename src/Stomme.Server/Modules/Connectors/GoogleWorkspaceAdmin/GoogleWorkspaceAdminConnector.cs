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

namespace Stomme.Modules.Connectors.GoogleWorkspaceAdmin
{
    /// <summary>
    /// Google Workspace Admin (n8n's G Suite Admin node: a Workspace's users, groups and ChromeOS devices): the Admin
    /// SDK's Directory API over the OAuth2 REFRESH-TOKEN scheme - a Google Cloud OAuth client's id and secret and an
    /// administrator's refresh token granted the directory scopes - exchanged at Google's token endpoint (the <c>token_url</c>
    /// setting) through the shared <see cref="OAuth2TokenProvider"/>. A test names the Workspace's domain.
    /// </summary>
    /// <remarks>
    /// <c>users</c> (the customer's or a domain's, by Google's query, the deleted ones when asked, sorted as asked), <c>user</c>
    /// (by e-mail or id), <c>update_user</c> (a name, a primary e-mail, an organizational unit, suspended or not, archived or
    /// not, a password change at next sign-in asked for - what is given, the rest kept: a PATCH, never Google's clearing PUT),
    /// <c>delete_user</c>, <c>add_to_group</c> (a member, a manager or an owner) and <c>remove_from_group</c>; <c>groups</c> (the
    /// customer's, a domain's or a user's), <c>group</c>, <c>create_group</c>, <c>update_group</c>, <c>delete_group</c> and
    /// <c>group_members</c>; <c>devices</c> (ChromeOS, by Google's query and an organizational unit), <c>device</c>,
    /// <c>update_device</c> (its user, location, asset id, notes and unit) and <c>device_action</c> (disable or re-enable). A
    /// user is never created here: Google takes one only with a password, and a password is never a step's parameter. A list is
    /// read page by page by Google's token, a page no bigger than what is left, up to <c>max</c> (1,000). A record is its fields
    /// as Google gives them, a list or an object as its JSON. Every e-mail, id, unit path and kind held to its shape before a
    /// token is asked for. A throttled call waits as Google says; a failure is thrown with its status, Google's status word and
    /// its message.
    /// </remarks>
    public sealed class GoogleWorkspaceAdminConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://admin.googleapis.com/admin/directory/v1";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string UpdateUserOperation = "update_user";

        public const string DeleteUserOperation = "delete_user";

        public const string AddToGroupOperation = "add_to_group";

        public const string RemoveFromGroupOperation = "remove_from_group";

        public const string GroupsOperation = "groups";

        public const string GroupOperation = "group";

        public const string CreateGroupOperation = "create_group";

        public const string UpdateGroupOperation = "update_group";

        public const string DeleteGroupOperation = "delete_group";

        public const string GroupMembersOperation = "group_members";

        public const string DevicesOperation = "devices";

        public const string DeviceOperation = "device";

        public const string UpdateDeviceOperation = "update_device";

        public const string DeviceActionOperation = "device_action";

        public const int DefaultMax = 1000;

        private static readonly Regex Email = new("^[^@\\s,/]+@[^@\\s,/]+\\.[^@\\s,/]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Key = new("^[A-Za-z0-9_.\\-]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex Domain = new("^[A-Za-z0-9][A-Za-z0-9.\\-]{0,252}\\.[A-Za-z]{2,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex UnitPath = new("^/[^\\x00-\\x1F]{0,500}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            UsersOperation, UserOperation, UpdateUserOperation, DeleteUserOperation, AddToGroupOperation, RemoveFromGroupOperation, GroupsOperation, GroupOperation,
            CreateGroupOperation, UpdateGroupOperation, DeleteGroupOperation, GroupMembersOperation, DevicesOperation, DeviceOperation, UpdateDeviceOperation, DeviceActionOperation,
        };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googleworkspaceadmin";

        public string DisplayName => "Google Workspace Admin";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability, ConnectorCategory.ItsmAssetManagement };

        public Uri WebsiteUrl { get; } = new("https://workspace.google.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/admin-sdk/directory/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "Their e-mail or id.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "Its e-mail or id.", required: true);

        private static readonly ConnectorConfigField DeviceField = Field("device", "Device", "The ChromeOS device's id.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField CustomerField = Field("customer", "Customer", "my_customer (the default) or the Workspace's customer id.");

        private static readonly ConnectorConfigField ProjectionField = Field("projection", "Fields", "basic (the default) or full.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(UsersOperation, "Get users", "n8n's G Suite Admin User Get Many.")
            {
                Parameters = Schema(CustomerField, Field("domain", "Domain", "One of the Workspace's domains, instead of the customer."), Field("query", "Search", "Google's user query - orgUnitPath=/Kafé isSuspended=false."),
                    Field("show_deleted", "Deleted", "true: the deleted users, the last 20 days'.", ConnectorConfigFieldType.Boolean), Field("order_by", "Sort by", "email, familyName or givenName."),
                    Field("sort_order", "Order", "ascending or descending."), ProjectionField, MaxField),
            },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's G Suite Admin User Get.") { Parameters = Schema(UserField, ProjectionField) },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "What is given, the rest kept - never a password - n8n's G Suite Admin User Update.")
            {
                Parameters = Schema(UserField, Field("first_name", "First name", "Their given name."), Field("last_name", "Last name", "Their family name."), Field("primary_email", "Primary e-mail", "Their new primary e-mail."),
                    Field("org_unit_path", "Organizational unit", "Its path - /Kafé/Kassa."), Field("suspended", "Suspended", "true or false.", ConnectorConfigFieldType.Boolean),
                    Field("archived", "Archived", "true or false.", ConnectorConfigFieldType.Boolean), Field("change_password_at_next_login", "New password at next sign-in", "true: they set one when they next sign in.", ConnectorConfigFieldType.Boolean)),
            },
            new ConnectorOperation(DeleteUserOperation, "Delete a user", "n8n's G Suite Admin User Delete.") { Parameters = Schema(UserField) },
            new ConnectorOperation(AddToGroupOperation, "Add to a group", "n8n's G Suite Admin User Add to Group.") { Parameters = Schema(UserField, GroupField, Field("role", "Role", "member (the default), manager or owner.")) },
            new ConnectorOperation(RemoveFromGroupOperation, "Remove from a group", "n8n's G Suite Admin User Remove from Group.") { Parameters = Schema(UserField, GroupField) },
            new ConnectorOperation(GroupsOperation, "Get groups", "n8n's G Suite Admin Group Get Many.")
            {
                Parameters = Schema(CustomerField, Field("domain", "Domain", "One of the Workspace's domains, instead of the customer."), Field("member", "Of a member", "A user's e-mail or id: the groups they are in."),
                    Field("query", "Search", "Google's group query - email:fika*."), Field("sort_order", "Order", "ascending or descending, by e-mail."), MaxField),
            },
            new ConnectorOperation(GroupOperation, "Get a group", "n8n's G Suite Admin Group Get.") { Parameters = Schema(GroupField) },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "n8n's G Suite Admin Group Create.")
            {
                Parameters = Schema(Field("email", "E-mail", "The group's e-mail.", required: true), Field("name", "Name", "Its name."), Field("description", "Description", "What it is for.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateGroupOperation, "Update a group", "What is given, the rest kept - n8n's G Suite Admin Group Update.")
            {
                Parameters = Schema(GroupField, Field("email", "E-mail", "Its new e-mail."), Field("name", "Name", "Its name."), Field("description", "Description", "What it is for.", ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(DeleteGroupOperation, "Delete a group", "n8n's G Suite Admin Group Delete.") { Parameters = Schema(GroupField) },
            new ConnectorOperation(GroupMembersOperation, "Get a group's members", "Its members, managers and owners.") { Parameters = Schema(GroupField, Field("roles", "Roles", "Comma-separated: member, manager, owner."), MaxField) },
            new ConnectorOperation(DevicesOperation, "Get ChromeOS devices", "n8n's G Suite Admin ChromeOS Device Get Many.")
            {
                Parameters = Schema(CustomerField, Field("query", "Search", "Google's device query - status:provisioned."), Field("org_unit_path", "Organizational unit", "Its path."),
                    Field("include_child_units", "Its units too", "true: the units under it too.", ConnectorConfigFieldType.Boolean),
                    Field("order_by", "Sort by", "annotatedLocation, annotatedUser, lastSync, notes, serialNumber or status."), Field("sort_order", "Order", "ascending or descending."), ProjectionField, MaxField),
            },
            new ConnectorOperation(DeviceOperation, "Get a ChromeOS device", "n8n's G Suite Admin ChromeOS Device Get.") { Parameters = Schema(DeviceField, ProjectionField) },
            new ConnectorOperation(UpdateDeviceOperation, "Update a ChromeOS device", "What is given, the rest kept - n8n's G Suite Admin ChromeOS Device Update.")
            {
                Parameters = Schema(DeviceField, Field("annotated_user", "User", "Who has it."), Field("annotated_location", "Location", "Where it is."), Field("annotated_asset_id", "Asset id", "Your own asset id."),
                    Field("notes", "Notes", "Anything else.", ConnectorConfigFieldType.Multiline), Field("org_unit_path", "Organizational unit", "Its path.")),
            },
            new ConnectorOperation(DeviceActionOperation, "Disable or re-enable a ChromeOS device", "n8n's G Suite Admin ChromeOS Device Change Status.") { Parameters = Schema(DeviceField, Field("action", "Action", "disable or reenable.", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var customer = await SendAsync(client, token, HttpMethod.Get, api + "/customers/my_customer", null, cancellationToken);
                return ConnectionTestResult.Ok($"Google Workspace: {Text(customer?["customerDomain"]) ?? "the administrator's Workspace"}.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? UsersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Google Workspace Admin has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before a token is asked for.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string KeyOf(string name, string what) => Values.Get(p, name) is { } given ? Email.IsMatch(given) || Key.IsMatch(given) ? Uri.EscapeDataString(given) : throw new InvalidOperationException($"'{given}' is not {what}'s e-mail or id ({name}).") : throw new InvalidOperationException($"Give {what}'s e-mail or id ({name}).");
            string DeviceOf() => Values.Get(p, "device") is { } given ? Key.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a device's id (device).") : throw new InvalidOperationException("Give the device's id (device).");
            string? EmailOf(string name) => Values.Get(p, name) is { } given ? Email.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an e-mail ({name}).") : null;
            string? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            string? KindOf(string name, params string[] kinds) => Values.Get(p, name) is { } given ? kinds.FirstOrDefault(kind => string.Equals(kind, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not {string.Join(", ", kinds[..^1])} or {kinds[^1]} ({name}).") : null;
            string? UnitOf(string name) => Values.Get(p, name) is { } given ? UnitPath.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an organizational unit's path - /Kafé/Kassa ({name}).") : null;
            var query = new List<string>();
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    query.Add(name + "=" + Uri.EscapeDataString(value));
                }
            }

            // The customer's, a domain's or - for groups - a member's: one of them; the customer's when none is named.
            void Scope(bool member)
            {
                var customer = Values.Get(p, "customer");
                var domain = Values.Get(p, "domain");
                var user = member ? Values.Get(p, "member") : null;
                if (new[] { customer, domain, user }.Count(given => given is not null) > 1)
                {
                    throw new InvalidOperationException($"Give a customer{(member ? ", a domain or a member" : " or a domain")} - one of them.");
                }

                if (domain is not null)
                {
                    Add("domain", Domain.IsMatch(domain) ? domain : throw new InvalidOperationException($"'{domain}' is not a domain - kafe.example.com (domain)."));
                }
                else if (user is not null)
                {
                    Add("userKey", Email.IsMatch(user) || Key.IsMatch(user) ? user : throw new InvalidOperationException($"'{user}' is not a user's e-mail or id (member)."));
                }
                else
                {
                    Add("customer", customer is null || customer == "my_customer" || Key.IsMatch(customer) ? customer ?? "my_customer" : throw new InvalidOperationException($"'{customer}' is not a customer's id - my_customer or C01abc23d (customer)."));
                }
            }

            string path;
            string list;
            var method = HttpMethod.Get;
            JsonObject? body = null;
            Func<JsonNode?, ConnectorRecord>? done = null;
            var paged = false;
            var pageCap = 500;
            switch (operation)
            {
                case UsersOperation:
                    Scope(member: false);
                    Add("query", Values.Get(p, "query"));
                    Add("showDeleted", FlagOf("show_deleted") is "true" ? "true" : null);
                    Add("orderBy", KindOf("order_by", "email", "familyName", "givenName"));
                    Add("sortOrder", KindOf("sort_order", "ascending", "descending")?.ToUpperInvariant());
                    Add("projection", KindOf("projection", "basic", "full"));
                    (path, list, paged) = ("/users", "users", true);
                    break;
                case UserOperation:
                    path = "/users/" + KeyOf("user", "the user");
                    Add("projection", KindOf("projection", "basic", "full"));
                    list = "";
                    break;
                case UpdateUserOperation:
                    {
                        path = "/users/" + KeyOf("user", "the user");
                        body = new JsonObject();
                        var name = new JsonObject();
                        if (Values.Get(p, "first_name") is { } first)
                        {
                            name["givenName"] = first;
                        }

                        if (Values.Get(p, "last_name") is { } last)
                        {
                            name["familyName"] = last;
                        }

                        if (name.Count > 0)
                        {
                            body["name"] = name;
                        }

                        bool? Bit(string name) => FlagOf(name) is { } flag ? flag == "true" : null;
                        foreach (var (field, value) in new (string Field, JsonNode? Value)[] { ("primaryEmail", EmailOf("primary_email")), ("orgUnitPath", UnitOf("org_unit_path")), ("suspended", Bit("suspended")), ("archived", Bit("archived")), ("changePasswordAtNextLogin", Bit("change_password_at_next_login")) })
                        {
                            if (value is not null)
                            {
                                body[field] = value;
                            }
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - a name, a primary e-mail, an organizational unit, suspended, archived or a new password at next sign-in.");
                        }

                        // A PATCH changes what is given and keeps the rest - Google's PUT would clear it.
                        method = HttpMethod.Patch;
                        list = "";
                        break;
                    }

                case DeleteUserOperation or DeleteGroupOperation:
                    {
                        var user = operation == DeleteUserOperation;
                        var key = user ? KeyOf("user", "the user") : KeyOf("group", "the group");
                        path = (user ? "/users/" : "/groups/") + key;
                        method = HttpMethod.Delete;
                        done = _ => Done((user ? "user" : "group", Uri.UnescapeDataString(key)), ("deleted", true));
                        list = "";
                        break;
                    }

                case AddToGroupOperation:
                    {
                        var user = Values.Get(p, "user") is { } given ? given : throw new InvalidOperationException("Give the user's e-mail or id (user).");
                        var group = KeyOf("group", "the group");
                        path = $"/groups/{group}/members";
                        method = HttpMethod.Post;
                        body = new JsonObject
                        {
                            [Email.IsMatch(user) ? "email" : Key.IsMatch(user) ? "id" : throw new InvalidOperationException($"'{user}' is not the user's e-mail or id (user).")] = user,
                            ["role"] = (KindOf("role", "member", "manager", "owner") ?? "member").ToUpperInvariant(),
                        };
                        list = "";
                        break;
                    }

                case RemoveFromGroupOperation:
                    {
                        var user = KeyOf("user", "the user");
                        var group = KeyOf("group", "the group");
                        path = $"/groups/{group}/members/{user}";
                        method = HttpMethod.Delete;
                        done = _ => Done(("group", Uri.UnescapeDataString(group)), ("user", Uri.UnescapeDataString(user)), ("removed", true));
                        list = "";
                        break;
                    }

                case GroupsOperation:
                    Scope(member: true);
                    Add("query", Values.Get(p, "query"));
                    if (KindOf("sort_order", "ascending", "descending") is { } order)
                    {
                        Add("orderBy", "email");
                        Add("sortOrder", order.ToUpperInvariant());
                    }

                    (path, list, paged, pageCap) = ("/groups", "groups", true, 200);
                    break;
                case GroupOperation:
                    path = "/groups/" + KeyOf("group", "the group");
                    list = "";
                    break;
                case CreateGroupOperation or UpdateGroupOperation:
                    {
                        var create = operation == CreateGroupOperation;
                        path = create ? "/groups" : "/groups/" + KeyOf("group", "the group");
                        body = new JsonObject();
                        if (EmailOf("email") is { } email)
                        {
                            body["email"] = email;
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the group's e-mail (email).");
                        }

                        foreach (var name in new[] { "name", "description" })
                        {
                            if (Values.Get(p, name) is { } value)
                            {
                                body[name] = value;
                            }
                        }

                        if (!create && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - its e-mail, name or description.");
                        }

                        method = create ? HttpMethod.Post : HttpMethod.Patch;
                        list = "";
                        break;
                    }

                case GroupMembersOperation:
                    {
                        path = $"/groups/{KeyOf("group", "the group")}/members";
                        if (Values.Get(p, "roles") is { } roles)
                        {
                            Add("roles", string.Join(",", roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(role => role.ToLowerInvariant() is "member" or "manager" or "owner" ? role.ToUpperInvariant() : throw new InvalidOperationException($"'{role}' is not member, manager or owner (roles)."))));
                        }

                        (list, paged, pageCap) = ("members", true, 200);
                        break;
                    }

                case DevicesOperation:
                    {
                        var customer = Values.Get(p, "customer") ?? "my_customer";
                        path = $"/customer/{(customer == "my_customer" || Key.IsMatch(customer) ? customer : throw new InvalidOperationException($"'{customer}' is not a customer's id - my_customer or C01abc23d (customer)."))}/devices/chromeos";
                        Add("query", Values.Get(p, "query"));
                        Add("orgUnitPath", UnitOf("org_unit_path"));
                        Add("includeChildOrgunits", FlagOf("include_child_units"));
                        Add("orderBy", KindOf("order_by", "annotatedLocation", "annotatedUser", "lastSync", "notes", "serialNumber", "status"));
                        Add("sortOrder", KindOf("sort_order", "ascending", "descending")?.ToUpperInvariant());
                        Add("projection", KindOf("projection", "basic", "full")?.ToUpperInvariant());
                        (list, paged, pageCap) = ("chromeosdevices", true, 200);
                        break;
                    }

                case DeviceOperation:
                    path = "/customer/my_customer/devices/chromeos/" + DeviceOf();
                    Add("projection", KindOf("projection", "basic", "full")?.ToUpperInvariant());
                    list = "";
                    break;
                case UpdateDeviceOperation:
                    {
                        path = "/customer/my_customer/devices/chromeos/" + DeviceOf();
                        body = new JsonObject();
                        foreach (var (name, field) in new[] { ("annotated_user", "annotatedUser"), ("annotated_location", "annotatedLocation"), ("annotated_asset_id", "annotatedAssetId"), ("notes", "notes") })
                        {
                            if (Values.Get(p, name) is { } value)
                            {
                                body[field] = value;
                            }
                        }

                        if (UnitOf("org_unit_path") is { } unit)
                        {
                            body["orgUnitPath"] = unit;
                        }

                        if (body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - its user, location, asset id, notes or organizational unit.");
                        }

                        method = HttpMethod.Patch;
                        list = "";
                        break;
                    }

                default:
                    {
                        var device = DeviceOf();
                        var action = KindOf("action", "disable", "reenable") ?? throw new InvalidOperationException("Give the action - disable or reenable (action).");
                        path = $"/customer/my_customer/devices/chromeos/{device}/action";
                        method = HttpMethod.Post;
                        body = new JsonObject { ["action"] = action };
                        done = _ => Done(("device", Uri.UnescapeDataString(device)), (action == "disable" ? "disabled" : "reenabled", true));
                        list = "";
                        break;
                    }
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (paged)
            {
                // Google pages by its token: a page no bigger than what is left, no token the end.
                var read = 0;
                string? next = null;
                while (true)
                {
                    var page = query.Append("maxResults=" + Math.Min(pageCap, max - read).ToString(CultureInfo.InvariantCulture)).Concat(next is null ? Array.Empty<string>() : new[] { "pageToken=" + Uri.EscapeDataString(next) });
                    var answer = await SendAsync(client, token, HttpMethod.Get, api + path + "?" + string.Join("&", page), null, cancellationToken);
                    var items = (answer?[list] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = Text(answer?["nextPageToken"]) is { Length: > 0 } t && t != next ? t : null;
                    if (items.Count == 0 || next is null)
                    {
                        yield break;
                    }
                }
            }

            var result = await SendAsync(client, token, method, api + path + (query.Count == 0 ? "" : "?" + string.Join("&", query)), body, cancellationToken);
            yield return done?.Invoke(result) ?? Record(result as JsonObject ?? new JsonObject());
        }

        private static ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => pair.Key is not "kind" and not "etag").ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer null), a throttled call waiting as Google says; a failure thrown with its status,
        // Google's status word and its message; a success that is not JSON a failure that says so.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
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
                throw new InvalidOperationException($"Google answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
