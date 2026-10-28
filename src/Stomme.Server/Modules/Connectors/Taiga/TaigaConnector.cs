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

namespace Stomme.Modules.Connectors.Taiga
{
    /// <summary>
    /// Taiga (BL-229, n8n's Taiga node: epics, issues, tasks and user stories) over Taiga's API (<c>{address}/api/v1</c>), signed
    /// in as n8n's credential does - a user's name or e-mail and password for a token (<c>POST /auth</c>), sent as a bearer token.
    /// A test names the user the connection signs in as.
    /// </summary>
    /// <remarks>
    /// Each kind listed (a project's, by assignee, status, tags, closed or not, and its own filters - an issue's priority,
    /// severity, type and order, a task's user story, a user story's epic), read, made, changed and deleted; <c>projects</c>, the
    /// user's. A status, priority, severity, type or assignee by its id or by its name in the project. Measured against Taiga
    /// 6.10 (<c>scripts/taiga-test-server.ps1</c>), which says little of what it does not take: a list for a project that does
    /// not exist, or under a filter it does not know, is answered as if nothing were wrong - n8n's "Is Closed" and its task's
    /// "User Story" filters reach Taiga under names it ignores, so they filter nothing - and a delete of an item that is not
    /// there is a 403. So the project is read before a list, the filters go under Taiga's own names (<c>status__is_closed</c>,
    /// <c>user_story</c>), and a change reads the item first for the version Taiga requires (a change without it, or with an old
    /// one, is refused). Every id and number held to Taiga's shape; Taiga's message and the field it refused on a failure.
    /// </remarks>
    public sealed class TaigaConnector : IConnector, IConnectorExtractor
    {
        public const int DefaultMax = 50; // n8n's limit

        private const int PageSize = 100;

        private const int MostText = 1048576;

        private sealed record Kind(string Name, string Plural, string Route, string Statuses, string[] Extra, string[] Filters);

        // Each kind: its operations' names, its route, its statuses' route, the fields only it takes, the filters only it takes.
        private static readonly Kind[] Kinds =
        {
            new("epic", "epics", "/epics", "/epic-statuses", new[] { "color" }, Array.Empty<string>()),
            new("issue", "issues", "/issues", "/issue-statuses", new[] { "priority", "severity", "type", "milestone" }, new[] { "priority", "severity", "type", "order_by" }),
            new("task", "tasks", "/tasks", "/task-statuses", new[] { "user_story", "milestone", "us_order", "taskboard_order" }, new[] { "user_story", "milestone" }),
            new("user_story", "user_stories", "/userstories", "/userstory-statuses", new[] { "milestone", "backlog_order", "kanban_order", "sprint_order" }, new[] { "epic", "milestone", "archived" }),
        };

        private static readonly string[] Orders = { "assigned_to", "created_date", "modified_date", "owner", "priority", "severity", "status", "subject", "type" };

        private static readonly Regex ColourPattern = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string ConnectorType => "taiga";

        public string DisplayName => "Taiga";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "Username or e-mail",
            ["password"] = "Password",
        };

        public string EndpointExample => "https://taiga.kafe.example";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://taiga.io/");

