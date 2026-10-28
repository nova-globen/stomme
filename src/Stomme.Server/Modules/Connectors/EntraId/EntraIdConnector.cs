#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Stomme.Modules.Connectors.EntraId
{
    /// <summary>
    /// Microsoft Entra ID (BL-96 increment 3, lifted from the predecessor platform's <c>entra</c>): a real directory connector - <c>users</c>
    /// and <c>devices</c> over Microsoft Graph with meaningful default <c>$select</c> projections, OAuth2 client credentials
    /// through the family's token provider, <c>@odata.nextLink</c> paging - every request through the host's egress (D110).
    /// </summary>
    /// <remarks>
    /// BL-229 (n8n's Microsoft Entra ID node) adds a user got, updated and deleted, a user added to and removed from a group, and
    /// a group read, got, created, updated and deleted - <c>users</c> and <c>devices</c> unchanged but for an optional
    /// <c>max</c>. A user's update is a JSON object of Graph's user properties - a password never among them; the few Graph
    /// keeps apart (about me, birthday, interests, my site, past projects, responsibilities, schools, skills) sent in a PATCH
    /// of their own. Every id, name, choice and value held to its shape before a token is asked for. A record is its fields as
    /// Graph gives them, a list or an object as its JSON, Graph's OData annotations left out. A failure is thrown with its
    /// status and Graph's code and message; a success answer that is not JSON a failure that says so.
    /// </remarks>
    public sealed class EntraIdConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://graph.microsoft.com";
        internal const string DefaultScope = "https://graph.microsoft.com/.default";
        internal const string DefaultPageSize = "999";
        internal const string DefaultUserColumns = "id,displayName,userPrincipalName,mail,accountEnabled,userType,createdDateTime";
        internal const string DefaultDeviceColumns = "id,displayName,deviceId,operatingSystem,operatingSystemVersion,accountEnabled,trustType,approximateLastSignInDateTime";
        internal const string DefaultGroupColumns = "id,displayName,description,mail,mailEnabled,mailNickname,securityEnabled,groupTypes,visibility,createdDateTime";

        public const string UsersOperation = "users";
        public const string DevicesOperation = "devices";
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

        private static readonly string[] Known =
        {
            UsersOperation, DevicesOperation, UserOperation, UpdateUserOperation, DeleteUserOperation, AddToGroupOperation, RemoveFromGroupOperation,
            GroupsOperation, GroupOperation, CreateGroupOperation, UpdateGroupOperation, DeleteGroupOperation,
        };

        private static readonly Regex ObjectId = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        private static readonly Regex PrincipalName = new("^[A-Za-z0-9._%+'#\\-]{1,113}@[A-Za-z0-9.\\-]{1,240}\\.[A-Za-z]{2,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex Property = new("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex Columns = new("^[A-Za-z0-9]+(,[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant);

        private static readonly Regex MailNickname = new("^[A-Za-z0-9!#$%&'*+\\-/=?^_`{|}~.]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Text = new("^[^\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Graph takes these user properties only in a request of their own.
        private static readonly string[] Apart = { "aboutMe", "birthday", "interests", "mySite", "pastProjects", "responsibilities", "schools", "skills" };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "entra";

        public string DisplayName => "Microsoft Entra ID";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-id"] = "Application (client) id (Entra ID > App registrations > the app)",
            ["client-secret"] = "Client secret (Certificates & secrets)",
            ["token-url"] = "Token endpoint - https://login.microsoftonline.com/{tenant id}/oauth2/v2.0/token",
        };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.MicrosoftCloudDirectory };

        public Uri? WebsiteUrl => new("https://www.microsoft.com/security/business/identity-access/microsoft-entra-id");

        public Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/graph/api/overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Description: "The token scope requested from the identity platform.")
            {
                Group = "Authentication",
                Default = DefaultScope,
                HelpText = "Change only for sovereign clouds (e.g. Graph US Government / 21Vianet endpoints).",
            },
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Objects requested per Graph page ($top).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "Its object id, or its user principal name - ada@kafe.onmicrosoft.com.", required: true);

        private static readonly ConnectorConfigField GroupField = Field("group", "Group", "Its object id.", required: true);

        private static readonly ConnectorConfigField MaxField = Field("max", "At most", "How many at most - every one when left empty.", ConnectorConfigFieldType.Integer);

        private static ConnectorConfigField[] GroupSchema(bool create) => new[]
        {
            Field("display_name", "Name", "Its name - 256 characters at most.", required: create),
            Field("mail_nickname", "Mail nickname", "The part of its address before the @ - 64 characters at most.", required: create),
            Field("description", "Description", "What it is for.", ConnectorConfigFieldType.Multiline),
            Field("membership_rule", "Membership rule", "A dynamic group's rule - (user.department -eq \"Kassa\")."),
            Field("rule_processing", "Rule processing", "On or Paused."),
            Field("visibility", "Visibility", "Public, Private or HiddenMembership."),
            Field("security_enabled", "Security enabled", "true or false.", ConnectorConfigFieldType.Boolean),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(UsersOperation, "Users", "Streams directory users from Microsoft Graph (/v1.0/users).") { Parameters = OperationParameters(DefaultUserColumns) },
            new ConnectorOperation(DevicesOperation, "Devices", "Streams registered devices from Microsoft Graph (/v1.0/devices).") { Parameters = OperationParameters(DefaultDeviceColumns) },
            new ConnectorOperation(UserOperation, "Get a user", "By its object id or user principal name - n8n's Microsoft Entra ID User Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { UserField, Field("select", "Columns", "Comma-separated properties.") with { Default = DefaultUserColumns } }),
            },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "Its properties changed, the rest kept - never a password - n8n's Microsoft Entra ID User Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { UserField, Field("values", "Values", "A JSON object of Graph's user properties - {\"jobTitle\": \"Barista\", \"accountEnabled\": false}.", ConnectorConfigFieldType.Multiline, required: true) }),
            },
            new ConnectorOperation(DeleteUserOperation, "Delete a user", "To the deleted items, for 30 days - n8n's Microsoft Entra ID User Delete.") { Parameters = new ConnectorConfigSchema(new[] { UserField }) },
            new ConnectorOperation(AddToGroupOperation, "Add a user to a group", "n8n's Microsoft Entra ID User Add to Group.") { Parameters = new ConnectorConfigSchema(new[] { UserField, GroupField }) },
            new ConnectorOperation(RemoveFromGroupOperation, "Remove a user from a group", "n8n's Microsoft Entra ID User Remove from Group.") { Parameters = new ConnectorConfigSchema(new[] { UserField, GroupField }) },
            new ConnectorOperation(GroupsOperation, "Groups", "Groups by a filter - n8n's Microsoft Entra ID Group Get Many.") { Parameters = OperationParameters(DefaultGroupColumns) },
            new ConnectorOperation(GroupOperation, "Get a group", "By its object id, its members too when asked - n8n's Microsoft Entra ID Group Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { GroupField, Field("select", "Columns", "Comma-separated properties.") with { Default = DefaultGroupColumns }, Field("include_members", "Members", "true: its members too.", ConnectorConfigFieldType.Boolean) }),
            },
            new ConnectorOperation(CreateGroupOperation, "Create a group", "A security or Microsoft 365 group, assigned or dynamic - n8n's Microsoft Entra ID Group Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { Field("kind", "Kind", "security (the default) or microsoft365."), Field("membership", "Membership", "assigned (the default) or dynamic.") }
                    .Concat(GroupSchema(create: true)).Append(Field("mail_enabled", "Mail enabled", "A Microsoft 365 group's - true or false.", ConnectorConfigFieldType.Boolean)).Append(Field("assignable_to_role", "Assignable to a role", "true: Entra roles may be given to it - set only when it is created.", ConnectorConfigFieldType.Boolean)).ToArray()),
            },
            new ConnectorOperation(UpdateGroupOperation, "Update a group", "What is given changed, the rest kept - n8n's Microsoft Entra ID Group Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { GroupField }.Concat(GroupSchema(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteGroupOperation, "Delete a group", "To the deleted items, for 30 days - n8n's Microsoft Entra ID Group Delete.") { Parameters = new ConnectorConfigSchema(new[] { GroupField }) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                var probe = GraphUrl(context.Endpoint, "users", new[] { "$top=1", "$select=id" });
                using var client = BearerClient.Create(context.Egress, context.Timeout, token);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri(probe)), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "Microsoft Graph", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"Microsoft Graph responded with {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail(FailureMessage(response.StatusCode));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? UsersOperation
                : Known.FirstOrDefault(known => string.Equals(known, context.OperationName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Entra connector - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : int.MaxValue;
            if (operation is UsersOperation or DevicesOperation or GroupsOperation)
            {
                var (path, defaultColumns) = operation switch
                {
                    UsersOperation => ("users", DefaultUserColumns),
                    DevicesOperation => ("devices", DefaultDeviceColumns),
                    _ => ("groups", DefaultGroupColumns),
                };
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = BearerClient.Create(context.Egress, context.Timeout, token);
                string? next = GraphUrl(context.Endpoint, path, BuildQuery(context.Settings, p, defaultColumns, max));
                var read = 0;
                while (next is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var payload = await BearerClient.GetAsync(client, next, FailureMessage, cancellationToken);
                    var (records, nextLink) = GraphPage.Read(payload, next);
                    foreach (var record in records)
                    {
                        yield return record;
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    next = nextLink;
                }

                yield break;
            }

            // Everything held to its shape before a token is asked for.
            string UserOf() => Values.Get(p, "user") is { } given
                ? ObjectId.IsMatch(given) || PrincipalName.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a user's object id or user principal name (user).")
                : throw new InvalidOperationException("Give the user - its object id or user principal name (user).");
            string GroupOf() => Values.Get(p, "group") is { } given
                ? ObjectId.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a group's object id (group).")
                : throw new InvalidOperationException("Give the group - its object id (group).");
            string? TextOf(string name, int most = 1024) => Values.Get(p, name) is { } given
                ? Text.IsMatch(given) && given.Length <= most ? given : throw new InvalidOperationException($"'{given}' is not a text of {most.ToString("N0", CultureInfo.InvariantCulture)} characters at most, without control characters ({name}).")
                : null;
            bool? FlagOf(string name) => Values.Get(p, name) is { } given
                ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).")
                : null;
            string? ChoiceOf(string name, params string[] choices) => Values.Get(p, name) is { } given
                ? choices.FirstOrDefault(choice => string.Equals(choice, given, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{given}' is not {string.Join(", ", choices[..^1])} or {choices[^1]} ({name}).")
                : null;
            string? ColumnsOf() => Values.Get(p, "select") is { } given ? Columns.IsMatch(given.Replace(" ", "", StringComparison.Ordinal)) ? given.Replace(" ", "", StringComparison.Ordinal) : throw new InvalidOperationException($"'{given}' is not a comma-separated list of properties (select).") : null;

            var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint) + "/v1.0";
            var requests = new List<(HttpMethod Method, string Url, JsonObject? Body)>();
            Dictionary<string, object?>? answer = null;
            switch (operation)
            {
                case UserOperation:
                    requests.Add((HttpMethod.Get, $"{api}/users/{Uri.EscapeDataString(UserOf())}?$select={Uri.EscapeDataString(ColumnsOf() ?? DefaultUserColumns)}", null));
                    break;
                case UpdateUserOperation:
                    {
                        var user = UserOf();
                        var values = ValuesOf(p);
                        var main = new JsonObject();
                        var apart = new JsonObject();
                        foreach (var (name, value) in values)
                        {
                            (Apart.Contains(name, StringComparer.Ordinal) ? apart : main)[name] = value?.DeepClone();
                        }

                        foreach (var body in new[] { main, apart }.Where(body => body.Count > 0))
                        {
                            requests.Add((HttpMethod.Patch, $"{api}/users/{Uri.EscapeDataString(user)}", body));
                        }

                        answer = new() { ["id"] = user, ["updated"] = true };
                        break;
                    }

                case DeleteUserOperation:
                    {
                        var user = UserOf();
                        requests.Add((HttpMethod.Delete, $"{api}/users/{Uri.EscapeDataString(user)}", null));
                        answer = new() { ["id"] = user, ["deleted"] = true };
                        break;
                    }

                case AddToGroupOperation:
                case RemoveFromGroupOperation:
                    {
                        var user = UserOf();
                        var group = GroupOf();
                        if (operation == AddToGroupOperation)
                        {
                            requests.Add((HttpMethod.Post, $"{api}/groups/{group}/members/$ref", new JsonObject { ["@odata.id"] = $"{api}/directoryObjects/{Uri.EscapeDataString(user)}" }));
                        }
                        else
                        {
                            requests.Add((HttpMethod.Delete, $"{api}/groups/{group}/members/{Uri.EscapeDataString(user)}/$ref", null));
                        }

                        answer = new() { ["user"] = user, ["group"] = group, [operation == AddToGroupOperation ? "added" : "removed"] = true };
                        break;
                    }

                case GroupOperation:
                    {
                        var group = GroupOf();
                        var members = FlagOf("include_members") == true ? "&$expand=" + Uri.EscapeDataString("members($select=id,displayName,userPrincipalName,mail)") : "";
                        requests.Add((HttpMethod.Get, $"{api}/groups/{group}?$select={Uri.EscapeDataString(ColumnsOf() ?? DefaultGroupColumns)}{members}", null));
                        break;
                    }

                case DeleteGroupOperation:
                    {
                        var group = GroupOf();
                        requests.Add((HttpMethod.Delete, $"{api}/groups/{group}", null));
                        answer = new() { ["id"] = group, ["deleted"] = true };
                        break;
                    }

                default:
                    {
                        // A group created or updated: what is given, under Graph's names.
                        var create = operation == CreateGroupOperation;
                        var group = create ? null : GroupOf();
                        var body = new JsonObject();
                        if (create)
                        {
                            var microsoft365 = (ChoiceOf("kind", "security", "microsoft365") ?? "security") == "microsoft365";
                            var dynamic = (ChoiceOf("membership", "assigned", "dynamic") ?? "assigned") == "dynamic";
                            var types = new JsonArray();
                            if (microsoft365)
                            {
                                types.Add("Unified");
                            }

                            if (dynamic)
                            {
                                types.Add("DynamicMembership");
                            }

                            body["groupTypes"] = types;
                            if (!microsoft365 && FlagOf("mail_enabled") == true)
                            {
                                throw new InvalidOperationException("A security group is not mail-enabled - make it a microsoft365 group (mail_enabled).");
                            }

                            body["mailEnabled"] = microsoft365 && (FlagOf("mail_enabled") ?? true);
                            body["securityEnabled"] = FlagOf("security_enabled") ?? !microsoft365;
                            if (FlagOf("assignable_to_role") == true)
                            {
                                body["isAssignableToRole"] = true;
                            }

                            if (dynamic && Values.Get(p, "membership_rule") is null)
                            {
                                throw new InvalidOperationException("Give a dynamic group's membership rule (membership_rule).");
                            }
                        }
                        else if (FlagOf("security_enabled") is { } security)
                        {
                            body["securityEnabled"] = security;
                        }

                        if (TextOf("display_name", 256) is { } name)
                        {
                            body["displayName"] = name;
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the group's name (display_name).");
                        }

                        if (Values.Get(p, "mail_nickname") is { } nickname)
                        {
                            body["mailNickname"] = MailNickname.IsMatch(nickname) ? nickname : throw new InvalidOperationException($"'{nickname}' is not a mail nickname - the part before the @, 64 characters at most (mail_nickname).");
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give the group's mail nickname (mail_nickname).");
                        }

                        if (TextOf("description") is { } description)
                        {
                            body["description"] = description;
                        }

                        if (TextOf("membership_rule", 3072) is { } rule)
                        {
                            body["membershipRule"] = rule;
                        }

                        if (ChoiceOf("rule_processing", "On", "Paused") is { } processing)
                        {
                            body["membershipRuleProcessingState"] = processing;
                        }

                        if (ChoiceOf("visibility", "Public", "Private", "HiddenMembership") is { } visibility)
                        {
                            body["visibility"] = visibility;
                        }

                        if (!create && body.Count == 0)
                        {
                            throw new InvalidOperationException("Give what to change - its name, mail nickname, description, membership rule, rule processing, visibility or security.");
                        }

                        requests.Add(create ? (HttpMethod.Post, $"{api}/groups", body) : (HttpMethod.Patch, $"{api}/groups/{group}", body));
                        if (!create)
                        {
                            answer = new() { ["id"] = group, ["updated"] = true };
                        }

                        break;
                    }
            }

            {
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                JsonNode? last = null;
                foreach (var (method, url, body) in requests)
                {
                    last = await SendAsync(client, token, method, url, body, cancellationToken);
                }

                yield return answer is not null ? new ConnectorRecord(answer) : Record(last as JsonObject ?? throw new InvalidOperationException("Microsoft Graph answered with nothing."));
            }
        }

        // A user's values: a JSON object of Graph's user properties - never a password.
        private static JsonObject ValuesOf(IReadOnlyDictionary<string, string> parameters)
        {
            JsonNode? parsed;
            try
            {
                parsed = Values.Get(parameters, "values") is { } given ? JsonNode.Parse(given) : throw new InvalidOperationException("Give what to change - a JSON object of Graph's user properties (values).");
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is not JsonObject values || values.Count == 0)
            {
                throw new InvalidOperationException("The values are not a JSON object of Graph's user properties - {\"jobTitle\": \"Barista\"} (values).");
            }

            foreach (var (name, _) in values)
            {
                if (!Property.IsMatch(name))
                {
                    throw new InvalidOperationException($"'{name}' is not a property's name - jobTitle, accountEnabled (values).");
                }

                if (name is "passwordProfile" or "password" or "passwordPolicies")
                {
                    throw new InvalidOperationException($"'{name}' is a password's - a password is never a step's parameter: set it in Entra ID (values).");
                }
            }

            return values;
        }

        private static ConnectorRecord Record(JsonObject item) =>
            new(item.Where(pair => !pair.Key.StartsWith("@odata.", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

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

        // Graph's answer as JSON, a throttled call waiting as Graph says; a failure thrown with its status and Graph's code and
        // message; a success that is not JSON a failure that says so (an empty one - a write's 204 - nothing).
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
                throw new InvalidOperationException($"Microsoft Graph answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Words(json?["error"]?["code"]);
                var said = Words(json?["error"]?["message"]);
                var text = string.Join(": ", new[] { code, said }.Where(part => !string.IsNullOrEmpty(part)));
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(text.Length == 0 ? "." : " " + (text.Length > 300 ? text[..300] : text))}");
            }

            return json;
        }

        private static ConnectorConfigSchema OperationParameters(string defaultColumns) => new(new[]
        {
            new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Description: "Optional OData $filter expression."),
            new ConnectorConfigField("select", "Columns", ConnectorConfigFieldType.Text, Description: "Comma-separated $select list.") { Default = defaultColumns },
            new ConnectorConfigField("top", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
            MaxField,
        });

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        private static string GraphUrl(string endpoint, string path, IReadOnlyList<string> query) =>
            $"{Values.BaseUrl(endpoint, DefaultEndpoint)}/v1.0/{path}?{string.Join('&', query)}";

        private static List<string> BuildQuery(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters, string defaultColumns, int max)
        {
            var top = Values.Get(parameters, "top") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (max < int.MaxValue && int.TryParse(top, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > max)
            {
                top = max.ToString(CultureInfo.InvariantCulture);
            }

            var columns = Values.GetOrDefault(parameters, "select", defaultColumns);
            var query = new List<string> { $"$top={Uri.EscapeDataString(top)}", $"$select={Uri.EscapeDataString(columns)}" };
            if (Values.Get(parameters, "filter") is { } filter)
            {
                query.Add($"$filter={Uri.EscapeDataString(filter)}");
            }

            return query;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Microsoft Graph rejected the token (401) - check the tenant's token URL and the client credentials.",
            HttpStatusCode.Forbidden => "Microsoft Graph denied access (403) - grant the app the User.Read.All / Device.Read.All (or Directory.Read.All) application permission with admin consent.",
            _ => $"Microsoft Graph request failed with {(int)status} {status}.",
        };
    }
}
