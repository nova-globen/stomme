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

namespace Stomme.Modules.Connectors.ClickUp
{
    /// <summary>
    /// ClickUp (BL-229, n8n's ClickUp node: a task created, got, listed, updated and deleted, a comment added): ClickUp's API
    /// (<c>https://api.clickup.com/api/v2</c>), a personal API token (<c>pk_...</c>) as the API key - sent as ClickUp has it, the
    /// <c>Authorization</c> header's whole value, no scheme before it. A test asks ClickUp who the token is (<c>user</c>).
    /// </summary>
    /// <remarks>
    /// A list is named by its id (digits) and a task by its id (letters and digits), each held to that shape - a task's address
    /// cut down to its id - before it reaches the path. <c>tasks</c>: a list's tasks - the open ones unless <c>closed</c> says
    /// every one, those with the <c>statuses</c> and <c>tags</c> given - page by page (ClickUp's 100 a page) up to <c>max</c>; one
    /// record each: id, name, description, status, priority, assignees and tags (names, comma-separated), due (a time, in UTC),
    /// url, list, created and updated. <c>get_task</c> the same for one. <c>create_task</c>: a task on a list, its name,
    /// description (Markdown), status, priority (urgent, high, normal, low, or 1-4), due date (a day or a time), assignees (user
    /// ids) and tags; <c>update_task</c> changes those given, adding and removing assignees; <c>delete_task</c> removes it;
    /// <c>comment</c> adds a comment. A throttled call waits as ClickUp says; a failure is thrown with its status and ClickUp's
    /// <c>err</c>.
    /// </remarks>
    public sealed class ClickUpConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.clickup.com/api/v2";

        public const string TasksOperation = "tasks";

        public const string GetTaskOperation = "get_task";

        public const string CreateTaskOperation = "create_task";

        public const string UpdateTaskOperation = "update_task";

        public const string DeleteTaskOperation = "delete_task";

        public const string CommentOperation = "comment";

        public const int DefaultMax = 1000;

        private static readonly Regex ListId = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex TaskId = new("^[A-Za-z0-9]{1,30}$", RegexOptions.CultureInvariant);