        public Uri ApiDocumentationUrl { get; } = new("https://docs.taiga.io/api.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Text, Required: required, Description: description);

        private static ConnectorConfigField Number(string name, string label, string description, bool required = false) => new(name, label, ConnectorConfigFieldType.Integer, Required: required, Description: description);

        private static readonly ConnectorConfigField ProjectField = Number("project", "Project", "Its number - from Taiga's projects, 2.", required: true);

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] Writable(Kind kind, bool create)
        {
            var fields = new List<ConnectorConfigField>();
            if (create)
            {
                fields.Add(ProjectField);
            }

            fields.Add(Field("subject", "Subject", "Kaffet tog slut.", required: create));
            fields.Add(new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown. Over several lines from the step's Query box."));
            fields.Add(Field("assigned_to", "Assignee", "A member's username or number."));
            fields.Add(Field("status", "Status", "Its name in the project - In progress - or its number."));
            fields.Add(Field("tags", "Tags", "By commas - kaffe, kassa."));
            fields.Add(new ConnectorConfigField("is_blocked", "Blocked", ConnectorConfigFieldType.Boolean, Required: false, Description: "Blocked or not."));
            fields.Add(Field("blocked_note", "Blocked note", "Why."));
            foreach (var extra in kind.Extra)
            {
                fields.Add(extra switch
                {
                    "color" => Field("color", "Colour", "#0000FF."),
                    "priority" or "severity" or "type" => Field(extra, extra[..1].ToUpperInvariant() + extra[1..], "Its name in the project - High - or its number."),
                    "user_story" => Number("user_story", "User story", "Its number."),
                    "milestone" => Number("milestone", "Sprint", "Its number."),
                    _ => Number(extra, extra.Replace('_', ' '), "A position, 1 or more."),
                });
            }

            return fields.ToArray();
        }

        public IReadOnlyList<ConnectorOperation> Operations { get; } = BuildOperations();

        private static IReadOnlyList<ConnectorOperation> BuildOperations()
        {
            var operations = new List<ConnectorOperation> { new("projects", "Get projects", "The projects the connection's user is a member of.") { Parameters = new(new[] { MaxField }) } };
            foreach (var kind in Kinds)
            {
                var label = kind.Name.Replace('_', ' ');
                var id = Number(kind.Name, char.ToUpperInvariant(label[0]) + label[1..], "Its number - from its address in Taiga.", required: true);
                var filters = new List<ConnectorConfigField>
                {
                    ProjectField, Field("assigned_to", "Assignee", "A member's username or number."), Field("status", "Status", "Its name or number."),
                    Field("tags", "Tags", "By commas: those with every one."), new("closed", "Closed", ConnectorConfigFieldType.Boolean, Required: false, Description: "Closed ones only, or open ones only."),
                };
                filters.AddRange(kind.Filters.Select(filter => filter switch
                {
                    "order_by" => Field("order_by", "Order by", string.Join(", ", Orders) + "; a minus first for newest first."),
                    "archived" => new ConnectorConfigField("archived", "Archived", ConnectorConfigFieldType.Boolean, Required: false, Description: "Archived ones only, or not."),
                    "user_story" or "epic" or "milestone" => Number(filter, filter.Replace('_', ' '), "Its number."),
                    _ => Field(filter, filter, "Its name or number."),
                }));
                filters.Add(MaxField);
                operations.Add(new ConnectorOperation(kind.Plural, $"Get {label.Replace("story", "stories")}", $"n8n's {label} - Get Many.") { Parameters = new(filters.ToArray()) });
                operations.Add(new ConnectorOperation(kind.Name, $"Get a {label}", $"n8n's {label} - Get.") { Parameters = new(new[] { id }) });
                operations.Add(new ConnectorOperation(kind.Name + "_create", $"Create a {label}", $"n8n's {label} - Create.") { Parameters = new(Writable(kind, create: true)) });
                operations.Add(new ConnectorOperation(kind.Name + "_update", $"Update a {label}", $"n8n's {label} - Update; the version Taiga requires is read first.") { Parameters = new(new[] { id }.Concat(Writable(kind, create: false)).ToArray()) });
                operations.Add(new ConnectorOperation(kind.Name + "_delete", $"Delete a {label}", $"n8n's {label} - Delete.") { Parameters = new(new[] { id }) });
            }

            return operations;
        }

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                return ConnectionTestResult.Ok($"Taiga answers: signed in as {session.Username}.");
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
            var name = (context.OperationName ?? "issues").Trim().ToLowerInvariant();
            var p = context.Parameters;
            var max = Count(p, "max", DefaultMax);

            if (name == "projects")
            {
                using var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken);
                await foreach (var member in session.ListAsync($"/projects?member={session.UserId}", max, cancellationToken))
                {
                    yield return Record(member);
                }

                yield break;
            }

