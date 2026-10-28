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

namespace Stomme.Modules.Connectors.MicrosoftToDo
{
    /// <summary>
    /// Microsoft To Do (BL-229, n8n's Microsoft To Do node: its lists and tasks created, got, read, updated and deleted): a user's
    /// To Do in a Microsoft 365 tenant over Microsoft Graph (<c>https://graph.microsoft.com/v1.0</c>), app-only - an Entra ID app
    /// registration's client id and secret and the tenant's token endpoint (OAuth2 client credentials, the <c>.default</c> scope;
    /// the app granted Tasks.ReadWrite.All) through the shared <see cref="OAuth2TokenProvider"/>, as Outlook's is. The app acts as
    /// the one user the connection names (<c>user</c>, an address or a user id). A test reads that user's lists.
    /// </summary>
    /// <remarks>
    /// A list and a task are named by Graph's id, each held to its shape and escaped before it reaches the path. <c>lists</c>: the
    /// user's lists; <c>get_list</c>, <c>create_list</c>, <c>rename_list</c>, <c>delete_list</c>. <c>tasks</c>: a list's tasks - those
    /// an OData filter keeps (<c>status ne 'completed'</c>) - page by page through the next links Graph gives (one that leaves the
    /// user's address never followed, so the token never leaves with it) up to <c>max</c> (1,000); one record each: its id, title,
    /// body, status, importance, due (UTC), reminder, when it was finished, its categories and times. <c>get_task</c>.
    /// <c>create_task</c>: a title of one line,
    /// a body (text, or HTML with <c>html</c>), a due time and a reminder (a time with its offset, or a day, in UTC), an importance,
    /// a status, categories; <c>update_task</c> changes those given, with no default (something, or refused); <c>delete_task</c>.
    /// A throttled call waits as Graph says; a failure is thrown with its status, Graph's code and its message.
    /// </remarks>
    public sealed class MicrosoftToDoConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        public const string DefaultEndpoint = "https://graph.microsoft.com/v1.0";

        public const string DefaultScope = "https://graph.microsoft.com/.default";

        public const string ListsOperation = "lists";

        public const string GetListOperation = "get_list";

        public const string CreateListOperation = "create_list";

        public const string RenameListOperation = "rename_list";

        public const string DeleteListOperation = "delete_list";

        public const string TasksOperation = "tasks";

        public const string GetTaskOperation = "get_task";

        public const string CreateTaskOperation = "create_task";

        public const string UpdateTaskOperation = "update_task";

        public const string DeleteTaskOperation = "delete_task";

        public const int DefaultMax = 1000;

        private const int PageSize = 100;

        private static readonly Regex GraphId = new("^[A-Za-z0-9+/=_-]{1,512}$", RegexOptions.CultureInvariant);

        private static readonly Regex User = new("^[^\\s/\\\\?#@]+@[^\\s/\\\\?#@]+$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

        // JSON as written: a body's HTML and accents as they are, not as \u escapes.
        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Statuses = { "notStarted", "inProgress", "completed", "waitingOnOthers", "deferred" };

        private static readonly string[] Known = { ListsOperation, GetListOperation, CreateListOperation, RenameListOperation, DeleteListOperation, TasksOperation, GetTaskOperation, CreateTaskOperation, UpdateTaskOperation, DeleteTaskOperation };

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "microsofttodo";

        public string DisplayName => "Microsoft To Do";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Productivity };

        public Uri WebsiteUrl { get; } = new("https://to-do.office.com");

