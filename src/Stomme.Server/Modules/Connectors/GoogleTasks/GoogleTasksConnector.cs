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

namespace Stomme.Modules.Connectors.GoogleTasks
{
    /// <summary>
    /// Google Tasks (BL-229, n8n's Google Tasks node: a task created, got, listed, updated and deleted - and moved, its lists read
    /// and a list's finished ones cleared): the Tasks API (<c>https://tasks.googleapis.com/tasks/v1</c>) over the OAuth2
    /// REFRESH-TOKEN scheme - a Google Cloud OAuth client's id and secret and a refresh token granted the tasks scope - exchanged
    /// at Google's token endpoint (the <c>token_url</c> setting) through the shared <see cref="OAuth2TokenProvider"/>, as Google
    /// Calendar's is. A test reads the task lists.
    /// </summary>
    /// <remarks>
    /// A task list is <c>@default</c> (the user's own) or its id, a task its id, each held to Google's shape. <c>task_lists</c>:
    /// the lists, page by page. <c>tasks</c>: a list's tasks - the finished ones too unless <c>show_completed</c> says not, the
    /// deleted and hidden ones when asked - due, finished or changed between two times, page by page by Google's token (100 a
    /// page, a page no bigger than what is left) up to <c>max</c> (1,000); one record each: its id, title, notes, status, due day,
    /// when it was finished, its parent and place, whether it is deleted or hidden, its links and address. <c>get_task</c> one.
    /// <c>create_task</c>: a title of one line, notes, a due day, finished (completed) or not - under a parent, after a sibling;
    /// <c>update_task</c> changes those given, with no default (something, or refused); <c>move_task</c> puts it under a parent,
    /// after a sibling, or in another list; <c>delete_task</c>; <c>clear_completed</c> hides a list's finished tasks. A throttled
    /// call waits as Google says; a failure is thrown with its status, Google's status word and its message.
    /// </remarks>
    public sealed class GoogleTasksConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://tasks.googleapis.com/tasks/v1";

        public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

        public const string TaskListsOperation = "task_lists";

        public const string TasksOperation = "tasks";

        public const string GetTaskOperation = "get_task";

        public const string CreateTaskOperation = "create_task";

        public const string UpdateTaskOperation = "update_task";

        public const string MoveTaskOperation = "move_task";

        public const string DeleteTaskOperation = "delete_task";