            var kind = Kinds.FirstOrDefault(known => name == known.Plural || name == known.Name || name.StartsWith(known.Name + "_", StringComparison.Ordinal));
            var verb = kind is null ? null : name == kind.Plural ? "list" : name == kind.Name ? "get" : name[(kind.Name.Length + 1)..];
            if (kind is null || verb is not ("list" or "get" or "create" or "update" or "delete"))
            {
                throw new InvalidOperationException($"Taiga has no operation '{context.OperationName}' - projects, or for each of epic, issue, task and user_story: its list (epics), get (epic), _create, _update and _delete.");
            }

            // Everything held to Taiga's shape before anything is sent.
            var id = verb is "get" or "update" or "delete" ? Id(p, kind.Name, true)!.Value : 0;
            var changes = verb is "create" or "update" ? Changes(kind, p, verb == "create") : null;
            var filters = verb == "list" ? Filters(kind, p) : null;
            var project = verb is "list" or "create" ? Id(p, "project", true)!.Value : 0;

            using (var session = await Session.OpenAsync(context.Endpoint, context.Credentials, context.Egress, context.Timeout, cancellationToken))
            {
                switch (verb)
                {
                    case "list":
                        {
                            // Taiga answers a list for a project it does not have with nothing at all: the project is read first.
                            await session.SendAsync(HttpMethod.Get, $"/projects/{project}", null, cancellationToken);
                            var query = new List<string> { "project=" + project };
                            foreach (var (key, value) in filters!)
                            {
                                query.Add(key + "=" + Uri.EscapeDataString(await ResolveAsync(session, kind, project, key, value, cancellationToken)));
                            }

                            await foreach (var item in session.ListAsync(kind.Route + "?" + string.Join("&", query), max, cancellationToken))
                            {
                                yield return Record(item);
                            }

                            break;
                        }

                    case "get":
                        yield return Record(await session.SendAsync(HttpMethod.Get, $"{kind.Route}/{id}", null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Taiga answered without the item - check the address."));
                        break;
                    case "create":
                        {
                            var body = new JsonObject { ["project"] = project };
                            foreach (var (key, value) in changes!)
                            {
                                body[key] = await ValueAsync(session, kind, project, key, value, cancellationToken);
                            }

                            yield return Record(await session.SendAsync(HttpMethod.Post, kind.Route, body, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Taiga answered without what it made - check the address."));
                            break;
                        }

                    case "update":
                        {
                            // Taiga refuses a change without the item's current version: read it, and its project for the names.
                            var current = await session.SendAsync(HttpMethod.Get, $"{kind.Route}/{id}", null, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Taiga answered without the item - check the address.");
                            var inProject = current["project"]?.GetValue<long>() ?? 0;
                            var body = new JsonObject();
                            foreach (var (key, value) in changes!)
                            {
                                body[key] = await ValueAsync(session, kind, inProject, key, value, cancellationToken);
                            }

                            body["version"] = current["version"]?.DeepClone();
                            yield return Record(await session.SendAsync(HttpMethod.Patch, $"{kind.Route}/{id}", body, cancellationToken) as JsonObject ?? throw new InvalidOperationException("Taiga answered without the item - check the address."));
                            break;
                        }

                    default:
                        await session.SendAsync(HttpMethod.Delete, $"{kind.Route}/{id}", null, cancellationToken);
                        yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = (long)id, ["done"] = kind.Name + "_delete" });
                        break;
                }
            }
        }

        // The fields a create or an update sends, by Taiga's names, as given - names resolved once the project is known.
        private static List<(string Key, string Value)> Changes(Kind kind, IReadOnlyDictionary<string, string> p, bool create)
        {
            var changes = new List<(string, string)>();
            if (Values.Get(p, "subject") is { } subject)
            {
                changes.Add(("subject", subject.Length <= 500 && !subject.Any(char.IsControl) ? subject : throw new InvalidOperationException("A subject is one line of at most 500 characters (subject).")));
            }
            else if (create)
            {
                throw new InvalidOperationException($"Give the {kind.Name.Replace('_', ' ')}'s subject (subject).");
            }

            if ((Values.Get(p, "description") ?? Values.Get(p, "query")) is { } description)
            {
                changes.Add(("description", description.Length <= MostText ? description : throw new InvalidOperationException("A description is at most a megabyte (description).")));
            }

            foreach (var name in new[] { "assigned_to", "status", "tags", "is_blocked", "blocked_note" }.Concat(kind.Extra))
            {
                if (Values.Get(p, name) is not { } given)
                {
                    continue;
                }

                changes.Add((name, name switch
                {
                    "tags" => string.Join(",", Tags(given)),
                    "is_blocked" => bool.TryParse(given, out var flag) ? flag ? "true" : "false" : throw new InvalidOperationException($"'{given}' is not true or false (is_blocked)."),
                    "blocked_note" => given.Length <= 1000 ? given : throw new InvalidOperationException("A blocked note is at most 1000 characters (blocked_note)."),
                    "color" => ColourPattern.IsMatch(given) ? "#" + given.TrimStart('#').ToUpperInvariant() : throw new InvalidOperationException($"'{given}' is not a colour - #0000FF (color)."),
                    "user_story" or "milestone" => Id(p, name, false)!.Value.ToString(CultureInfo.InvariantCulture),
                    "us_order" or "taskboard_order" or "backlog_order" or "kanban_order" or "sprint_order" => Count(p, name, 1).ToString(CultureInfo.InvariantCulture),
                    _ => Lookup(given, name),
                }));
            }

            return changes.Count > 0 || create ? changes : throw new InvalidOperationException($"Give something to change on the {kind.Name.Replace('_', ' ')} - a subject, a description, a status, an assignee, tags.");
        }

        private static List<(string Key, string Value)> Filters(Kind kind, IReadOnlyDictionary<string, string> p)
        {
            var filters = new List<(string, string)>();
            void Add(string name, string key, Func<string, string> check)
            {
                if (Values.Get(p, name) is { } given)
                {
                    filters.Add((key, check(given)));
                }
            }

            Add("assigned_to", "assigned_to", given => Lookup(given, "assigned_to"));
            Add("status", "status", given => Lookup(given, "status"));
            Add("tags", "tags", given => string.Join(",", Tags(given)));
            Add("closed", "status__is_closed", given => Flag(given, "closed")); // n8n's statusIsClosed: a name Taiga ignores
            foreach (var filter in kind.Filters)
            {
                switch (filter)
                {
                    case "order_by":
                        Add("order_by", "order_by", given => Orders.Contains(given.TrimStart('-')) ? given : throw new InvalidOperationException($"'{given}' is not an order - {string.Join(", ", Orders)}; a minus first for newest first (order_by)."));
                        break;
                    case "archived":
                        Add("archived", "status__is_archived", given => Flag(given, "archived"));
                        break;
                    case "user_story" or "epic" or "milestone":
                        Add(filter, filter, _ => Id(p, filter, false)!.Value.ToString(CultureInfo.InvariantCulture)); // n8n's userStory: a name Taiga ignores
                        break;
                    default:
                        Add(filter, filter, given => Lookup(given, filter));
                        break;
                }
            }

            return filters;
        }

        // A name in the project (status, priority, severity, type, an assignee's username) resolved to its number.
        private static async Task<string> ResolveAsync(Session session, Kind kind, long project, string key, string value, CancellationToken cancellationToken)
        {
            if (key is not ("status" or "priority" or "severity" or "type" or "assigned_to") || long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return value;
            }

            var route = key switch
            {
                "status" => kind.Statuses,
                "priority" => "/priorities",
                "severity" => "/severities",
                "type" => "/issue-types",
                _ => "/users",
            };
            var field = key == "assigned_to" ? "username" : "name";
            var found = new List<JsonObject>();
            await foreach (var item in session.ListAsync($"{route}?project={project}", int.MaxValue, cancellationToken))
            {
                found.Add(item);
            }

            var match = found.FirstOrDefault(item => string.Equals(Text(item[field]), value, StringComparison.OrdinalIgnoreCase));
            return match?["id"]?.ToString()
                ?? throw new InvalidOperationException($"The project has no {key.Replace('_', ' ')} '{value}' - {(found.Count == 0 ? "it has none" : "it has " + string.Join(", ", found.Select(item => Text(item[field]))))} ({key}).");
        }

        private static async Task<JsonNode?> ValueAsync(Session session, Kind kind, long project, string key, string value, CancellationToken cancellationToken) => key switch
        {
            "tags" => new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()),
            "is_blocked" => JsonValue.Create(value == "true"),
            "subject" or "description" or "blocked_note" or "color" => JsonValue.Create(value),
            _ => JsonValue.Create(long.Parse(await ResolveAsync(session, kind, project, key, value, cancellationToken), CultureInfo.InvariantCulture)),
        };

        private static string Lookup(string given, string name) =>
            given.Length <= 200 && !given.Any(char.IsControl) ? given.Trim() : throw new InvalidOperationException($"'{(given.Length > 40 ? given[..40] + "..." : given)}' is not a {name.Replace('_', ' ')}'s name or number ({name}).");

        private static string[] Tags(string given)
        {
            var tags = given.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToArray();
            return tags.All(tag => tag.Length <= 100 && !tag.Any(char.IsControl)) ? tags : throw new InvalidOperationException("A tag is one line of at most 100 characters (tags).");
        }

        private static string Flag(string given, string name) =>
            bool.TryParse(given, out var flag) ? flag ? "true" : "false" : throw new InvalidOperationException($"'{given}' is not true or false ({name}).");

        private static long? Id(IReadOnlyDictionary<string, string> p, string name, bool required) =>
            Values.Get(p, name) is { } given
                ? long.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new InvalidOperationException($"'{given}' is not a {name.Replace('_', ' ')}'s number - 42 ({name}).")
                : required ? throw new InvalidOperationException($"Give the {name.Replace('_', ' ')} - its number, 42 ({name}).") : null;

        private static int Count(IReadOnlyDictionary<string, string> p, string name, int fallback) =>
            Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{given}' is not a number above 0 ({name}).")
                : fallback;

        // An item as a record: its plain values as they are, a list or an object as JSON; its tags by name; its status's name beside it.
        private static ConnectorRecord Record(JsonObject data)
        {
            var fields = data.ToDictionary(pair => pair.Key, pair => pair.Key == "tags" && pair.Value is JsonArray tags
                ? (object?)new JsonArray(tags.Select(tag => (JsonNode?)JsonValue.Create(tag is JsonArray pairing ? Text(pairing.FirstOrDefault()) : Text(tag))).ToArray()).ToJsonString(Wire)
                : Value(pair.Value), StringComparer.Ordinal);
            if (Text(data["status_extra_info"]?["name"]) is { } status)
            {
                fields["status_name"] = status;
            }

            return new ConnectorRecord(fields);
        }

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole,
            JsonValue value when value.TryGetValue<double>(out var number) => number,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private sealed class Session : IDisposable
        {
            private readonly HttpClient client;
            private readonly string api;
            private readonly string token;

            private Session(HttpClient client, string api, string token, long userId, string username)
            {
                this.client = client;
                this.api = api;
                this.token = token;
                UserId = userId;
                Username = username;
            }

            public long UserId { get; }

            public string Username { get; }

            public static async Task<Session> OpenAsync(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("Give Taiga's address - https://taiga.kafe.example.");
                }

                var root = Values.BaseUrl(endpoint, endpoint);
                foreach (var suffix in new[] { "/api/v1", "/api" })
                {
                    if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        root = root[..^suffix.Length]; // the API's own address given: Taiga's
                        break;
                    }
                }

                var uri = new Uri(root);
                if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException($"'{endpoint}' is not Taiga's address - https://taiga.kafe.example, with no query.");
                }