        public Uri ApiDocumentationUrl { get; } = new("https://learn.microsoft.com/graph/api/resources/todo-overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("user", "User", ConnectorConfigFieldType.Text, Required: true,
                Description: "The user whose To Do the app reads and writes - their address (ada@contoso.com) or user id."),
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Required: false,
                Description: "The token scope requested from the identity platform. Leave it as it is.") { Default = DefaultScope },
        });

        private static readonly ConnectorConfigField ListField = new("list", "List", ConnectorConfigFieldType.Text, Required: true, Description: "The list's id.");

        private static readonly ConnectorConfigField TaskField = new("task", "Task", ConnectorConfigFieldType.Text, Required: true, Description: "The task's id.");

        private static ConnectorConfigField[] TaskFields(bool create) => new[]
        {
            new ConnectorConfigField("title", "Title", ConnectorConfigFieldType.Text, Required: create, Description: "One line."),
            new ConnectorConfigField("body", "Body", ConnectorConfigFieldType.Multiline, Required: false, Description: "Text - or HTML with html = true."),
            new ConnectorConfigField("html", "HTML", ConnectorConfigFieldType.Boolean, Required: false, Description: "The body is HTML.") { Default = "false" },
            new ConnectorConfigField("due", "Due", ConnectorConfigFieldType.Text, Required: false, Description: "2026-10-31, or 2026-10-31T17:00:00+02:00."),
            new ConnectorConfigField("reminder", "Reminder", ConnectorConfigFieldType.Text, Required: false, Description: "A time - 2026-10-31T09:00:00+02:00."),
            new ConnectorConfigField("importance", "Importance", ConnectorConfigFieldType.Text, Required: false, Description: "low, normal or high."),
            new ConnectorConfigField("status", "Status", ConnectorConfigFieldType.Text, Required: false, Description: "notStarted, inProgress, completed, waitingOnOthers or deferred."),
            new ConnectorConfigField("categories", "Categories", ConnectorConfigFieldType.Text, Required: false, Description: "Names, comma-separated."),
        };

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(ListsOperation, "Get lists", "The user's task lists - their ids and names - n8n's list Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) } }),
            },
            new ConnectorOperation(GetListOperation, "Get a list", "One list - n8n's list Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField }),
            },
            new ConnectorOperation(CreateListOperation, "Create a list", "Makes a task list - n8n's list Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line.") }),
            },
            new ConnectorOperation(RenameListOperation, "Rename a list", "Gives a list a new name - n8n's list Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, new ConnectorConfigField("name", "Name", ConnectorConfigFieldType.Text, Required: true, Description: "One line.") }),
            },
            new ConnectorOperation(DeleteListOperation, "Delete a list", "Deletes a list and its tasks - n8n's list Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField }),
            },
            new ConnectorOperation(TasksOperation, "Get tasks", "A list's tasks - one record each: its title, status, importance and due time - n8n's task Get Many.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    ListField,
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Required: false, Description: "An OData filter - status ne 'completed'."),
                    new ConnectorConfigField("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) },
                }),
            },
            new ConnectorOperation(GetTaskOperation, "Get a task", "One task - n8n's task Get.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }),
            },
            new ConnectorOperation(CreateTaskOperation, "Create a task", "Adds a task to a list - n8n's task Create.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField }.Concat(TaskFields(create: true)).ToArray()),
            },
            new ConnectorOperation(UpdateTaskOperation, "Update a task", "Changes a task's title, body, due time, reminder, importance, status or categories - n8n's task Update.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }.Concat(TaskFields(create: false)).ToArray()),
            },
            new ConnectorOperation(DeleteTaskOperation, "Delete a task", "Deletes a task - n8n's task Delete.")
            {
                Parameters = new ConnectorConfigSchema(new[] { ListField, TaskField }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var me = UserUrl(context.Endpoint, context.Settings);
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                var lists = await SendAsync(client, token, HttpMethod.Get, me + "/todo/lists?$top=100", null, cancellationToken);
                var names = (lists["value"] as JsonArray ?? new JsonArray()).Select(list => Text(list?["displayName"])).Where(name => name is not null).ToList();
                return ConnectionTestResult.Ok(names.Count == 0 ? "Signed in - no lists yet." : $"Signed in - {names.Count} list{(names.Count == 1 ? "" : "s")}, {names[0]} first.");
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
            var me = UserUrl(context.Endpoint, context.Settings);
            var operation = context.OperationName ?? TasksOperation;
            var parameters = context.Parameters;
            if (!Known.Any(known => Is(operation, known)))
            {
                throw new InvalidOperationException($"Microsoft To Do has no operation '{operation}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            }

            // Everything held to its shape before a token is asked for.
            var list = Is(operation, ListsOperation) || Is(operation, CreateListOperation) ? null : Id(Values.Get(parameters, "list"), "list");
            var task = Is(operation, GetTaskOperation) || Is(operation, UpdateTaskOperation) || Is(operation, DeleteTaskOperation) ? Id(Values.Get(parameters, "task"), "task") : null;
            var name = Is(operation, CreateListOperation) || Is(operation, RenameListOperation) ? OneLine(Values.Get(parameters, "name") ?? throw new InvalidOperationException("Give the list's name."), "name") : null;
            var body = Is(operation, CreateTaskOperation) || Is(operation, UpdateTaskOperation) ? TaskBody(parameters, Is(operation, CreateTaskOperation)) : null;
            var filter = Values.Get(parameters, "filter") is { } given ? OneLine(given, "filter") : null;
            var max = int.TryParse(Values.Get(parameters, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) && most > 0 ? most : DefaultMax;

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            using var client = context.Egress.CreateHttpClient(context.Timeout);
            var lists = me + "/todo/lists";
            var tasks = $"{lists}/{Uri.EscapeDataString(list ?? "")}/tasks";

            if (Is(operation, ListsOperation) || Is(operation, TasksOperation))
            {
                var listsOnly = Is(operation, ListsOperation);
                var read = 0;
                string? url = (listsOnly ? lists : tasks) + "?$top=" + Math.Min(PageSize, max).ToString(CultureInfo.InvariantCulture) + (filter is null || listsOnly ? "" : "&$filter=" + Uri.EscapeDataString(filter));
                while (url is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await SendAsync(client, token, HttpMethod.Get, url, null, cancellationToken);
                    foreach (var item in (page["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        read++;
                        yield return listsOnly ? ListRecord(item) : TaskRecord(item);
                        if (read >= max)
                        {
                            yield break;
                        }
                    }

                    // Graph's next link followed only under the user's own address - the token never leaves with it.
                    url = Text(page["@odata.nextLink"]) is { } next && next.StartsWith(me + "/", StringComparison.OrdinalIgnoreCase) ? next : null;
                }

                yield break;
            }

            if (Is(operation, CreateListOperation))
            {
                yield return ListRecord(await SendAsync(client, token, HttpMethod.Post, lists, new JsonObject { ["displayName"] = name }, cancellationToken));
                yield break;
            }

            if (Is(operation, GetListOperation))
            {
                yield return ListRecord(await SendAsync(client, token, HttpMethod.Get, $"{lists}/{Uri.EscapeDataString(list!)}", null, cancellationToken));
                yield break;
            }

            if (Is(operation, RenameListOperation))
            {
                yield return ListRecord(await SendAsync(client, token, new HttpMethod("PATCH"), $"{lists}/{Uri.EscapeDataString(list!)}", new JsonObject { ["displayName"] = name }, cancellationToken));
                yield break;
            }

            if (Is(operation, DeleteListOperation))
            {
                await SendAsync(client, token, HttpMethod.Delete, $"{lists}/{Uri.EscapeDataString(list!)}", null, cancellationToken);
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = list, ["deleted"] = true });
                yield break;
            }

            if (Is(operation, CreateTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Post, tasks, body, cancellationToken));
                yield break;
            }

            if (Is(operation, GetTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, HttpMethod.Get, $"{tasks}/{Uri.EscapeDataString(task!)}", null, cancellationToken));
                yield break;
            }

            if (Is(operation, UpdateTaskOperation))
            {
                yield return TaskRecord(await SendAsync(client, token, new HttpMethod("PATCH"), $"{tasks}/{Uri.EscapeDataString(task!)}", body, cancellationToken));
                yield break;
            }

            await SendAsync(client, token, HttpMethod.Delete, $"{tasks}/{Uri.EscapeDataString(task!)}", null, cancellationToken);
            yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = task, ["deleted"] = true });
        }

        // A task's fields as Graph takes them: a title of one line, a body as text or HTML, times in UTC with their zone named, an
        // importance and a status by their words, categories as a list.
        private static JsonObject TaskBody(IReadOnlyDictionary<string, string> parameters, bool create)
        {
            var body = new JsonObject();
            var html = Flag(parameters, "html");
            if (Values.Get(parameters, "title") is { } title)
            {
                body["title"] = OneLine(title, "title");
            }
            else if (create)
            {
                throw new InvalidOperationException("Give the task's title.");
            }

            if (parameters.TryGetValue("body", out var text) && !string.IsNullOrWhiteSpace(text))
            {
                body["body"] = new JsonObject { ["content"] = text, ["contentType"] = html ? "html" : "text" };
            }

            if (Values.Get(parameters, "due") is { } due)
            {
                body["dueDateTime"] = When(due, "due");
            }

            if (Values.Get(parameters, "reminder") is { } reminder)
            {
                body["reminderDateTime"] = When(reminder, "reminder");
                body["isReminderOn"] = true;
            }

            if (Values.Get(parameters, "importance") is { } importance)
            {
                body["importance"] = Choice(importance, "importance", "low", "normal", "high");
            }

            if (Values.Get(parameters, "status") is { } status)
            {
                body["status"] = Choice(status, "status", Statuses);
            }

            if (Values.Get(parameters, "categories") is { } categories)
            {
                body["categories"] = new JsonArray(categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(category => (JsonNode)JsonValue.Create(OneLine(category, "category"))!).ToArray());
            }

            if (!create && body.Count == 0)
            {
                throw new InvalidOperationException("Give something to change - a title, a body, a due time, a reminder, an importance, a status or categories.");
            }

            return body;
        }

        // A time as Graph's dateTimeTimeZone, in UTC: a day its midnight, a time with its offset, one without taken as UTC.
        private static JsonObject When(string given, string what) =>
            DateTimeOffset.TryParse(given, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? new JsonObject { ["dateTime"] = moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = "UTC" }
                : throw new InvalidOperationException($"'{given}' is not a time ({what}) - 2026-10-31, or 2026-10-31T17:00:00+02:00.");

        private static ConnectorRecord TaskRecord(JsonObject task) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(task["id"]),
            ["title"] = Text(task["title"]) ?? "",
            ["body"] = Text(task["body"]?["content"]) ?? "",
            ["status"] = Text(task["status"]),
            ["importance"] = Text(task["importance"]),
            ["due"] = Utc(task["dueDateTime"]),
            ["reminder"] = task["isReminderOn"] is JsonValue on && on.TryGetValue<bool>(out var set) && set ? Utc(task["reminderDateTime"]) : null,
            ["completed_at"] = Utc(task["completedDateTime"]),
            ["categories"] = string.Join(", ", (task["categories"] as JsonArray ?? new JsonArray()).Select(Text).Where(category => category is not null)),
            ["created"] = Text(task["createdDateTime"]),
            ["updated"] = Text(task["lastModifiedDateTime"]),
        });

        private static ConnectorRecord ListRecord(JsonObject list) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Text(list["id"]),
            ["name"] = Text(list["displayName"]),
            ["owner"] = list["isOwner"] is JsonValue owner && owner.TryGetValue<bool>(out var owns) && owns,
            ["shared"] = list["isShared"] is JsonValue shared && shared.TryGetValue<bool>(out var with) && with,
            ["well_known"] = Text(list["wellknownListName"]),
        });

        // Graph's dateTimeTimeZone as a time in UTC - Graph gives To Do's times in UTC unless asked otherwise; one in another zone
        // as Graph wrote it, its zone named beside it.
        private static string? Utc(JsonNode? when)
        {
            if (Text(when?["dateTime"]) is not { } time)
            {
                return null;
            }

            var zone = Text(when?["timeZone"]);
            return zone is not null && !string.Equals(zone, "UTC", StringComparison.OrdinalIgnoreCase) ? $"{time} ({zone})"
                : DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? moment.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : time;
        }

        private static bool Flag(IReadOnlyDictionary<string, string> parameters, string name) =>
            Values.Get(parameters, name) is not { } given ? false
                : given.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                : given.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
                : throw new InvalidOperationException($"'{given}' is not true or false ({name}).");

        // The user's own address under Graph: the endpoint, users/, the user held to an address's or a user id's shape.
        private static string UserUrl(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            var user = Values.Get(settings, "user") ?? throw new InvalidOperationException("Name the user whose To Do the app acts on - the connection's user setting.");
            if (!User.IsMatch(user))
            {
                throw new InvalidOperationException($"'{user}' is not a user - an address or a user id.");
            }

            return $"{Values.BaseUrl(endpoint, DefaultEndpoint)}/users/{Uri.EscapeDataString(user)}";
        }

        private static string Id(string? given, string what) =>
            given is not null && GraphId.IsMatch(given.Trim()) ? given.Trim() : throw new InvalidOperationException($"'{given}' is not a {what}'s id.");

        private static string Choice(string given, string what, params string[] allowed) =>
            allowed.FirstOrDefault(choice => string.Equals(choice, given.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{given}' is not a{("aeiou".Contains(what[0]) ? "n" : "")} {what} - {string.Join(", ", allowed[..^1])} or {allowed[^1]}.");

        private static string OneLine(string value, string what) =>
            value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ? throw new InvalidOperationException($"The {what} has a line break - one line only.") : value.Trim();

        private static bool Is(string operation, string name) => string.Equals(operation, name, StringComparison.OrdinalIgnoreCase);

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        // Graph's answer as JSON (an empty answer an empty object), a throttled call waiting as Graph says; a failure thrown with its
        // status, Graph's own code and its message.
        private static async Task<JsonObject> SendAsync(HttpClient client, string token, HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken)
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
                throw new InvalidOperationException($"Microsoft To Do answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = Text(json?["error"]?["code"]);
                var said = Text(json?["error"]?["message"]);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}{(code is null ? "" : " " + code)}{(said is null ? "." : ": " + (said.Length > 200 ? said[..200] : said))}");
            }

            return json as JsonObject ?? new JsonObject();
        }
    }
}
