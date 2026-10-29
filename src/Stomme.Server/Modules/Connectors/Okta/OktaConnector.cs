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

namespace Stomme.Modules.Connectors.Okta
{
    /// <summary>
    /// Okta (n8n's Okta node: its users, and their groups): an Okta org's management API with an API token in Okta's own
    /// <c>Authorization: SSWS</c> header, over the host's egress. A test names whose the token is.
    /// </summary>
    /// <remarks>
    /// <c>users</c> (by Okta's search expression or a name's start, page by page by the cursor Okta's Link header names - its
    /// cursor, never its address), <c>user</c> (by id or login), <c>create_user</c> (its profile - a name, e-mail and login and
    /// the rest of Okta's attributes, custom ones as JSON - activated unless it says not; a password never a step's parameter:
    /// Okta e-mails the user to set one), <c>update_user</c> (the attributes given, the rest kept), <c>activate_user</c>,
    /// <c>deactivate_user</c>, <c>suspend_user</c>, <c>unsuspend_user</c> and <c>delete_user</c> (as Okta has it: an active user
    /// deactivated, a deactivated one deleted); <c>user_groups</c>, <c>groups</c> (by a name's start), <c>add_to_group</c> and
    /// <c>remove_from_group</c>. A record is a user's id, status and times with its profile's attributes beside them, a list
    /// or an object as its JSON. Every id, login, e-mail, attribute and search held to its shape before anything is sent. A
    /// throttled call waits as Okta says; a failure is thrown with its status and Okta's summary and causes.
    /// </remarks>
    public sealed class OktaConnector : IConnector, IConnectorExtractor
    {
        public const string UsersOperation = "users";

        public const string UserOperation = "user";

        public const string CreateUserOperation = "create_user";

        public const string UpdateUserOperation = "update_user";

        public const string ActivateUserOperation = "activate_user";

        public const string DeactivateUserOperation = "deactivate_user";

        public const string SuspendUserOperation = "suspend_user";

        public const string UnsuspendUserOperation = "unsuspend_user";

        public const string DeleteUserOperation = "delete_user";

        public const string UserGroupsOperation = "user_groups";

        public const string GroupsOperation = "groups";

        public const string AddToGroupOperation = "add_to_group";

        public const string RemoveFromGroupOperation = "remove_from_group";

        public const int DefaultMax = 1000;

        private const int PageSize = 200;

