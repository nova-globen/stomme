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

namespace Stomme.Modules.Connectors.Asana
{
    /// <summary>
    /// Asana (n8n's Asana node: a task created, got, listed, updated, moved and deleted, a comment added): Asana's REST API
    /// (<c>https://app.asana.com/api/1.0</c>), a personal access token as the bearer. A test asks Asana who the token is
    /// (<c>users/me</c>).
    /// </summary>
    /// <remarks>
    /// A task, a project, a section and a workspace are named by their gid (digits), a task's or a project's address cut down to
    /// it, and held to that shape before they reach the path. <c>tasks</c>: a project's tasks - only the open ones unless
    /// <c>completed</c> says otherwise, modified since a time when given - page by page by Asana's offset up to <c>max</c>; one
    /// record each: gid, name, notes, completed, completed_at, due_on, due_at, assignee (a name), projects and tags (names,
    /// comma-separated), section, url, created_at, modified_at. <c>get_task</c> the same for one. <c>create_task</c>: a task in a
    /// project (or a workspace), its name, notes, assignee (me, an address or a gid) and due date (a day, or a time);
    /// <c>update_task</c> changes those given or completes it; <c>move_task</c> puts it in a section; <c>delete_task</c> removes
    /// it; <c>comment</c> adds a comment. A throttled call waits as Asana says; a failure is thrown with its status and Asana's
    /// first error.
    /// </remarks>
    public sealed class AsanaConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://app.asana.com/api/1.0";

        public const string TasksOperation = "tasks";

        public const string GetTaskOperation = "get_task";

        public const string CreateTaskOperation = "create_task";

        public const string UpdateTaskOperation = "update_task";

        public const string MoveTaskOperation = "move_task";

        public const string DeleteTaskOperation = "delete_task";

        public const string CommentOperation = "comment";

        public const int DefaultMax = 1000;

        public const string TaskFields = "name,notes,completed,completed_at,due_on,due_at,assignee.name,projects.name,tags.name,memberships.section.name,permalink_url,created_at,modified_at";

        private static readonly Regex Gid = new("^[0-9]{1,30}$", RegexOptions.CultureInvariant);

        private static readonly Regex Day = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "asana";

        public string DisplayName => "Asana";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The bearer as Asana names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "Personal access token" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://asana.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.asana.com/reference/rest-api-reference");

        private static readonly ConnectorConfigField TaskField = new("task", "Task", ConnectorConfigFieldType.Text, Required: true, Description: "The task's gid, or its address.");