                var user = Values.Get(credentials, "username") ?? throw new InvalidOperationException("The Taiga connection has no username.");
                var password = Values.Get(credentials, "password") ?? throw new InvalidOperationException("The Taiga connection has no password.");
                var client = egress.CreateHttpClient(timeout);
                try
                {
                    // n8n's sign-in: the user's name or e-mail and password for a token, sent as a bearer token on every call.
                    var body = new JsonObject { ["type"] = "normal", ["username"] = user, ["password"] = password }.ToJsonString(Wire);
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/auth") { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
                    var answer = Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Taiga refused the sign-in (HTTP {(int)response.StatusCode}){Said(answer)} - check the username and the password.");
                    }

                    return Text(answer?["auth_token"]) is { Length: > 0 } token && answer?["id"] is JsonValue id && id.TryGetValue<long>(out var userId)
                        ? new Session(client, root + "/api/v1", token, userId, Text(answer["username"]) ?? user)
                        : throw new InvalidOperationException("Taiga signed in without a token - check the address: a sign-in page or a proxy answers so.");
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            public void Dispose() => client.Dispose();

            private HttpRequestMessage Request(HttpMethod method, string route, string? payload)
            {
                var request = new HttpRequestMessage(method, api + route);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.Accept.ParseAdd("application/json");
                if (payload is not null)
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                return request;
            }