        private static readonly Regex UserId = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);

        private static readonly Regex Day = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

        public string ConnectorType => "clickup";

        public string DisplayName => "ClickUp";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        /// <summary>The API key as ClickUp names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "API token (pk_...)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://clickup.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.clickup.com/reference");

        private static readonly ConnectorConfigField TaskField = new("task", "Task", ConnectorConfigFieldType.Text, Required: true, Description: "The task's id, or its address.");

        private static ConnectorConfigField[] TaskFields(bool create) => new[]
        {
            new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Markdown."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "One of the list's statuses - to do, in progress..."),
            new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Text, Required: false, Description: "urgent, high, normal or low (or 1-4)."),
            new ConnectorConfigField("due", "Due", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-12, or 2026-10-12T17:00:00+02:00."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TasksOperation, "Get tasks", "A list's tasks - one record each: its id, name, status, priority, assignees, due date and page - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id (digits)."),
                    new ConnectorConfigField("closed", "Closed too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Closed tasks as well as open ones.") { Default = "false" },
                    new ConnectorConfigField("statuses", "Statuses", ConnectorConfigFieldType.Text, Required: false, Description: "Only these, comma-separated."),
                    new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Only these, comma-separated."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many tasks, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetTaskOperation, "Get a task", "One task - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(CreateTaskOperation, "Create a task", "Adds a task to a list - one record: the task - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id (digits).") }
                    .Concat(TaskFields(create: true))
                    .Concat(new[]
                    {
                        new ConnectorConfigField("assignees", "Assignees", ConnectorConfigFieldType.Text, Required: false, Description: "User ids, comma-separated."),
                        new ConnectorConfigField("tags", "Tags", ConnectorConfigFieldType.Text, Required: false, Description: "Tag names, comma-separated."),
                    }).ToArray()),
            },
            new ConnectorOperation(UpdateTaskOperation, "Update a task", "Changes a task - its name, description, status, priority or due date - and adds or removes assignees - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }.Concat(TaskFields(create: false)).Concat(new[]
                {
                    new ConnectorConfigField("add_assignees", "Add assignees", ConnectorConfigFieldType.Text, Required: false, Description: "User ids, comma-separated."),
                    new ConnectorConfigField("remove_assignees", "Remove assignees", ConnectorConfigFieldType.Text, Required: false, Description: "User ids, comma-separated."),
                }).ToArray()),
            },
            new ConnectorOperation(DeleteTaskOperation, "Delete a task", "Removes a task - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(CommentOperation, "Comment on a task", "Adds a comment to a task - one record: its id - n8n's comment Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TaskField,
                    new ConnectorConfigField("text", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Text."),
                    new ConnectorConfigField("notify_all", "Notify everyone", ConnectorConfigFieldType.Boolean, Required: false, Description: "Tell everyone on the task.") { Default = "false" },
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/user", null, cancellationToken);
                return ConnectionTestResult.Ok($"Signed in as {Text(me?["user"]?["username"]) ?? Text(me?["user"]?["email"]) ?? "the token's user"}.");
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
                var list = List(Values.Get(parameters, "list"));
                var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
                var query = new List<string>();
                if (Values.GetBool(parameters, "closed", false))
                {
                    query.Add("include_closed=true");
                }

                foreach (var (parameter, name) in new[] { ("statuses", "statuses[]"), ("tags", "tags[]") })
                {
                    foreach (var value in (Values.Get(parameters, parameter) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        query.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
                    }
                }

                var read = 0;
                for (var page = 0; ; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}/list/{list}/task?{string.Join("&", query.Prepend("page=" + page.ToString(CultureInfo.InvariantCulture)))}", null, cancellationToken);
                    var tasks = (answer?["tasks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var task in tasks)
                    {
                        if (read >= max)
                        {
                            yield break;
                        }

                        read++;
                        yield return TaskRecord(task);
                    }

                    if (tasks.Count == 0 || answer?["last_page"] is JsonValue last && last.TryGetValue<bool>(out var done) && done || read >= max)
                    {
                        yield break;
                    }
                }
            }

            if (Is(operation, CreateTaskOperation))
            {
                var list = List(Values.Get(parameters, "list"));
                var body = TaskBody(parameters, create: true);
                if (Values.Get(parameters, "assignees") is { } assignees)
                {
                    body["assignees"] = Users(assignees);
                }

                if (Values.Get(parameters, "tags") is { } tags)
                {
                    body["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
                }

                var made = await SendAsync(client, token, HttpMethod.Post, $"{api}/list/{list}/task", body, cancellationToken);
                yield return TaskRecord(made as JsonObject ?? new JsonObject());
                yield break;
            }

            var taskId = TaskOf(Values.Get(parameters, "task"));
            if (Is(operation, GetTaskOperation))
            {
                var found = await SendAsync(client, token, HttpMethod.Get, $"{api}/task/{taskId}", null, cancellationToken);
                yield return TaskRecord(found as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateTaskOperation))
            {
                var body = TaskBody(parameters, create: false);
                var add = Values.Get(parameters, "add_assignees");
                var remove = Values.Get(parameters, "remove_assignees");
                if (add is not null || remove is not null)
                {
                    body["assignees"] = new JsonObject { ["add"] = Users(add ?? ""), ["rem"] = Users(remove ?? "") };
                }

                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - name, description, status, priority, due or assignees.");
                }

                var changed = await SendAsync(client, token, HttpMethod.Put, $"{api}/task/{taskId}", body, cancellationToken);
                yield return TaskRecord(changed as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, DeleteTaskOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, $"{api}/task/{taskId}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = taskId, ["deleted"] = true });
                yield break;
            }

            if (Is(operation, CommentOperation))
            {
                var text = parameters.TryGetValue("text", out var given) && !string.IsNullOrWhiteSpace(given) ? given : throw new InvalidOperationException("Give the comment's text.");
                var comment = await SendAsync(client, token, HttpMethod.Post, $"{api}/task/{taskId}/comment", new JsonObject { ["comment_text"] = text, ["notify_all"] = Values.GetBool(parameters, "notify_all", false) }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["task"] = taskId, ["comment_id"] = Text(comment?["id"]) ?? comment?["id"]?.ToJsonString() });
                yield break;
            }

            throw new InvalidOperationException($"ClickUp has no operation '{operation}' - tasks, get_task, create_task, update_task, delete_task or comment.");
        }

        // A task's fields as ClickUp's: its name, Markdown description, status, priority (1 urgent - 4 low) and due date, as given.
        private static JsonObject TaskBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "name") is { } name)
            {
                body["name"] = name.Contains('\n', StringComparison.Ordinal) || name.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The name has a line break - one line only.") : name;
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the task's name.");
            }

            if (parameters.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description))
            {
                body["markdown_content"] = description;
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                body["status"] = status;
            }

            if (Values.Get(parameters, "priority") is { } priority)
            {
                body["priority"] = priority.ToLowerInvariant() switch
                {
                    "urgent" or "1" => 1,
                    "high" or "2" => 2,
                    "normal" or "3" => 3,
                    "low" or "4" => 4,
                    _ => throw new InvalidOperationException($"'{priority}' is not a priority - urgent, high, normal or low."),
                };
            }

            if (Values.Get(parameters, "due") is { } due)
            {
                var whole = Day.IsMatch(due);
                body["due_date"] = DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? moment.ToUnixTimeMilliseconds()
                    : throw new InvalidOperationException($"'{due}' is not a date - 2026-10-12, or 2026-10-12T17:00:00+02:00.");
                body["due_date_time"] = !whole; // a day is a day, a time a time
            }

            return body;
        }

        // A task as a record - a status, a priority, a person and a tag by name, its times in UTC.
        private static ConnectorRecord TaskRecord(JsonObject task) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(task["id"]),
            ["name"] = Text(task["name"]) ?? "",
            ["description"] = Text(task["text_content"]) ?? Text(task["description"]) ?? "",
            ["status"] = Text(task["status"]?["status"]),
            ["priority"] = Text(task["priority"]?["priority"]),
            ["assignees"] = string.Join(", ", (task["assignees"] as JsonArray ?? new JsonArray()).Select(person => Text(person?["username"]) ?? Text(person?["email"])).Where(name => !string.IsNullOrEmpty(name))),
            ["tags"] = string.Join(", ", (task["tags"] as JsonArray ?? new JsonArray()).Select(tag => Text(tag?["name"])).Where(name => !string.IsNullOrEmpty(name))),
            ["due"] = Moment(task["due_date"]),
            ["url"] = Text(task["url"]),
            ["list"] = Text(task["list"]?["name"]),
            ["created"] = Moment(task["date_created"]),
            ["updated"] = Moment(task["date_updated"]),
        });

        // ClickUp's milliseconds since 1970 (a string, or a number) as a time in UTC.
        private static string? Moment(JsonNode? node)
        {
            long? milliseconds = node is JsonValue value
                ? value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
                : value.TryGetValue<long>(out var number) ? number : null
                : null;
            return milliseconds is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;
        }

        private static JsonArray Users(string ids) =>
            new(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => UserId.IsMatch(id) ? (JsonNode?)JsonValue.Create(long.Parse(id, CultureInfo.InvariantCulture)) : throw new InvalidOperationException($"'{id}' is not a ClickUp user's id (digits)."))
                .ToArray());

        private static string List(string? given) =>
            given is not null && ListId.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a ClickUp list's id (digits).");

        // A task's id, its address (app.clickup.com/t/{id}) cut down to it.
        private static string TaskOf(string? given)
        {
            var value = given?.Trim() ?? throw new InvalidOperationException("Name the task.");
            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(value, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var segments = address.AbsolutePath.Trim('/').Split('/');
                value = segments.Length >= 2 && segments[^2] == "t" ? segments[^1] : value;
            }

            return TaskId.IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not a ClickUp task - its id or address.");
        }

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("api-key", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The ClickUp connection has no API token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // ClickUp's answer as JSON (an empty answer null), a throttled call waiting as ClickUp says; a failure thrown with its status
        // and ClickUp's err.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("Authorization", token); // ClickUp takes the token itself, no scheme before it
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                throw new InvalidOperationException($"ClickUp answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = json is JsonObject error ? Text(error["err"]) : null;
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
