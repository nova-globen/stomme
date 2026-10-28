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

namespace Stomme.Modules.Connectors.Grafana
{
    /// <summary>
    /// Grafana (BL-229, n8n's Grafana node: its dashboards, teams, team members and the organization's users): a Grafana
    /// server's HTTP API with a service account's token as a bearer token, over the host's egress. A test names the token's
    /// organization.
    /// </summary>
    /// <remarks>
    /// <c>dashboards</c> (by a search, tags and folders, page by page), <c>dashboard</c> (by its uid or address),
    /// <c>create_dashboard</c> (in a folder, with tags), <c>update_dashboard</c> (its title, folder or tags - its panels kept:
    /// the dashboard is read first and saved at its version, never replaced by a title alone) and <c>delete_dashboard</c>;
    /// <c>teams</c> (by name or a search, page by page), <c>team</c>, <c>create_team</c>, <c>update_team</c> (what is not given
    /// kept) and <c>delete_team</c>; <c>team_members</c>, <c>add_team_member</c> and <c>remove_team_member</c>; <c>users</c> (the
    /// organization's), <c>add_user</c> (by login or e-mail with a role), <c>update_user</c> (a role) and <c>remove_user</c>. A
    /// record is its fields as Grafana gives them, a list or an object as its JSON. Every id, uid, role, folder and tag held to
    /// its shape before anything is sent. A throttled call waits as Grafana says; a failure is thrown with its status and
    /// Grafana's message.
    /// </remarks>
    public sealed class GrafanaConnector : IConnector, IConnectorExtractor
    {
        public const string DashboardsOperation = "dashboards";

        public const string DashboardOperation = "dashboard";

        public const string CreateDashboardOperation = "create_dashboard";

        public const string UpdateDashboardOperation = "update_dashboard";

        public const string DeleteDashboardOperation = "delete_dashboard";

        public const string TeamsOperation = "teams";

        public const string TeamOperation = "team";

        public const string CreateTeamOperation = "create_team";

        public const string UpdateTeamOperation = "update_team";

        public const string DeleteTeamOperation = "delete_team";

        public const string TeamMembersOperation = "team_members";

        public const string AddTeamMemberOperation = "add_team_member";

        public const string RemoveTeamMemberOperation = "remove_team_member";

        public const string UsersOperation = "users";

        public const string AddUserOperation = "add_user";

        public const string UpdateUserOperation = "update_user";

        public const string RemoveUserOperation = "remove_user";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Uid = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            DashboardsOperation, DashboardOperation, CreateDashboardOperation, UpdateDashboardOperation, DeleteDashboardOperation, TeamsOperation, TeamOperation, CreateTeamOperation,
            UpdateTeamOperation, DeleteTeamOperation, TeamMembersOperation, AddTeamMemberOperation, RemoveTeamMemberOperation, UsersOperation, AddUserOperation, UpdateUserOperation,
            RemoveUserOperation,
        };

        private static readonly string[] Roles = { "Viewer", "Editor", "Admin" };

        public string ConnectorType => "grafana";

        public string DisplayName => "Grafana";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public string EndpointExample => "https://grafana.example.com";

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Service account token (Administration > Service accounts)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://grafana.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://grafana.com/docs/grafana/latest/developers/http_api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static readonly ConnectorConfigField DashboardField = Field("dashboard", "Dashboard", "Its uid or address - https://grafana.example.com/d/abc123/sales.", required: true);

        private static readonly ConnectorConfigField TeamField = Field("team", "Team", "The team's id.", required: true);