        private static readonly Regex Id = new("^[A-Za-z0-9]{20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Login = new("^[^\\s/]{1,100}$", RegexOptions.CultureInvariant);

        private static readonly Regex Email = new("^[^@\\s,]+@[^@\\s,]+\\.[^@\\s,]+$", RegexOptions.CultureInvariant);

        private static readonly Regex Link = new("<(?<url>[^>]*)>(?<attributes>(\\s*;\\s*[A-Za-z]+=\"[^\"]*\")*)", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            UsersOperation, UserOperation, CreateUserOperation, UpdateUserOperation, ActivateUserOperation, DeactivateUserOperation, SuspendUserOperation,
            UnsuspendUserOperation, DeleteUserOperation, UserGroupsOperation, GroupsOperation, AddToGroupOperation, RemoveFromGroupOperation,
        };

        // Okta's base profile attributes by the names a step gives them - its own names, in snake case.
        private static readonly (string Name, string Okta)[] Attributes =
        {
            ("first_name", "firstName"), ("last_name", "lastName"), ("email", "email"), ("login", "login"), ("second_email", "secondEmail"), ("middle_name", "middleName"),
            ("honorific_prefix", "honorificPrefix"), ("honorific_suffix", "honorificSuffix"), ("title", "title"), ("display_name", "displayName"), ("nick_name", "nickName"),
            ("profile_url", "profileUrl"), ("mobile_phone", "mobilePhone"), ("primary_phone", "primaryPhone"), ("street_address", "streetAddress"), ("city", "city"),
            ("state", "state"), ("zip_code", "zipCode"), ("country_code", "countryCode"), ("postal_address", "postalAddress"), ("preferred_language", "preferredLanguage"),
            ("locale", "locale"), ("timezone", "timezone"), ("user_type", "userType"), ("employee_number", "employeeNumber"), ("cost_center", "costCenter"),
            ("organization", "organization"), ("division", "division"), ("department", "department"), ("manager_id", "managerId"), ("manager", "manager"),
        };

        public string ConnectorType => "okta";

        public string DisplayName => "Okta";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => "https://example.okta.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (Security > API > Tokens)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri WebsiteUrl { get; } = new("https://www.okta.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.okta.com/docs/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's id or login.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "The group's id.", required: true);

        private static readonly ConnectorConfigField SendEmailField = Field("send_email", "Send e-mail", "true: Okta e-mails the org's admins; false (the default) not.", ConnectorConfigFieldType.Boolean);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] Profile(bool create) =>
            Attributes.Select(attribute => Field(attribute.Name, char.ToUpperInvariant(attribute.Name[0]) + attribute.Name[1..].Replace('_', ' '), $"Okta's {attribute.Okta}.", required: create && attribute.Name is "first_name" or "last_name" or "email" or "login"))
                .Append(Field("profile", "Custom attributes", "A JSON object of custom attributes - {\"badge\": \"K-17\"}.", ConnectorConfigFieldType.Multiline)).ToArray();

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(UsersOperation, "Get users", "n8n's Okta User Get Many.")
            {
                Parameters = Schema(Field("search", "Search", "Okta's search expression - profile.lastName sw \"Smi\"."), Field("q", "Name starts", "A first name, last name or e-mail's start."), MaxField),
            },
            new ConnectorOperation(UserOperation, "Get a user", "n8n's Okta User Get.") { Parameters = Schema(UserField) },
            new ConnectorOperation(CreateUserOperation, "Create a user", "n8n's Okta User Create.")
            {
                Parameters = Schema(Profile(create: true).Append(Field("activate", "Activate", "true (the default): active at once, e-mailed to set a password.", ConnectorConfigFieldType.Boolean)).ToArray()),
            },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "The attributes given, the rest kept - n8n's Okta User Update.") { Parameters = Schema(Profile(create: false).Prepend(UserField).ToArray()) },
            new ConnectorOperation(ActivateUserOperation, "Activate a user", "Okta e-mails them unless it says not.") { Parameters = Schema(UserField, Field("send_email", "Send e-mail", "true (the default) or false.", ConnectorConfigFieldType.Boolean)) },
            new ConnectorOperation(DeactivateUserOperation, "Deactivate a user", "n8n's Okta User Delete, its first step.") { Parameters = Schema(UserField, SendEmailField) },
            new ConnectorOperation(SuspendUserOperation, "Suspend a user", "Signing in refused until unsuspended.") { Parameters = Schema(UserField) },
            new ConnectorOperation(UnsuspendUserOperation, "Unsuspend a user", "Signing in allowed again.") { Parameters = Schema(UserField) },
            new ConnectorOperation(DeleteUserOperation, "Delete a user", "As Okta has it: an active user deactivated, a deactivated one deleted - n8n's Okta User Delete.") { Parameters = Schema(UserField, SendEmailField) },
            new ConnectorOperation(UserGroupsOperation, "Get a user's groups", "The groups they are in.") { Parameters = Schema(UserField, MaxField) },
            new ConnectorOperation(GroupsOperation, "Get groups", "By a name's start.") { Parameters = Schema(Field("q", "Name starts", "A group name's start."), MaxField) },
            new ConnectorOperation(AddToGroupOperation, "Add to a group", "A user added to a group.") { Parameters = Schema(GroupField, Field("user", "User", "The user's id.", required: true)) },
            new ConnectorOperation(RemoveFromGroupOperation, "Remove from a group", "A user taken out of a group.") { Parameters = Schema(GroupField, Field("user", "User", "The user's id.", required: true)) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var (me, _) = await session.SendAsync(HttpMethod.Get, "/users/me", null, cancellationToken);
                var login = Text((me as JsonObject)?["profile"]?["login"]);
                return ConnectionTestResult.Ok($"Okta: the token is {login ?? "a user"}'s.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? UsersOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Okta has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string UserOf(bool loginToo) => Values.Get(p, "user") is { } given
                ? Id.IsMatch(given) || (loginToo && Login.IsMatch(given)) ? Uri.EscapeDataString(given) : throw new InvalidOperationException($"'{given}' is not a user's {(loginToo ? "id or login" : "id - 20 letters and digits")} (user).")
                : throw new InvalidOperationException($"Give the user's {(loginToo ? "id or login" : "id")}.");
            string GroupOf() => Values.Get(p, "group") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a group's id - 20 letters and digits (group).") : throw new InvalidOperationException("Give the group's id.");
            bool? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;

            // A profile: Okta's attributes by their snake-case names, its custom ones from a JSON object.
            JsonObject ProfileOf()
            {
                var profile = new JsonObject();
                foreach (var (name, okta) in Attributes)
                {
                    if (Values.Get(p, name) is { } value)
                    {
                        profile[okta] = name is "email" or "second_email" && !Email.IsMatch(value) ? throw new InvalidOperationException($"'{value}' is not an e-mail ({name}).")
                            : name == "login" && !Login.IsMatch(value) ? throw new InvalidOperationException($"'{value}' is not a login - no spaces or slashes, 100 characters at most (login).")
                            : value;
                    }
                }

                if (Values.Get(p, "profile") is { } custom)
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

                    foreach (var (name, value) in parsed as JsonObject ?? throw new InvalidOperationException("The custom attributes are not a JSON object - {\"badge\": \"K-17\"}."))
                    {
                        profile[Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,99}$") ? name : throw new InvalidOperationException($"'{name}' is not an attribute's name (profile).")] = value?.DeepClone();
                    }
                }

                return profile;
            }

            using var client = session.Client;
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case UsersOperation or GroupsOperation or UserGroupsOperation:
                    {
                        var query = new List<string>();
                        string path;
                        if (operation == UsersOperation)
                        {
                            var search = Values.Get(p, "search");
                            var q = Values.Get(p, "q");
                            if (search is not null && q is not null)
                            {
                                throw new InvalidOperationException("Give a search or a name's start - Okta takes one of them.");
                            }

                            if (search is not null)
                            {
                                query.Add("search=" + Uri.EscapeDataString(search));
                            }

                            if (q is not null)
                            {
                                query.Add("q=" + Uri.EscapeDataString(q));
                            }

                            path = "/users";
                        }
                        else if (operation == GroupsOperation)
                        {
                            if (Values.Get(p, "q") is { } q)
                            {
                                query.Add("q=" + Uri.EscapeDataString(q));
                            }

                            path = "/groups";
                        }
                        else
                        {
                            path = $"/users/{UserOf(loginToo: true)}/groups";
                        }

                        // Okta pages by the cursor its Link header's next names - its after read, never the address beside it -
                        // the size fixed at the start, no next or an empty page the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        string? after = null;
                        while (true)
                        {
                            var (answer, link) = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Append($"limit={size}").Concat(after is null ? Array.Empty<string>() : new[] { "after=" + Uri.EscapeDataString(after) })), null, cancellationToken);
                            var items = (answer as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                            foreach (var item in items)
                            {
                                yield return Record(item);
                                if (++read >= max)
                                {
                                    yield break;
                                }
                            }

                            after = Next(link);
                            if (items.Count == 0 || after is null)
                            {
                                yield break;
                            }
                        }
                    }