        private static ConnectorConfigField[] TaskFieldsFor(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("notes", "Notes", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text."),
            new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "me, an address, or a user's gid."),
            new ConnectorConfigField("due", "Due", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-12, or 2026-10-12T17:00:00+02:00."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TasksOperation, "Get tasks", "A project's tasks - one record each: its gid, name, notes, due date, assignee, section and page - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: true, Description: "The project's gid, or its address."),
                    new ConnectorConfigField("completed", "Completed too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Completed tasks as well as open ones.") { Default = "false" },
                    new ConnectorConfigField("modified_since", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "Only tasks changed since then - 2026-10-01, or a time."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many tasks, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetTaskOperation, "Get a task", "One task - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(CreateTaskOperation, "Create a task", "Adds a task to a project - or a workspace - one record: the task - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project's gid. Or name a workspace."),
                    new ConnectorConfigField("workspace", "Workspace", ConnectorConfigFieldType.Text, Required: false, Description: "The workspace's gid, for a task in no project."),
                }.Concat(TaskFieldsFor(create: true)).ToArray()),
            },
            new ConnectorOperation(UpdateTaskOperation, "Update a task", "Changes a task - its name, notes, assignee or due date - or completes it - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }.Concat(TaskFieldsFor(create: false)).Append(
                    new ConnectorConfigField("completed", "Completed", ConnectorConfigFieldType.Boolean, Required: false, Description: "Done, or open again.")).ToArray()),
            },
            new ConnectorOperation(MoveTaskOperation, "Move a task", "Puts a task in a section of its project - n8n's Move.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField, new ConnectorConfigField("section", "Section", ConnectorConfigFieldType.Text, Required: true, Description: "The section's gid.") }),
            },
            new ConnectorOperation(DeleteTaskOperation, "Delete a task", "Removes a task - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(CommentOperation, "Comment on a task", "Adds a comment to a task - one record: its gid - n8n's task comment Add.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField, new ConnectorConfigField("text", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Text.") }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var (me, _) = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/users/me?opt_fields=name,email", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {Text(me?["name"]) ?? Text(me?["email"]) ?? "the token's user"}.");
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
            var operation = context.OperationName ?? TasksOperation;
            var parameters = context.Parameters;
            using var client = context.Egress.CreateHttpClient(context.Timeout);

            if (Is(operation, TasksOperation))
            {
                var project = Id(Values.Get(parameters, "project"), "project");
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var query = new List<string> { "opt_fields=" + TaskFields };
                if (!Values.GetBool(parameters, "completed", false))
                {
                    query.Add("completed_since=now"); // Asana's own way of saying "only the open ones"
                }

                if (Values.Get(parameters, "modified_since") is { } since)
                {
                    query.Add("modified_since=" + Uri.EscapeDataString(Instant(since)));
                }

                var read = 0;
                string? offset = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = $"{api}/projects/{project}/tasks?{string.Join("&", query)}&limit={Math.Min(100, max - read).ToString(CultureInfo.InvariantCulture)}{(offset is null ? "" : "&offset=" + Uri.EscapeDataString(offset))}";
                    var (page, next) = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var task in (page as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (read >= max)
                        {
                            yield break;
                        }

                        read++;
                        yield return TaskRecord(task);
                    }

                    offset = read < max ? next : null;
                }
                while (!string.IsNullOrEmpty(offset));

                yield break;
            }

            if (Is(operation, CreateTaskOperation))
            {
                var data = TaskBody(parameters, create: true);
                if (Values.Get(parameters, "project") is { } project)
                {
                    data["projects"] = new JsonArray(Id(project, "project"));
                }
                else if (Values.Get(parameters, "workspace") is { } workspace)
                {
                    data["workspace"] = Id(workspace, "workspace");
                }
                else
                {
                    throw new InvalidOperationException("Name the project - or the workspace - the task goes in.");
                }

                var (made, _) = await SendAsync(client, token, HttpMethod.Post, $"{api}/tasks?opt_fields={TaskFields}", new JsonObject { ["data"] = data }, cancellationToken);
                yield return TaskRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            var taskId = Id(Values.Get(parameters, "task"), "task");
            if (Is(operation, GetTaskOperation))
            {
                var (found, _) = await SendAsync(client, token, HttpMethod.Get, $"{api}/tasks/{taskId}?opt_fields={TaskFields}", null, cancellationToken);
                yield return TaskRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateTaskOperation))
            {
                var data = TaskBody(parameters, create: false);
                if (Values.Get(parameters, "completed") is { } completed)
                {
                    data["completed"] = completed.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : completed.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                        : throw new InvalidOperationException($"'{completed}' is not true or false (completed).");
                }

                if (data.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - name, notes, assignee, due or completed.");
                }

                var (changed, _) = await SendAsync(client, token, HttpMethod.Put, $"{api}/tasks/{taskId}?opt_fields={TaskFields}", new JsonObject { ["data"] = data }, cancellationToken);
                yield return TaskRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, MoveTaskOperation))
            {
                var section = Id(Values.Get(parameters, "section"), "section");
                await SendAsync(client, token, HttpMethod.Post, $"{api}/sections/{section}/addTask", new JsonObject { ["data"] = new JsonObject { ["task"] = taskId } }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["gid"] = taskId, ["section"] = section });
                yield break;
            }

            if (Is(operation, DeleteTaskOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, $"{api}/tasks/{taskId}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["gid"] = taskId, ["deleted"] = true });
                yield break;
            }

            if (Is(operation, CommentOperation))
            {
                var text = parameters.TryGetValue("text", out var given) && !string.IsNullOrWhiteSpace(given) ? given : throw new InvalidOperationException("Give the comment's text.");
                var (story, _) = await SendAsync(client, token, HttpMethod.Post, $"{api}/tasks/{taskId}/stories", new JsonObject { ["data"] = new JsonObject { ["text"] = text } }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["task"] = taskId, ["comment_gid"] = Text(story?["gid"]) });
                yield break;
            }

            throw new InvalidOperationException($"Asana has no operation '{operation}' - tasks, get_task, create_task, update_task, move_task, delete_task or comment.");
        }

        // A task's fields as Asana's: its name, notes, assignee and due date (a day as due_on, a time as due_at), as given.
        private static JsonObject TaskBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var data = new JsonObject();
            if (Values.Get(parameters, "name") is { } name)
            {
                data["name"] = name.Contains('\n', StringComparison.Ordinal) || name.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The name has a line break - one line only.") : name;
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the task's name.");
            }

            if (parameters.TryGetValue("notes", out var notes) && !string.IsNullOrWhiteSpace(notes))
            {
                data["notes"] = notes;
            }

            if (Values.Get(parameters, "assignee") is { } assignee)
            {
                data["assignee"] = assignee == "me" || Gid.IsMatch(assignee) || assignee.Contains('@', StringComparison.Ordinal) && !assignee.Any(char.IsWhiteSpace)
                    ? assignee
                    : throw new InvalidOperationException($"'{assignee}' is not an assignee - me, an address, or a user's gid.");
            }

            if (Values.Get(parameters, "due") is { } due)
            {
                if (Day.IsMatch(due))
                {
                    data["due_on"] = DateTime.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? due : throw new InvalidOperationException($"'{due}' is not a date.");
                }
                else
                {
                    data["due_at"] = Instant(due);
                }
            }

            return data;
        }

        // A task as a record - a person, a project, a tag and a section by name.
        private static ConnectorRecord TaskRecord(JsonObject task) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gid"] = Text(task["gid"]),
            ["name"] = Text(task["name"]) ?? "",
            ["notes"] = Text(task["notes"]) ?? "",
            ["completed"] = task["completed"] is JsonValue completed && completed.TryGetValue<bool>(out var done) && done,
            ["completed_at"] = Text(task["completed_at"]),
            ["due_on"] = Text(task["due_on"]),
            ["due_at"] = Text(task["due_at"]),
            ["assignee"] = Text(task["assignee"]?["name"]),
            ["projects"] = Names(task["projects"]),
            ["tags"] = Names(task["tags"]),
            ["section"] = (task["memberships"] as JsonArray ?? new JsonArray()).Select(membership => Text(membership?["section"]?["name"])).FirstOrDefault(name => !string.IsNullOrEmpty(name)),
            ["url"] = Text(task["permalink_url"]),
            ["created_at"] = Text(task["created_at"]),
            ["modified_at"] = Text(task["modified_at"]),
        });

        private static string Names(JsonNode? list) =>
            string.Join(", ", (list as JsonArray ?? new JsonArray()).Select(item => Text(item?["name"])).Where(name => !string.IsNullOrEmpty(name)));

        // A moment in UTC, as Asana takes it.
        private static string Instant(string given) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time - 2026-10-12, or 2026-10-12T17:00:00+02:00.");

        // A gid, a task's or a project's address cut down to it (app.asana.com/0/{project}/{task}, or /1/.../project/{p}/task/{t}).
        private static string Id(string? given, string what)
        {
            var value = given?.Trim() ?? throw new InvalidOperationException($"Name the {what}.");
            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(value, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var segments = address.AbsolutePath.Trim('/').Split('/').Where(segment => segment != "f").ToList();
                var named = segments.IndexOf(what);
                value = named >= 0 && named + 1 < segments.Count ? segments[named + 1]
                    : what == "task" && segments.Count >= 3 && segments[0] == "0" ? segments[2]
                    : what == "project" && segments.Count >= 2 && segments[0] == "0" ? segments[1]
                    : value;
            }

            return Gid.IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not an Asana {what} - its gid{(what is "task" or "project" ? " or address" : "")}.");
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The Asana connection has no token - a personal access token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Asana's answer's data and its next page's offset; a throttled call waits as Asana says; a failure thrown with its status and
        // Asana's first error.
        private static async Task<(JsonNode? Data, string? Next)> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
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
            JsonObject? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload) as JsonObject;
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Asana answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Text((json?["errors"] as JsonArray)?.FirstOrDefault()?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return (json?["data"], Text(json?["next_page"]?["offset"]));
        }
    }
}