        private static readonly ConnectorConfigField UserField = Field("user", "User", "The user's id.", required: true);

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(DashboardsOperation, "Get dashboards", "n8n's Grafana Dashboard Get Many.")
            {
                Parameters = Schema(Field("query", "Query", "A part of their title."), Field("tags", "Tags", "Comma-separated - all of them."), Field("folders", "Folders", "Comma-separated folder uids."), MaxField),
            },
            new ConnectorOperation(DashboardOperation, "Get a dashboard", "n8n's Grafana Dashboard Get.") { Parameters = Schema(DashboardField) },
            new ConnectorOperation(CreateDashboardOperation, "Create a dashboard", "n8n's Grafana Dashboard Create.")
            {
                Parameters = Schema(Field("title", "Title", "Its title.", required: true), Field("folder", "Folder", "A folder's uid."), Field("tags", "Tags", "Comma-separated.")),
            },
            new ConnectorOperation(UpdateDashboardOperation, "Update a dashboard", "Its panels kept - n8n's Grafana Dashboard Update.")
            {
                Parameters = Schema(DashboardField, Field("title", "Title", "Its new title."), Field("folder", "Folder", "A folder's uid."), Field("tags", "Tags", "Comma-separated - they replace its tags.")),
            },
            new ConnectorOperation(DeleteDashboardOperation, "Delete a dashboard", "n8n's Grafana Dashboard Delete.") { Parameters = Schema(DashboardField) },
            new ConnectorOperation(TeamsOperation, "Get teams", "n8n's Grafana Team Get Many.") { Parameters = Schema(Field("name", "Name", "A team's exact name."), Field("query", "Query", "A part of their name."), MaxField) },
            new ConnectorOperation(TeamOperation, "Get a team", "n8n's Grafana Team Get.") { Parameters = Schema(TeamField) },
            new ConnectorOperation(CreateTeamOperation, "Create a team", "n8n's Grafana Team Create.") { Parameters = Schema(Field("name", "Name", "Its name.", required: true), Field("email", "E-mail", "Its e-mail.")) },
            new ConnectorOperation(UpdateTeamOperation, "Update a team", "What is not given kept - n8n's Grafana Team Update.") { Parameters = Schema(TeamField, Field("name", "Name", "Its new name."), Field("email", "E-mail", "Its new e-mail.")) },
            new ConnectorOperation(DeleteTeamOperation, "Delete a team", "n8n's Grafana Team Delete.") { Parameters = Schema(TeamField) },
            new ConnectorOperation(TeamMembersOperation, "Get team members", "n8n's Grafana Team Member Get Many.") { Parameters = Schema(TeamField, MaxField) },
            new ConnectorOperation(AddTeamMemberOperation, "Add a team member", "n8n's Grafana Team Member Add.") { Parameters = Schema(TeamField, UserField) },
            new ConnectorOperation(RemoveTeamMemberOperation, "Remove a team member", "n8n's Grafana Team Member Remove.") { Parameters = Schema(TeamField, UserField) },
            new ConnectorOperation(UsersOperation, "Get users", "The organization's - n8n's Grafana User Get Many.") { Parameters = Schema(MaxField) },
            new ConnectorOperation(AddUserOperation, "Add a user", "An existing user to the organization.")
            {
                Parameters = Schema(Field("login", "Login or e-mail", "The user's login or e-mail.", required: true), Field("role", "Role", "Viewer (the default), Editor or Admin.")),
            },
            new ConnectorOperation(UpdateUserOperation, "Update a user", "Their role - n8n's Grafana User Update.") { Parameters = Schema(UserField, Field("role", "Role", "Viewer, Editor or Admin.", required: true)) },
            new ConnectorOperation(RemoveUserOperation, "Remove a user", "From the organization - n8n's Grafana User Delete.") { Parameters = Schema(UserField) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var organization = await session.SendAsync(HttpMethod.Get, "/org", null, cancellationToken) as JsonObject;
                return ConnectionTestResult.Ok($"Grafana: the token is {(Text(organization?["name"]) is { Length: > 0 } name ? name : "an organization")}'s.");
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
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? DashboardsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Grafana has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            string IdOf(string name, string what) => Values.Get(p, name) is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id ({name}).") : throw new InvalidOperationException($"Give the {what}'s id.");
            string? FolderOf() => Values.Get(p, "folder") is { } folder ? Uid.IsMatch(folder) ? folder : throw new InvalidOperationException($"'{folder}' is not a folder's uid - letters, digits, '-' and '_' (folder).") : null;
            JsonArray? TagsOf() => Values.Get(p, "tags") is { } tags ? new JsonArray(tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Select(tag => (JsonNode?)JsonValue.Create(tag.Length <= 50 ? tag : throw new InvalidOperationException($"'{tag}' is longer than a tag's 50 characters (tags)."))).ToArray()) : null;
            string RoleOf(bool required) => Values.Get(p, "role") is { } role ? Roles.FirstOrDefault(known => string.Equals(known, role, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"'{role}' is not Viewer, Editor or Admin (role).")
                : required ? throw new InvalidOperationException("Give the role - Viewer, Editor or Admin.") : "Viewer";

            // A dashboard by its uid, or by the address it opens at - /d/{uid}/...
            string DashboardOf()
            {
                var given = Values.Get(p, "dashboard") ?? throw new InvalidOperationException("Give the dashboard - its uid or address.");
                if (given.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Uri.TryCreate(given, UriKind.Absolute, out var address) ? Regex.Match(address.AbsolutePath, "/d/([^/]+)") : Match.Empty;
                    given = match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : throw new InvalidOperationException($"'{given}' is not a dashboard's address - https://grafana.example.com/d/abc123/sales (dashboard).");
                }

                return Uid.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not a dashboard's uid - letters, digits, '-' and '_' (dashboard).");
            }

            using var client = session.Client;
            ConnectorRecord Done(params (string Name, object? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
            switch (operation)
            {
                case DashboardsOperation or TeamsOperation:
                    {
                        var query = new List<string>();
                        if (operation == DashboardsOperation)
                        {
                            query.Add("type=dash-db");
                            if (Values.Get(p, "query") is { } search)
                            {
                                query.Add("query=" + Uri.EscapeDataString(search));
                            }

                            foreach (var tag in TagsOf() ?? new JsonArray())
                            {
                                query.Add("tag=" + Uri.EscapeDataString(tag!.GetValue<string>()));
                            }

                            foreach (var folder in (Values.Get(p, "folders") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            {
                                query.Add("folderUIDs=" + (Uid.IsMatch(folder) ? folder : throw new InvalidOperationException($"'{folder}' is not a folder's uid (folders).")));
                            }
                        }
                        else
                        {
                            foreach (var name in new[] { "name", "query" })
                            {
                                if (Values.Get(p, name) is { } value)
                                {
                                    query.Add(name + "=" + Uri.EscapeDataString(value));
                                }
                            }
                        }

                        // Page by page from the first, the size fixed at the start: a short or empty page the end.
                        var size = Math.Min(PageSize, max);
                        var read = 0;
                        for (var page = 1; ; page++)
                        {
                            var path = operation == DashboardsOperation ? "/search" : "/teams/search";
                            var answer = await session.SendAsync(HttpMethod.Get, path + "?" + string.Join("&", query.Concat(new[] { (operation == DashboardsOperation ? "limit=" : "perpage=") + size, "page=" + page })), null, cancellationToken);
                            var items = ((operation == DashboardsOperation ? answer : answer?["teams"]) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
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

                case DashboardOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, $"/dashboards/uid/{DashboardOf()}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateDashboardOperation:
                    {
                        var dashboard = new JsonObject { ["id"] = null, ["uid"] = null, ["title"] = Values.Get(p, "title") ?? throw new InvalidOperationException("Give the dashboard's title.") };
                        if (TagsOf() is { } tags)
                        {
                            dashboard["tags"] = tags;
                        }

                        var body = new JsonObject { ["dashboard"] = dashboard, ["overwrite"] = false };
                        if (FolderOf() is { } folder)
                        {
                            body["folderUid"] = folder;
                        }

                        yield return Record(await session.SendAsync(HttpMethod.Post, "/dashboards/db", body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case UpdateDashboardOperation:
                    {
                        var uid = DashboardOf();
                        var title = Values.Get(p, "title");
                        var folder = FolderOf();
                        var tags = TagsOf();
                        if (title is null && folder is null && tags is null)
                        {
                            throw new InvalidOperationException("Give what to change - its title, folder or tags.");
                        }

                        // Its panels kept: the dashboard read and saved whole at its version, so a change made meanwhile is refused.
                        var current = await session.SendAsync(HttpMethod.Get, $"/dashboards/uid/{uid}", null, cancellationToken) as JsonObject;
                        var dashboard = current?["dashboard"] as JsonObject ?? throw new InvalidOperationException($"Grafana has no dashboard '{uid}'.");
                        dashboard = (JsonObject)dashboard.DeepClone();
                        if (title is not null)
                        {
                            dashboard["title"] = title;
                        }

                        if (tags is not null)
                        {
                            dashboard["tags"] = tags;
                        }

                        var body = new JsonObject
                        {
                            ["dashboard"] = dashboard,
                            ["folderUid"] = folder ?? Text(current["meta"]?["folderUid"]),
                            ["overwrite"] = false,
                        };
                        yield return Record(await session.SendAsync(HttpMethod.Post, "/dashboards/db", body, cancellationToken) as JsonObject ?? new JsonObject());
                        yield break;
                    }

                case DeleteDashboardOperation:
                    {
                        var uid = DashboardOf();
                        var answer = await session.SendAsync(HttpMethod.Delete, $"/dashboards/uid/{uid}", null, cancellationToken) as JsonObject;
                        yield return Done(("uid", uid), ("title", Text(answer?["title"])), ("deleted", true));
                        yield break;
                    }

                case TeamOperation:
                    yield return Record(await session.SendAsync(HttpMethod.Get, $"/teams/{IdOf("team", "team")}", null, cancellationToken) as JsonObject ?? new JsonObject());
                    yield break;
                case CreateTeamOperation:
                    {
                        var body = new JsonObject { ["name"] = Values.Get(p, "name") ?? throw new InvalidOperationException("Give the team's name.") };
                        if (Values.Get(p, "email") is { } email)
                        {
                            body["email"] = email.Contains('@') && !email.Any(char.IsWhiteSpace) ? email : throw new InvalidOperationException($"'{email}' is not an e-mail (email).");
                        }

                        var answer = await session.SendAsync(HttpMethod.Post, "/teams", body, cancellationToken) as JsonObject;
                        yield return Done(("id", answer?["teamId"] is JsonValue id && id.TryGetValue<long>(out var teamId) ? teamId : null), ("name", Text(body["name"])), ("message", Text(answer?["message"])));
                        yield break;
                    }

                case UpdateTeamOperation:
                    {
                        var team = IdOf("team", "team");
                        var name = Values.Get(p, "name");
                        var email = Values.Get(p, "email");
                        if (name is null && email is null)
                        {
                            throw new InvalidOperationException("Give what to change - its name or e-mail.");
                        }

                        if (email is not null && !(email.Contains('@') && !email.Any(char.IsWhiteSpace)))
                        {
                            throw new InvalidOperationException($"'{email}' is not an e-mail (email).");
                        }

                        // Grafana sets both: what is not given read and kept.
                        var current = await session.SendAsync(HttpMethod.Get, $"/teams/{team}", null, cancellationToken) as JsonObject;
                        var body = new JsonObject { ["name"] = name ?? Text(current?["name"]), ["email"] = email ?? Text(current?["email"]) ?? "" };
                        var answer = await session.SendAsync(HttpMethod.Put, $"/teams/{team}", body, cancellationToken) as JsonObject;
                        yield return Done(("id", long.Parse(team, CultureInfo.InvariantCulture)), ("name", Text(body["name"])), ("email", Text(body["email"])), ("message", Text(answer?["message"])));
                        yield break;
                    }

                case DeleteTeamOperation:
                    {
                        var team = IdOf("team", "team");
                        await session.SendAsync(HttpMethod.Delete, $"/teams/{team}", null, cancellationToken);
                        yield return Done(("id", long.Parse(team, CultureInfo.InvariantCulture)), ("deleted", true));
                        yield break;
                    }

                case TeamMembersOperation:
                    foreach (var item in (await session.SendAsync(HttpMethod.Get, $"/teams/{IdOf("team", "team")}/members", null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                    {
                        yield return Record(item);
                    }

                    yield break;
                case AddTeamMemberOperation or RemoveTeamMemberOperation:
                    {
                        var team = IdOf("team", "team");
                        var user = IdOf("user", "user");
                        var answer = operation == AddTeamMemberOperation
                            ? await session.SendAsync(HttpMethod.Post, $"/teams/{team}/members", new JsonObject { ["userId"] = long.Parse(user, CultureInfo.InvariantCulture) }, cancellationToken) as JsonObject
                            : await session.SendAsync(HttpMethod.Delete, $"/teams/{team}/members/{user}", null, cancellationToken) as JsonObject;
                        yield return Done(("team", long.Parse(team, CultureInfo.InvariantCulture)), ("user", long.Parse(user, CultureInfo.InvariantCulture)), (operation == AddTeamMemberOperation ? "added" : "removed", true), ("message", Text(answer?["message"])));
                        yield break;
                    }

                case UsersOperation:
                    foreach (var item in (await session.SendAsync(HttpMethod.Get, "/org/users", null, cancellationToken) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(max))
                    {
                        yield return Record(item);
                    }

                    yield break;
                case AddUserOperation:
                    {
                        var body = new JsonObject { ["loginOrEmail"] = Values.Get(p, "login") ?? throw new InvalidOperationException("Give the user's login or e-mail."), ["role"] = RoleOf(required: false) };
                        var answer = await session.SendAsync(HttpMethod.Post, "/org/users", body, cancellationToken) as JsonObject;
                        yield return Done(("id", answer?["userId"] is JsonValue id && id.TryGetValue<long>(out var userId) ? userId : null), ("login", Text(body["loginOrEmail"])), ("role", Text(body["role"])), ("message", Text(answer?["message"])));
                        yield break;
                    }

                case UpdateUserOperation:
                    {
                        var user = IdOf("user", "user");
                        var role = RoleOf(required: true);
                        var answer = await session.SendAsync(HttpMethod.Patch, $"/org/users/{user}", new JsonObject { ["role"] = role }, cancellationToken) as JsonObject;
                        yield return Done(("id", long.Parse(user, CultureInfo.InvariantCulture)), ("role", role), ("message", Text(answer?["message"])));
                        yield break;
                    }

                default:
                    {
                        var user = IdOf("user", "user");
                        var answer = await session.SendAsync(HttpMethod.Delete, $"/org/users/{user}", null, cancellationToken) as JsonObject;
                        yield return Done(("id", long.Parse(user, CultureInfo.InvariantCulture)), ("removed", true), ("message", Text(answer?["message"])));
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

        // One connection's calls: the server's HTTP API with the token as a bearer token; a throttled call waiting as Grafana
        // says; a failure thrown with its status and Grafana's message.
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
                    throw new InvalidOperationException("Give the Grafana server's address - https://grafana.example.com.");
                }

                var baseUrl = Values.BaseUrl(endpoint, "");
                baseUrl = baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^4] : baseUrl;
                return new Session(baseUrl + "/api", Values.Get(credentials, "token") ?? throw new InvalidOperationException("The Grafana connection has no token."), egress.CreateHttpClient(timeout));
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
                    // A page that is not Grafana's - its own sign-in page, a proxy's - read as no dashboards would hide that it never was.
                    throw new InvalidOperationException($"Grafana answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var said = Text((json as JsonObject)?["message"]);
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(said) ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