        public const string ClearCompletedOperation = "clear_completed";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex Id = new("^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant);

        private static readonly Regex Day = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

        // JSON as written: notes' accents and markup as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known = { TaskListsOperation, TasksOperation, GetTaskOperation, CreateTaskOperation, UpdateTaskOperation, MoveTaskOperation, DeleteTaskOperation, ClearCompletedOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "googletasks";

        public string DisplayName => "Google Tasks";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://tasks.google.com");

        public Uri ApiDocumentationUrl { get; } = new("https://developers.google.com/tasks/reference/rest");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("token_url", "Token endpoint", ConnectorConfigFieldType.Text, Required: false,
                Description: "Google's OAuth2 token endpoint. Leave it as it is.") { Default = DefaultTokenUrl },
        });

        private static readonly ConnectorConfigField ListField = new("list", "Task list", ConnectorConfigFieldType.Text, Required: false, Description: "@default - the user's own - or a list's id.") { Default = "@default" };

        private static readonly ConnectorConfigField TaskField = new("task", "Task", ConnectorConfigFieldType.Text, Required: true, Description: "The task's id.");

        private static ConnectorConfigField[] TaskFields(bool create) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("notes", "Notes", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text."),
            new ConnectorConfigField("due", "Due", ConnectorConfigFieldType.Text, Required: false, Description: "A day - 2026-10-31 (Google keeps the day, not the time)."),
            new ConnectorConfigField("completed", "Finished", ConnectorConfigFieldType.Boolean, Required: false, Description: "Done (true), or to do again (false)."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(TaskListsOperation, "Get task lists", "The user's task lists - their ids and titles.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation(TasksOperation, "Get tasks", "A list's tasks - one record each: its title, notes, status and due day - n8n's Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField,
                    new ConnectorConfigField("show_completed", "Finished ones too", ConnectorConfigFieldType.Boolean, Required: false, Description: "The finished tasks as well.") { Default = "true" },
                    new ConnectorConfigField("show_deleted", "Deleted ones too", ConnectorConfigFieldType.Boolean, Required: false, Description: "The deleted tasks as well.") { Default = "false" },
                    new ConnectorConfigField("show_hidden", "Hidden ones too", ConnectorConfigFieldType.Boolean, Required: false, Description: "Those cleared - and those finished in Google's own apps.") { Default = "false" },
                    new ConnectorConfigField("due_min", "Due from", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-01, or a time."),
                    new ConnectorConfigField("due_max", "Due before", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31, or a time."),
                    new ConnectorConfigField("completed_min", "Finished from", ConnectorConfigFieldType.Text, Required: false, Description: "A day or a time."),
                    new ConnectorConfigField("completed_max", "Finished before", ConnectorConfigFieldType.Text, Required: false, Description: "A day or a time."),
                    new ConnectorConfigField("updated_min", "Changed since", ConnectorConfigFieldType.Text, Required: false, Description: "A day or a time."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetTaskOperation, "Get a task", "One task - n8n's Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }),
            },
            new ConnectorOperation(CreateTaskOperation, "Create a task", "Adds a task to a list - under a parent, after a sibling - n8n's Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField }.Concat(TaskFields(create: true)).Concat(new[]
                {
                    new ConnectorConfigField("parent", "Parent task", ConnectorConfigFieldType.Text, Required: false, Description: "A sub-task of it - its id."),
                    new ConnectorConfigField("previous", "After", ConnectorConfigFieldType.Text, Required: false, Description: "The sibling it comes after - its id. Empty: first."),
                }).ToArray()),
            },
            new ConnectorOperation(UpdateTaskOperation, "Update a task", "Changes a task's title, notes or due day, or finishes it - n8n's Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }.Concat(TaskFields(create: false)).ToArray()),
            },
            new ConnectorOperation(MoveTaskOperation, "Move a task", "Puts a task under a parent, after a sibling, or in another list.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField,
                    TaskField,
                    new ConnectorConfigField("parent", "Parent task", ConnectorConfigFieldType.Text, Required: false, Description: "Its id. Empty: at the top."),
                    new ConnectorConfigField("previous", "After", ConnectorConfigFieldType.Text, Required: false, Description: "The sibling it comes after. Empty: first."),
                    new ConnectorConfigField("to_list", "To list", ConnectorConfigFieldType.Text, Required: false, Description: "Another list's id."),
                }),
            },
            new ConnectorOperation(DeleteTaskOperation, "Delete a task", "Deletes a task - n8n's Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }),
            },
            new ConnectorOperation(ClearCompletedOperation, "Clear finished tasks", "Hides a list's finished tasks from it.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var api = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var lists = await SendAsync(client, token, HttpMethod.Get, api + "/users/@me/lists?maxResults=100", null, cancellationToken);
                var titles = (lists?["items"] as JsonArray ?? new JsonArray()).Select(list => Text(list?["title"])).Where(title => title is not null).ToList();
                return ConnectionTestResult.Ok(titles.Count == 0 ? "Signed in - no task lists yet." : $"Signed in - {titles.Count} task list{(titles.Count == 1 ? "" : "s")}, {titles[0]} first.");
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
            var operation = context.OperationName ?? TasksOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Google Tasks has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before a token is asked for.
            var list = List(Values.Get(parameters, "list"));
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;
            var task = Is(operation, GetTaskOperation) || Is(operation, UpdateTaskOperation) || Is(operation, MoveTaskOperation) || Is(operation, DeleteTaskOperation)
                ? Key(Values.Get(parameters, "task"), "task")
                : null;
            var query = Is(operation, TasksOperation) ? TaskQuery(parameters) : new List<string>();
            var body = Is(operation, CreateTaskOperation) || Is(operation, UpdateTaskOperation) ? TaskBody(parameters, Is(operation, CreateTaskOperation)) : null;
            var placing = new List<string>();
            if (Is(operation, CreateTaskOperation) || Is(operation, MoveTaskOperation))
            {
                foreach (var name in new[] { "parent", "previous" })
                {
                    if (Values.Get(parameters, name) is { } id)
                    {
                        placing.Add(name + "=" + Key(id, name == "parent" ? "parent task" : "sibling"));
                    }
                }

                if (Is(operation, MoveTaskOperation) && Values.Get(parameters, "to_list") is { } toList)
                {
                    placing.Add("destinationTasklist=" + List(toList));
                }
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var tasks = $"{api}/lists/{list}/tasks";

            if (Is(operation, TaskListsOperation) || Is(operation, TasksOperation))
            {
                var lists = Is(operation, TaskListsOperation);
                var read = 0;
                string? pageToken = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = query.Append("maxResults=" + Math.Min(PageSize, max - read).ToString(CultureInfo.InvariantCulture)).ToList();
                    if (pageToken is not null)
                    {
                        page.Add("pageToken=" + Uri.EscapeDataString(pageToken));
                    }

                    var answer = await SendAsync(client, token, HttpMethod.Get, $"{(lists ? api + "/users/@me/lists" : tasks)}?{string.Join("&", page)}", null, cancellationToken);
                    foreach (var item in (answer?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        read++;
                        yield return lists ? ListRecord(item) : TaskRecord(item);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    pageToken = Text(answer?["nextPageToken"]);
                }
                while (!string.IsNullOrEmpty(pageToken));

                yield break;
            }

            if (Is(operation, ClearCompletedOperation))
            {
                await SendAsync(client, token, HttpMethod.Post, $"{api}/lists/{list}/clear", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["list"] = list, ["cleared"] = true });
                yield break;
            }

            if (Is(operation, CreateTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, tasks + (placing.Count == 0 ? "" : "?" + string.Join("&", placing)), body, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, GetTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Get, $"{tasks}/{task}", null, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, UpdateTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, new HttpMethod("PATCH"), $"{tasks}/{task}", body, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            if (Is(operation, MoveTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, $"{tasks}/{task}/move{(placing.Count == 0 ? "" : "?" + string.Join("&", placing))}", null, cancellationToken) as JsonObject ?? new JsonObject());
                yield break;
            }

            await SendAsync(client, token, HttpMethod.Delete, $"{tasks}/{task}", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = task, ["deleted"] = true });
        }

        private static List<string> TaskQuery(IReadOnlyDictionary<string, string> parameters)
        {
            var query = new List<string>
            {
                "showCompleted=" + Flag(parameters, "show_completed", true),
                "showDeleted=" + Flag(parameters, "show_deleted", false),
                "showHidden=" + Flag(parameters, "show_hidden", false),
            };
            foreach (var (name, key) in new[] { ("due_min", "dueMin"), ("due_max", "dueMax"), ("completed_min", "completedMin"), ("completed_max", "completedMax"), ("updated_min", "updatedMin") })
            {
                if (Values.Get(parameters, name) is { } time)
                {
                    query.Add(key + "=" + Uri.EscapeDataString(Instant(time, name.Replace('_', ' '))));
                }
            }

            return query;
        }

        // A task's fields as Google takes them: a title of one line, notes as written, a due day (Google keeps the day at midnight
        // UTC), finished or to do.
        private static JsonObject TaskBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            if (Values.Get(parameters, "title") is { } title)
            {
                body["title"] = title.Contains('\n', StringComparison.Ordinal) || title.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException("The title has a line break - one line only.") : title.Trim();
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the task's title.");
            }

            if (parameters.TryGetValue("notes", out var notes) && !string.IsNullOrWhiteSpace(notes))
            {
                body["notes"] = notes;
            }

            if (Values.Get(parameters, "due") is { } due)
            {
                var day = Day.IsMatch(due) ? due : DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                body["due"] = day is not null && DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? day + "T00:00:00.000Z"
                    : throw new InvalidOperationException($"'{due}' is not a day - 2026-10-31.");
            }

            if (Values.Get(parameters, "completed") is { } completed)
            {
                body["status"] = completed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "completed"
                    : completed.Equals("false", StringComparison.OrdinalIgnoreCase) ? "needsAction"
                    : throw new InvalidOperationException($"'{completed}' is not true or false (completed).");
                if (body["status"]!.GetValue<string>() == "needsAction")
                {
                    body["completed"] = null; // to do again: Google forgets when it was finished
                }
            }

            if (!create && body.Count == 0)
            {
                throw new InvalidOperationException("Give something to change - a title, notes, a due day or completed.");
            }

            return body;
        }

        private static ConnectorRecord TaskRecord(JsonObject task) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(task["id"]),
            ["title"] = Text(task["title"]) ?? "",
            ["notes"] = Text(task["notes"]) ?? "",
            ["status"] = Text(task["status"]),
            ["completed"] = Text(task["status"]) == "completed",
            ["due"] = Text(task["due"]) is { Length: >= 10 } due ? due[..10] : null,
            ["completed_at"] = Text(task["completed"]),
            ["parent"] = Text(task["parent"]),
            ["position"] = Text(task["position"]),
            ["deleted"] = task["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var gone) && gone,
            ["hidden"] = task["hidden"] is JsonValue hidden && hidden.TryGetValue<bool>(out var away) && away,
            ["links"] = string.Join(", ", (task["links"] as JsonArray ?? new JsonArray()).Select(link => Text(link?["link"])).Where(link => link is not null)),
            ["url"] = Text(task["webViewLink"]),
            ["updated"] = Text(task["updated"]),
        });

        private static ConnectorRecord ListRecord(JsonObject list) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(list["id"]),
            ["title"] = Text(list["title"]),
            ["updated"] = Text(list["updated"]),
        });

        private static string List(string? given) =>
            given is null || given == "@default" ? "@default" : Key(given, "task list");

        private static string Key(string? given, string what) =>
            given is not null && Id.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s id.");

        private static string Flag(IReadOnlyDictionary<string, string> parameters, string name, bool fallback) =>
            Values.Get(parameters, name) is { } given
                ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() : throw new InvalidOperationException($"'{given}' is not true or false ({name.Replace('_', ' ')}).")
                : fallback ? "true" : "false";

        // A moment for a query, in UTC as Google takes it: a day its midnight, a time without an offset taken as UTC.
        private static string Instant(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-01, or 2026-10-01T09:00:00+02:00.");

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest(Values.GetOrDefault(settings, "token_url", DefaultTokenUrl), Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        // Google's answer as JSON (an empty answer null), a throttled call waiting as Google says; a failure thrown with its status,
        // Google's status word and its message.
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
                throw new InvalidOperationException($"Google Tasks answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = Text(json?["error"]?["status"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(status is null ? "" : " " + status)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json;
        }
    }
}
