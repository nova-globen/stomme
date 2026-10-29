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

namespace Stomme.Modules.Connectors.Todoist
{
    /// <summary>
    /// Todoist (n8n's Todoist node, its tasks: found, got, created, quick-added, updated, closed, reopened, moved and
    /// deleted, a comment added, the projects read): Todoist's API (<c>https://api.todoist.com/api/v1</c>), an API token as the
    /// bearer. A test names the token's user.
    /// </summary>
    /// <remarks>
    /// A task, a project, a section and a parent task are named by their id (letters, digits, - and _), a task's or a project's
    /// address cut down to it (app.todoist.com/app/task/buy-milk-6Jf8VQXxpwv56VQ7), each held to that shape before it reaches the
    /// path. <c>tasks</c>: a project's, a section's, a parent's, a label's or the ids given - or those a Todoist filter finds
    /// (today | overdue) - page by page by Todoist's cursor (200 a page, a page no bigger than what is left) up to <c>max</c>
    /// (1,000); one record each: its id, content, description, project, section, parent, labels, priority (4 urgent to 1 normal),
    /// due date, time and words, recurring or not, deadline, duration, assignee, done or not, times and address.
    /// <c>get_task</c> one. <c>create_task</c>: content of one line, a description, a project (the Inbox when none), a section, a
    /// parent, labels, a priority (its number or word), due words, a day or a time, an assignee, a duration and a deadline;
    /// <c>quick_add</c> takes a line as Todoist's own quick add reads it (#project @label p1 tomorrow); <c>update_task</c> changes
    /// those given, with no default (something, or refused); <c>close_task</c> completes one, <c>reopen_task</c> opens it again;
    /// <c>move_task</c> moves one to a project, a section or under a parent - one of them; <c>delete_task</c>;
    /// <c>add_comment</c> comments on one. <c>projects</c>: the projects, page by page. A throttled call waits as Todoist says; a
    /// failure is thrown with its status, Todoist's words and its tag.
    /// </remarks>
    public sealed class TodoistConnector : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://api.todoist.com/api/v1";

        public const string TasksOperation = "tasks";

        public const string GetTaskOperation = "get_task";

        public const string CreateTaskOperation = "create_task";

        public const string QuickAddOperation = "quick_add";

        public const string UpdateTaskOperation = "update_task";

        public const string CloseTaskOperation = "close_task";

        public const string ReopenTaskOperation = "reopen_task";

        public const string MoveTaskOperation = "move_task";

        public const string DeleteTaskOperation = "delete_task";

        public const string AddCommentOperation = "add_comment";

        public const string ProjectsOperation = "projects";

        public const int DefaultMax = 1000;

        private const int PageSize = 200;

        private static readonly Regex Key = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        private static readonly Regex Day = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

        // JSON as written: a description's words and accents as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { TasksOperation, GetTaskOperation, CreateTaskOperation, QuickAddOperation, UpdateTaskOperation, CloseTaskOperation, ReopenTaskOperation, MoveTaskOperation, DeleteTaskOperation, AddCommentOperation, ProjectsOperation };

        public string ConnectorType => "todoist";