                case UserOperation:
                    yield return Record((await session.SendAsync(HttpMethod.Get, $"/users/{UserOf(loginToo: true)}", null, cancellationToken)).Answer as JsonObject ?? new JsonObject());
                    yield break;
                case CreateUserOperation:
                    {
                        var profile = ProfileOf();
                        foreach (var (name, okta) in new[] { ("first_name", "firstName"), ("last_name", "lastName"), ("email", "email"), ("login", "login") })
                        {
                            if (!profile.ContainsKey(okta))
                            {
                                throw new InvalidOperationException($"Give the user's {name.Replace('_', ' ')}.");
                            }
                        }

                        var activate = FlagOf("activate") ?? true;
                        yield return Record((await session.SendAsync(HttpMethod.Post, "/users?activate=" + (activate ? "true" : "false"), new JsonObject { ["profile"] = profile }, cancellationToken)).Answer as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case UpdateUserOperation:
                    {
                        var user = UserOf(loginToo: true);
                        var profile = ProfileOf();
                        if (profile.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - an attribute of the user's.");
                        }

                        // A POST changes the attributes given and keeps the rest - a PUT would clear them.
                        yield return Record((await session.SendAsync(HttpMethod.Post, $"/users/{user}", new JsonObject { ["profile"] = profile }, cancellationToken)).Answer as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case ActivateUserOperation or DeactivateUserOperation or SuspendUserOperation or UnsuspendUserOperation:
                    {
                        var user = UserOf(loginToo: true);
                        var step = operation.Split('_')[0];
                        // Okta e-mails an activated user unless told not; the admins of a deactivation only when told.
                        var send = operation is ActivateUserOperation or DeactivateUserOperation ? "?sendEmail=" + ((FlagOf("send_email") ?? operation == ActivateUserOperation) ? "true" : "false") : "";
                        await session.SendAsync(HttpMethod.Post, $"/users/{user}/lifecycle/{step}{send}", null, cancellationToken);
                        yield return Done(("user", Uri.UnescapeDataString(user)), (step switch { "activate" => "activated", "deactivate" => "deactivated", "suspend" => "suspended", _ => "unsuspended" }, true));
                        yield break;
                    }

                case DeleteUserOperation:
                    {
                        var user = UserOf(loginToo: true);
                        await session.SendAsync(HttpMethod.Delete, $"/users/{user}?sendEmail=" + ((FlagOf("send_email") ?? false) ? "true" : "false"), null, cancellationToken);
                        yield return Done(("user", Uri.UnescapeDataString(user)), ("deleted", true));
                        yield break;
                    }

                default:
                    {
                        var group = GroupOf();
                        var user = UserOf(loginToo: false);
                        await session.SendAsync(operation == AddToGroupOperation ? HttpMethod.Put : HttpMethod.Delete, $"/groups/{group}/users/{user}", null, cancellationToken);
                        yield return Done(("group", group), ("user", user), (operation == AddToGroupOperation ? "added" : "removed", true));
                        yield break;
                    }
            }
        }

        // The cursor of the Link header's next page: the after its address carries, read alone.
        private static string? Next(string? link)
        {
            foreach (Match match in Link.Matches(link ?? ""))
            {
                if (Regex.IsMatch(match.Groups["attributes"].Value, "rel=\"next\"") && Stomme.Shared.Utilities.TryCreateAbsoluteUri(match.Groups["url"].Value, out var address))
                {
                    var after = address.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair[0] == "after");
                    return after is { Length: 2 } && after[1].Length > 0 ? Uri.UnescapeDataString(after[1]) : null;
                }
            }

            return null;
        }

        // A record: the user's or group's fields with its profile's attributes beside them, a list or an object as its JSON.
        private static ConnectorRecord Record(JsonObject item)
        {
            var fields = item.Where(pair => pair.Key is not "profile" and not "_links").ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal);
            if (item["profile"] is JsonObject profile)
            {
                foreach (var (name, value) in profile)
                {
                    fields.TryAdd(name, Value(value));
                }
            }

            return new ConnectorRecord(fields);
        }

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

        // One connection's calls: the org's API v1 with the token in Okta's SSWS header; a throttled call waiting as Okta says; a
        // failure thrown with its status and Okta's summary and causes.
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
                    throw new InvalidOperationException("Give the Okta org's address - https://example.okta.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^7] : baseUrl;
                return new Session(baseUrl + "/api/v1", Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The Okta connection has no API token."), egress.CreateHttpClient(timeout));
            }

            public async Task<(JsonNode? Answer, string? Link)> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(method, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("SSWS", token);
                    request.Headers.Accept.ParseAdd("application/json");
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
                    // A page that is not Okta's - a sign-in page, a proxy's - read as no users would hide that it never was.
                    throw new InvalidOperationException($"Okta answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = json as JsonObject;
                    var causes = (error?["errorCauses"] as JsonArray)?.OfType<JsonObject>().Select(cause => Text(cause["errorSummary"])).OfType<string>().ToList() ?? new List<string>();
                    var said = string.Join("; ", new[] { Text(error?["errorSummary"]) }.Concat(causes).Where(part => !string.IsNullOrEmpty(part)));
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return (json, response.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null);
            }
        }
    }
}