            /// <summary>A call with the token; Taiga's JSON (null for its 204), or its status, message and the fields it refused.</summary>
            public async Task<JsonNode?> SendAsync(HttpMethod method, string route, JsonObject? body, CancellationToken cancellationToken)
            {
                var payload = body?.ToJsonString(Wire);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(method, route, payload), cancellationToken);
                return await ReadAsync(response, method, cancellationToken);
            }

            /// <summary>A list, page by page, up to max.</summary>
            public async IAsyncEnumerable<JsonObject> ListAsync(string route, int max, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                // One page size throughout: a page of another size starts elsewhere. Past the last page Taiga answers 404, so its
                // next-page header decides.
                var read = 0;
                var size = Math.Min(PageSize, max);
                for (var page = 1; read < max; page++)
                {
                    using var response = await RetryAfterPolicy.Default.SendAsync(client, () => Request(HttpMethod.Get, $"{route}{(route.Contains('?') ? "&" : "?")}page={page}&page_size={size}", null), cancellationToken);
                    var items = await ReadAsync(response, HttpMethod.Get, cancellationToken) as JsonArray
                        ?? throw new InvalidOperationException("Taiga answered with what is not a list - check the address: a sign-in page or a proxy answers so.");
                    foreach (var item in items.OfType<JsonObject>().Take(max - read))
                    {
                        yield return item;
                        read++;
                    }

                    if (items.Count < size || !response.Headers.TryGetValues("x-pagination-next", out var next) || string.IsNullOrEmpty(next.FirstOrDefault()))
                    {
                        yield break;
                    }
                }
            }

            private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, HttpMethod method, CancellationToken cancellationToken)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = Parse(text);
                if (response.IsSuccessStatusCode)
                {
                    return json is null && text.Trim().Length > 0
                        ? throw new InvalidOperationException("Taiga answered with what is not JSON - check the address: a sign-in page or a proxy answers so.")
                        : json;
                }

                var status = (int)response.StatusCode;
                throw new InvalidOperationException($"Taiga answered HTTP {status}{Said(json)}"
                    + (status == 401 ? " - check the username and the password."
                        : status == 404 && Said(json) == "." ? " - it is not there, or the user may not see it." // Taiga's own message is empty for a project

                        : status == 403 && method == HttpMethod.Delete ? " - Taiga says so too of an item that is not there."
                        : status == 403 ? " - the user may not do this in that project." : string.Empty));
            }

            private static JsonNode? Parse(string answer)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            // Taiga's message: its _error_message, its detail, or the fields it refused ({"subject": ["This field is required."]}).
            private static string Said(JsonNode? json)
            {
                var words = Text(json?["_error_message"]) is { Length: > 0 } error ? error
                    : Text(json?["detail"]) is { Length: > 0 } detail ? detail
                    : json is JsonObject fields ? string.Join("; ", fields.Where(pair => !pair.Key.StartsWith('_')).Select(pair => pair.Key + ": " + (pair.Value is JsonArray list ? string.Join(" ", list.Select(item => Text(item))) : Text(pair.Value))))
                    : string.Empty;
                words = words.Length > 300 ? words[..300] : words;
                return words.Length == 0 ? "." : ": " + words + (words.EndsWith('.') ? string.Empty : ".");
            }
        }
    }
}