        public string DisplayName => "Todoist";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        /// <summary>The token as Todoist names it.</summary>
        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "API token (Settings > Integrations > Developer)" };

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://todoist.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developer.todoist.com/api/v1/");

        private static readonly ConnectorConfigField TaskField = new("task", "Task", ConnectorConfigFieldType.Text, Required: true, Description: "The task's id, or its address.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigField[] TaskFieldsFor(bool create) => new[]
        {
            new ConnectorConfigField("content", "Content", ConnectorConfigFieldType.Text, Required: create, Description: "The task, one line."),
            new ConnectorConfigField("description", "Description", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text."),
            new ConnectorConfigField("labels", "Labels", ConnectorConfigFieldType.Text, Required: false, Description: "Names, comma-separated."),
            new ConnectorConfigField("priority", "Priority", ConnectorConfigFieldType.Text, Required: false, Description: "4 urgent, 3 high, 2 medium, 1 normal - the number or the word."),
            new ConnectorConfigField("due_string", "Due (words)", ConnectorConfigFieldType.Text, Required: false, Description: "As a person says it - tomorrow at 5pm, every monday."),
            new ConnectorConfigField("due_date", "Due day", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31."),
            new ConnectorConfigField("due_datetime", "Due time", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31T17:00:00+02:00."),
            new ConnectorConfigField("due_lang", "Due words' language", ConnectorConfigFieldType.Text, Required: false, Description: "Two letters - en."),
            new ConnectorConfigField("assignee", "Assignee", ConnectorConfigFieldType.Text, Required: false, Description: "A collaborator's user id, in a shared project."),
            new ConnectorConfigField("duration", "Duration", ConnectorConfigFieldType.Integer, Required: false, Description: "How long, in duration_unit."),
            new ConnectorConfigField("duration_unit", "Duration unit", ConnectorConfigFieldType.Text, Required: false, Description: "minute or day.") { Default = "minute" },
            new ConnectorConfigField("deadline", "Deadline", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TasksOperation, "Get tasks", "A project's, a section's, a label's or the ids given - or a filter's - one record each: its content, due date, priority and labels - n8n's task Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project's id, or its address."),
                    new ConnectorConfigField("section", "Section", ConnectorConfigFieldType.Text, Required: false, Description: "The section's id."),
                    new ConnectorConfigField("parent", "Parent task", ConnectorConfigFieldType.Text, Required: false, Description: "Its sub-tasks - the parent's id."),
                    new ConnectorConfigField("label", "Label", ConnectorConfigFieldType.Text, Required: false, Description: "A label's name."),
                    new ConnectorConfigField("ids", "Ids", ConnectorConfigFieldType.Text, Required: false, Description: "Task ids, comma-separated."),
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "A Todoist filter, on its own - today | overdue, #Work & p1."),
                    new ConnectorConfigField("lang", "Filter's language", ConnectorConfigFieldType.Text, Required: false, Description: "Two letters - en."),
                    MaxField,
                }),
            },
            new ConnectorOperation(GetTaskOperation, "Get a task", "One task - n8n's task Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(CreateTaskOperation, "Create a task", "Adds a task - to a project, a section or under a parent - n8n's task Create.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project's id, or its address. Empty: the Inbox."),
                    new ConnectorConfigField("section", "Section", ConnectorConfigFieldType.Text, Required: false, Description: "The section's id."),
                    new ConnectorConfigField("parent", "Parent task", ConnectorConfigFieldType.Text, Required: false, Description: "A sub-task of it - the parent's id."),
                }.Concat(TaskFieldsFor(create: true)).ToArray()),
            },
            new ConnectorOperation(QuickAddOperation, "Quick add a task", "One line as Todoist's quick add reads it - Buy milk tomorrow #Errands @shop p1 - n8n's task Quick Add.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("text", "Text", ConnectorConfigFieldType.Text, Required: true, Description: "One line, with #project, @label, p1 and a date as Todoist reads them."),
                    new ConnectorConfigField("note", "Note", ConnectorConfigFieldType.Text, Required: false, Description: "A comment on it."),
                    new ConnectorConfigField("reminder", "Reminder", ConnectorConfigFieldType.Text, Required: false, Description: "When to remind - tomorrow 9am."),
                    new ConnectorConfigField("auto_reminder", "Default reminder", ConnectorConfigFieldType.Boolean, Required: false, Description: "Add the user's default reminder when it has a time.") { Default = "false" },
                }),
            },
            new ConnectorOperation(UpdateTaskOperation, "Update a task", "Changes a task's content, description, labels, priority, due date, assignee, duration or deadline - n8n's task Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }.Concat(TaskFieldsFor(create: false)).ToArray()),
            },
            new ConnectorOperation(CloseTaskOperation, "Complete a task", "Marks a task done - n8n's task Close.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(ReopenTaskOperation, "Reopen a task", "Opens a done task again - n8n's task Reopen.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(MoveTaskOperation, "Move a task", "Moves a task to a project, a section or under a parent - one of them - n8n's task Move.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    TaskField,
                    new ConnectorConfigField("project", "Project", ConnectorConfigFieldType.Text, Required: false, Description: "The project's id, or its address."),
                    new ConnectorConfigField("section", "Section", ConnectorConfigFieldType.Text, Required: false, Description: "The section's id."),
                    new ConnectorConfigField("parent", "Parent task", ConnectorConfigFieldType.Text, Required: false, Description: "The parent's id."),
                }),
            },
            new ConnectorOperation(DeleteTaskOperation, "Delete a task", "Deletes a task - n8n's task Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField }),
            },
            new ConnectorOperation(AddCommentOperation, "Comment on a task", "Adds a comment to a task - n8n's comment Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { TaskField, new ConnectorConfigField("content", "Comment", ConnectorConfigFieldType.Multiline, Required: true, Description: "Text - Markdown as Todoist shows it.") }),
            },
            new ConnectorOperation(ProjectsOperation, "Get projects", "The projects - their ids, names and colours - n8n's project Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { MaxField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Api(context.Endpoint);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var me = await SendAsync(client, Token(context.Credentials), HttpMethod.Get, api + "/user", null, cancellationToken);
                return Text(me?["email"]) is { } email
                    ? ConnectionTestResult.Ok($"Signed in as {Text(me?["full_name"]) ?? email} ({email}).")
                    : ConnectionTestResult.Fail("Todoist answered without a user - check the API token.");
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
            var token = Token(context.Credentials);
            var operation = context.OperationName ?? TasksOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Todoist has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            if (Is(operation, TasksOperation) || Is(operation, ProjectsOperation))
            {
                string path;
                var query = new List<string>();
                if (Is(operation, ProjectsOperation))
                {
                    path = "/projects";
                }
                else if (Values.Get(parameters, "filter") is { } filter)
                {
                    if (new[] { "project", "section", "parent", "label", "ids" }.FirstOrDefault(name => Values.Get(parameters, name) is not null) is { } other)
                    {
                        throw new InvalidOperationException($"A filter reads on its own - give the filter or '{other}', not both (#Work in the filter names a project).");
                    }

                    path = "/tasks/filter";
                    query.Add("query=" + Uri.EscapeDataString(OneLine(filter, "filter")));
                    if (Values.Get(parameters, "lang") is { } lang)
                    {
                        query.Add("lang=" + Uri.EscapeDataString(Lang(lang)));
                    }
                }
                else
                {
                    path = "/tasks";
                    foreach (var (name, key) in new[] { ("project", "project_id"), ("section", "section_id"), ("parent", "parent_id") })
                    {
                        if (Values.Get(parameters, name) is { } id)
                        {
                            query.Add(key + "=" + Id(id, name));
                        }
                    }

                    if (Values.Get(parameters, "label") is { } label)
                    {
                        query.Add("label=" + Uri.EscapeDataString(OneLine(label, "label")));
                    }

                    if (Values.Get(parameters, "ids") is { } ids)
                    {
                        query.Add("ids=" + string.Join(",", ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(id => Id(id, "task"))));
                    }
                }

                var read = 0;
                string? cursor = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = query.Append("limit=" + Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)).ToList();
                    if (cursor is not null)
                    {
                        page.Add("cursor=" + Uri.EscapeDataString(cursor));
                    }

                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{api}{path}?{string.Join("&", page)}", null, cancellationToken);
                    foreach (var item in (answer?["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        read++;
                        yield return Is(operation, ProjectsOperation) ? ProjectRecord(item) : TaskRecord(item);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    cursor = Text(answer?["next_cursor"]);
                }
                while (!string.IsNullOrEmpty(cursor));

                yield break;
            }

            if (Is(operation, CreateTaskOperation))
            {
                var body = TaskBody(parameters, create: true);
                foreach (var (name, key) in new[] { ("project", "project_id"), ("section", "section_id"), ("parent", "parent_id") })
                {
                    if (Values.Get(parameters, name) is { } id)
                    {
                        body[key] = Id(id, name);
                    }
                }

                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, api + "/tasks", body, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, QuickAddOperation))
            {
                var body = new JsonObject { ["text"] = OneLine(Values.Get(parameters, "text") ?? throw new InvalidOperationException("Give the line to add - Buy milk tomorrow #Errands."), "text"), ["meta"] = true };
                if (Values.Get(parameters, "note") is { } note)
                {
                    body["note"] = note;
                }

                if (Values.Get(parameters, "reminder") is { } reminder)
                {
                    body["reminder"] = OneLine(reminder, "reminder");
                }

                body["auto_reminder"] = Values.GetBool(parameters, "auto_reminder", false);
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, api + "/tasks/quick", body, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            var task = Id(Values.Get(parameters, "task"), "task");
            if (Is(operation, GetTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Get, $"{api}/tasks/{task}", null, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateTaskOperation))
            {
                var body = TaskBody(parameters, create: false);
                if (body.Count == 0)
                {
                    throw new InvalidOperationException("Give something to change - content, description, labels, priority, a due date, an assignee, a duration or a deadline.");
                }

                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, $"{api}/tasks/{task}", body, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, MoveTaskOperation))
            {
                var to = new[] { ("project", "project_id"), ("section", "section_id"), ("parent", "parent_id") }.Where(pair => Values.Get(parameters, pair.Item1) is not null).ToList();
                if (to.Count != 1)
                {
                    throw new InvalidOperationException("Move it to one place - a project, a section or a parent task.");
                }

                var (name, key) = to[0];
                var where = Id(Values.Get(parameters, name), name);
                await SendAsync(client, token, HttpMethod.Post, $"{api}/tasks/{task}/move", new JsonObject { [key] = where }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = task, [key] = where });
                yield break;
            }

            if (Is(operation, AddCommentOperation))
            {
                var content = Values.Get(parameters, "content") ?? throw new InvalidOperationException("Give the comment's text.");
                var comment = await SendAsync(client, token, HttpMethod.Post, api + "/comments", new JsonObject { ["task_id"] = task, ["content"] = content }, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Text(comment?["id"]), ["task_id"] = task, ["content"] = Text(comment?["content"]), ["posted_at"] = Text(comment?["posted_at"]),
                });
                yield break;
            }

            // Close, reopen and delete: Todoist answers 204, the record says what was done.
            var (method, suffix, done) = Is(operation, CloseTaskOperation) ? (HttpMethod.Post, "/close", "completed")
                : Is(operation, ReopenTaskOperation) ? (HttpMethod.Post, "/reopen", "reopened")
                : (HttpMethod.Delete, "", "deleted");
            await SendAsync(client, token, method, $"{api}/tasks/{task}{suffix}", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = task, [done] = true });
        }

        // A task's fields as Todoist takes them: content of one line, labels as a list, a priority by its number or word, a due date
        // by words, a day or a time (UTC), a duration with its unit, a deadline day.
        private static JsonObject TaskBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "content") is { } content)
            {
                body["content"] = OneLine(content, "content");
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the task's content.");
            }

            if (Values.Get(parameters, "description") is { } description)
            {
                body["description"] = description;
            }

            if (Values.Get(parameters, "labels") is { } labels)
            {
                body["labels"] = new JsonArray(labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(label => (JsonNode)JsonValue.Create(OneLine(label, "label"))!).ToArray());
            }

            if (Values.Get(parameters, "priority") is { } priority)
            {
                body["priority"] = priority.ToLowerInvariant() switch
                {
                    "4" or "urgent" => 4,
                    "3" or "high" => 3,
                    "2" or "medium" => 2,
                    "1" or "normal" => 1,
                    _ => throw new InvalidOperationException($"'{priority}' is not a priority - 4 urgent, 3 high, 2 medium or 1 normal."),
                };
            }

            var dues = new[] { "due_string", "due_date", "due_datetime" }.Where(name => Values.Get(parameters, name) is not null).ToList();
            if (dues.Count > 1)
            {
                throw new InvalidOperationException($"Give one due date - {string.Join(" or ", dues)}, not both.");
            }

            if (Values.Get(parameters, "due_string") is { } words)
            {
                body["due_string"] = OneLine(words, "due words");
            }

            if (Values.Get(parameters, "due_date") is { } day)
            {
                body["due_date"] = Date(day, "due day");
            }

            if (Values.Get(parameters, "due_datetime") is { } time)
            {
                body["due_datetime"] = DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) && !Day.IsMatch(time.Trim())
                    ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException($"'{time}' is not a time - 2026-10-31T17:00:00+02:00.");
            }

            if (Values.Get(parameters, "due_lang") is { } dueLang)
            {
                body["due_lang"] = Lang(dueLang);
            }

            if (Values.Get(parameters, "assignee") is { } assignee)
            {
                body["assignee_id"] = Id(assignee, "assignee");
            }

            if (Values.Get(parameters, "duration") is { } duration)
            {
                body["duration"] = int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) && amount > 0 ? amount : throw new InvalidOperationException($"'{duration}' is not a duration - a whole number above 0.");
                var unit = Values.Get(parameters, "duration_unit")?.ToLowerInvariant() ?? "minute";
                body["duration_unit"] = unit is "minute" or "day" ? unit : throw new InvalidOperationException($"'{unit}' is not a duration unit - minute or day.");
            }

            if (Values.Get(parameters, "deadline") is { } deadline)
            {
                body["deadline_date"] = Date(deadline, "deadline");
            }

            return body;
        }

        private static ConnectorRecord TaskRecord(JsonObject task)
        {
            var id = Text(task["id"]);
            var amount = task["duration"]?["amount"] is JsonValue value && value.TryGetValue<int>(out var minutes) ? minutes : (int?)null;
            return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["content"] = Text(task["content"]),
                ["description"] = Text(task["description"]) ?? "",
                ["project_id"] = Text(task["project_id"]),
                ["section_id"] = Text(task["section_id"]),
                ["parent_id"] = Text(task["parent_id"]),
                ["labels"] = string.Join(", ", (task["labels"] as JsonArray ?? new JsonArray()).Select(Text).Where(label => !string.IsNullOrEmpty(label))),
                ["priority"] = task["priority"] is JsonValue priority && priority.TryGetValue<int>(out var level) ? level : (int?)null,
                ["due_date"] = Text(task["due"]?["date"]),
                ["due_datetime"] = Text(task["due"]?["datetime"]),
                ["due_string"] = Text(task["due"]?["string"]),
                ["is_recurring"] = task["due"]?["is_recurring"] is JsonValue recurring && recurring.TryGetValue<bool>(out var again) && again,
                ["deadline"] = Text(task["deadline"]?["date"]),
                ["duration"] = amount is null ? null : $"{amount} {Text(task["duration"]?["unit"]) ?? "minute"}",
                ["assignee_id"] = Text(task["responsible_uid"]) ?? Text(task["assignee_id"]),
                ["completed"] = (task["checked"] ?? task["is_completed"]) is JsonValue completed && completed.TryGetValue<bool>(out var done) && done,
                ["completed_at"] = Text(task["completed_at"]),
                ["created_at"] = Text(task["added_at"]) ?? Text(task["created_at"]),
                ["updated_at"] = Text(task["updated_at"]),
                ["url"] = id is null ? null : "https://app.todoist.com/app/task/" + id,
            });
        }

        private static ConnectorRecord ProjectRecord(JsonObject project) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(project["id"]),
            ["name"] = Text(project["name"]),
            ["color"] = Text(project["color"]),
            ["parent_id"] = Text(project["parent_id"]),
            ["is_favorite"] = project["is_favorite"] is JsonValue favorite && favorite.TryGetValue<bool>(out var liked) && liked,
            ["is_inbox"] = (project["inbox_project"] ?? project["is_inbox_project"]) is JsonValue inbox && inbox.TryGetValue<bool>(out var isInbox) && isInbox,
            ["view_style"] = Text(project["view_style"]),
            ["url"] = Text(project["id"]) is { } id ? "https://app.todoist.com/app/project/" + id : null,
        });

        // Todoist's API: its own address for a todoist.com one (a page's too), a stand-in's own /api/v1 otherwise.
        private static string Api(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return DefaultEndpoint;
            }

            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint.Trim(), out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"'{endpoint}' is not Todoist's API address - {DefaultEndpoint}.");
            }

            return address.Host.Equals("todoist.com", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".todoist.com", StringComparison.OrdinalIgnoreCase)
                ? DefaultEndpoint
                : address.GetLeftPart(UriPartial.Authority) + "/api/v1";
        }

        // An id, a task's or a project's address cut down to it (app.todoist.com/app/task/buy-milk-6Jf8VQXxpwv56VQ7: the part after
        // the last dash).
        private static string Id(string? given, string what)
        {
            var value = given?.Trim() ?? "";
            if (Stomme.Shared.Utilities.TryCreateAbsoluteUri(value, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
            {
                var last = address.AbsolutePath.TrimEnd('/').Split('/')[^1];
                value = last.Contains('-', StringComparison.Ordinal) ? last[(last.LastIndexOf('-') + 1)..] : last;
            }

            return Key.IsMatch(value) ? value : throw new InvalidOperationException($"'{given}' is not a Todoist {what}'s id (letters, digits, - and _){(what is "task" or "project" ? " or its address" : "")}.");
        }

        private static string Date(string given, string what) =>
            Day.IsMatch(given) && DateTime.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? given : throw new InvalidOperationException($"'{given}' is not a day for the {what} - 2026-10-31.");

        private static string Lang(string given) =>
            given.Length == 2 && given.All(char.IsAsciiLetterLower) ? given : throw new InvalidOperationException($"'{given}' is not a language - two letters, en.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static string Token(IReadOnlyDictionary<string, string> credentials) =>
            credentials.TryGetValue("token", out var token) && !string.IsNullOrWhiteSpace(token) ? token.Trim() : throw new InvalidOperationException("The Todoist connection has no API token.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // Todoist's answer; a throttled call waits as Todoist says; a failure thrown with its status, Todoist's words and its tag.
        private static async Task<JsonNode?> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
        {
            var content = body?.ToJsonString(Wire);
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
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
            }
            catch (JsonException) when (response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Todoist answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = json is JsonObject error
                    ? Text(error["error"]) is { } words ? words + (Text(error["error_tag"]) is { } tag ? $" ({tag})" : "") : null
                    : string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(said is null ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
            }

            return json;
        }
    }
}
