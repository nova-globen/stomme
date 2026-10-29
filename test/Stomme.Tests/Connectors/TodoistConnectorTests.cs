#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Todoist;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Todoist: the token as the bearer; todoist.com's address taken to the API, a stand-in's to its own /api/v1; a
    /// project's tasks page by page by Todoist's cursor up to the most asked for, a page no bigger than what is left, by a section,
    /// a parent, a label and ids too, or by a filter on its own; each task a plain record; a task got, created (the Inbox when no
    /// project; a priority by its number or word; a due date by words, a day or a time in UTC; labels, a duration, a deadline),
    /// quick-added, updated with no default, closed, reopened, moved to one place, deleted and commented on; the projects; what
    /// could go wrong refused before anything is sent; Todoist's refusal its words and tag; a test naming the user.
    /// </summary>
    public sealed class TodoistConnectorTests
    {
        private const string Api = "https://api.todoist.com/api/v1";

        private const string Token = "0123456789abcdef0123456789abcdef01234567";

        private sealed class TodoistApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(TodoistApi todoist, string operation, Dictionary<string, string> parameters, string endpoint = "https://api.todoist.com/api/v1") =>
            Contexts.Extract(new StubEgress(todoist), endpoint, operation, parameters, null, new Dictionary<string, string> { ["token"] = Token });

        private static TodoistConnector Connector() => new();

        private static string TaskJson(int n, string content = "Buy milk") =>
            "{\"id\":\"6Jf8VQXxpwv5" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "\",\"content\":\"" + content + " " + n + "\",\"description\":\"Café <b>full</b> & fresh\",\"project_id\":\"6Jf8VQXxpwv56VQ7\",\"section_id\":null,\"parent_id\":null,"
            + "\"labels\":[\"shop\",\"errands\"],\"priority\":4,\"due\":{\"date\":\"2026-10-31\",\"string\":\"Oct 31\",\"lang\":\"en\",\"is_recurring\":false,\"timezone\":null},\"deadline\":{\"date\":\"2026-11-02\",\"lang\":\"en\"},"
            + "\"duration\":{\"amount\":30,\"unit\":\"minute\"},\"responsible_uid\":\"2671355\",\"checked\":false,\"completed_at\":null,\"added_at\":\"2026-10-01T10:00:00Z\",\"updated_at\":\"2026-10-02T10:00:00Z\"}";

        private static string Page(int from, int count, string? next) =>
            "{\"results\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => TaskJson(n))) + "],\"next_cursor\":" + (next is null ? "null" : "\"" + next + "\"") + "}";

        [Fact]
        public async Task A_projects_tasks_are_read_page_by_page_by_cursor_up_to_the_most_asked_for_by_the_bearer()
        {
            var todoist = new TodoistApi { Answer = (_, url) => (HttpStatusCode.OK, url.Contains("cursor=c1", StringComparison.Ordinal) ? Page(201, 200, "c2") : Page(1, 200, "c1")) };
            var records = await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.TasksOperation, new() { ["project"] = "https://app.todoist.com/app/project/groceries-6Jf8VQXxpwv56VQ7", ["max"] = "250" }, "https://app.todoist.com/app/today"));

            Assert.Equal(250, records.Count); // the most asked for
            Assert.All(todoist.Requests, request => Assert.Equal("Bearer " + Token, request.Authorization));
            Assert.Equal(new[] { Api + "/tasks?project_id=6Jf8VQXxpwv56VQ7&limit=200", Api + "/tasks?project_id=6Jf8VQXxpwv56VQ7&limit=50&cursor=c1" }, todoist.Requests.Select(request => request.Url)); // a page's address cut to its id; a page no bigger than what is left
            var task = records[0].Fields;
            Assert.Equal(("6Jf8VQXxpwv50001", "Buy milk 1", "Café <b>full</b> & fresh", "6Jf8VQXxpwv56VQ7", "shop, errands", 4),
                ((string?)task["id"], (string?)task["content"], (string?)task["description"], (string?)task["project_id"], (string?)task["labels"], (int?)task["priority"]));
            Assert.Equal(("2026-10-31", "Oct 31", false, "2026-11-02", "30 minute", "2671355", false, "2026-10-01T10:00:00Z", "https://app.todoist.com/app/task/6Jf8VQXxpwv50001"),
                ((string?)task["due_date"], (string?)task["due_string"], (bool?)task["is_recurring"], (string?)task["deadline"], (string?)task["duration"], (string?)task["assignee_id"], (bool?)task["completed"], (string?)task["created_at"], (string?)task["url"]));

            var by = new TodoistApi { Answer = (_, _) => (HttpStatusCode.OK, Page(1, 3, null)) };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(by, TodoistConnector.TasksOperation, new() { ["section"] = "s1", ["parent"] = "p_1", ["label"] = "on hold & later", ["ids"] = "a1, b2" }, "http://127.0.0.1:5280/app"))).Count);
            Assert.Equal("http://127.0.0.1:5280/api/v1/tasks?section_id=s1&parent_id=p_1&label=on%20hold%20%26%20later&ids=a1,b2&limit=200", Assert.Single(by.Requests).Url); // a stand-in's own /api/v1; no cursor: the last

            var filtered = new TodoistApi { Answer = (_, _) => (HttpStatusCode.OK, Page(1, 2, null)) };
            await Contexts.DrainAsync(Connector(), Context(filtered, TodoistConnector.TasksOperation, new() { ["filter"] = "today | overdue & #Work", ["lang"] = "en" }));
            Assert.Equal(Api + "/tasks/filter?query=today%20%7C%20overdue%20%26%20%23Work&lang=en&limit=200", Assert.Single(filtered.Requests).Url);

            var ten = new TodoistApi { Answer = (_, _) => (HttpStatusCode.OK, Page(1, 200, "c")) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, TodoistConnector.TasksOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(5, ten.Requests.Count);

            var projects = new TodoistApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"results\":[{\"id\":\"6Jf8VQXxpwv56VQ7\",\"name\":\"Groceries\",\"color\":\"berry_red\",\"parent_id\":null,\"is_favorite\":true,\"inbox_project\":false,\"view_style\":\"list\"},{\"id\":\"inbox1\",\"name\":\"Inbox\",\"color\":\"grey\",\"is_favorite\":false,\"inbox_project\":true,\"view_style\":\"list\"}],\"next_cursor\":null}") };
            var listed = await Contexts.DrainAsync(Connector(), Context(projects, TodoistConnector.ProjectsOperation, new()));
            Assert.Equal(("Groceries", "berry_red", true, false, "https://app.todoist.com/app/project/6Jf8VQXxpwv56VQ7"), ((string?)listed[0].Fields["name"], (string?)listed[0].Fields["color"], (bool?)listed[0].Fields["is_favorite"], (bool?)listed[0].Fields["is_inbox"], (string?)listed[0].Fields["url"]));
            Assert.True((bool?)listed[1].Fields["is_inbox"]);
            Assert.Equal(Api + "/projects?limit=200", Assert.Single(projects.Requests).Url);
        }

        [Fact]
        public async Task A_task_is_got_created_quick_added_updated_closed_reopened_moved_deleted_and_commented_on()
        {
            var todoist = new TodoistApi
            {
                Answer = (method, url) => (method == HttpMethod.Post && url.EndsWith("/close", StringComparison.Ordinal) || url.EndsWith("/reopen", StringComparison.Ordinal) || url.EndsWith("/move", StringComparison.Ordinal) || method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK,
                    url.EndsWith("/comments", StringComparison.Ordinal) ? "{\"id\":\"c1\",\"task_id\":\"t1\",\"content\":\"On it.\",\"posted_at\":\"2026-10-12T08:00:00Z\"}"
                    : url.EndsWith("/close", StringComparison.Ordinal) || url.EndsWith("/reopen", StringComparison.Ordinal) || url.EndsWith("/move", StringComparison.Ordinal) || method == HttpMethod.Delete ? ""
                    : TaskJson(7)),
            };
            (HttpMethod, string, string?) Last() => (todoist.Requests[^1].Method, todoist.Requests[^1].Url, todoist.Requests[^1].Body);

            Assert.Equal("Buy milk 7", Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.GetTaskOperation, new() { ["task"] = "https://app.todoist.com/app/task/buy-milk-6Jf8VQXxpwv50007" }))).Fields["content"]);
            Assert.Equal((HttpMethod.Get, Api + "/tasks/6Jf8VQXxpwv50007", (string?)null), Last()); // a task's address cut to its id

            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.CreateTaskOperation, new()
            {
                ["content"] = "Buy milk", ["description"] = "Café <b>full</b> & fresh", ["project"] = "6Jf8VQXxpwv56VQ7", ["section"] = "s1", ["labels"] = "shop, errands", ["priority"] = "urgent",
                ["due_datetime"] = "2026-10-31T17:00:00+02:00", ["due_lang"] = "en", ["assignee"] = "2671355", ["duration"] = "30", ["deadline"] = "2026-11-02",
            }));
            Assert.Equal((HttpMethod.Post, Api + "/tasks", "{\"content\":\"Buy milk\",\"description\":\"Café <b>full</b> & fresh\",\"labels\":[\"shop\",\"errands\"],\"priority\":4,\"due_datetime\":\"2026-10-31T15:00:00Z\",\"due_lang\":\"en\",\"assignee_id\":\"2671355\",\"duration\":30,\"duration_unit\":\"minute\",\"deadline_date\":\"2026-11-02\",\"project_id\":\"6Jf8VQXxpwv56VQ7\",\"section_id\":\"s1\"}"), Last());
            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.CreateTaskOperation, new() { ["content"] = "Call Ada", ["due_string"] = "tomorrow at 5pm", ["priority"] = "2", ["duration"] = "1", ["duration_unit"] = "Day" }));
            Assert.Equal("{\"content\":\"Call Ada\",\"priority\":2,\"due_string\":\"tomorrow at 5pm\",\"duration\":1,\"duration_unit\":\"day\"}", Last().Item3); // the Inbox; a priority's number; a day's duration
            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.CreateTaskOperation, new() { ["content"] = "Pay rent", ["due_date"] = "2026-11-01", ["parent"] = "p_1" }));
            Assert.Equal("{\"content\":\"Pay rent\",\"due_date\":\"2026-11-01\",\"parent_id\":\"p_1\"}", Last().Item3);

            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.QuickAddOperation, new() { ["text"] = "Buy milk tomorrow #Errands @shop p1", ["note"] = "Oat, please", ["reminder"] = "tomorrow 9am", ["auto_reminder"] = "true" }));
            Assert.Equal((HttpMethod.Post, Api + "/tasks/quick", "{\"text\":\"Buy milk tomorrow #Errands @shop p1\",\"meta\":true,\"note\":\"Oat, please\",\"reminder\":\"tomorrow 9am\",\"auto_reminder\":true}"), Last());
            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.QuickAddOperation, new() { ["text"] = "Call Ada" }));
            Assert.Equal("{\"text\":\"Call Ada\",\"meta\":true,\"auto_reminder\":false}", Last().Item3);

            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.UpdateTaskOperation, new() { ["task"] = "t1", ["content"] = "Buy oat milk", ["priority"] = "normal", ["labels"] = "shop" }));
            Assert.Equal((HttpMethod.Post, Api + "/tasks/t1", "{\"content\":\"Buy oat milk\",\"labels\":[\"shop\"],\"priority\":1}"), Last());

            var closed = Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.CloseTaskOperation, new() { ["task"] = "t1" })));
            Assert.Equal(("t1", true), ((string?)closed.Fields["id"], (bool?)closed.Fields["completed"]));
            Assert.Equal((HttpMethod.Post, Api + "/tasks/t1/close", (string?)null), Last());
            Assert.True((bool?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.ReopenTaskOperation, new() { ["task"] = "t1" }))).Fields["reopened"]);
            Assert.Equal((HttpMethod.Post, Api + "/tasks/t1/reopen", (string?)null), Last());
            var moved = Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.MoveTaskOperation, new() { ["task"] = "t1", ["section"] = "s2" })));
            Assert.Equal((HttpMethod.Post, Api + "/tasks/t1/move", "{\"section_id\":\"s2\"}"), Last());
            Assert.Equal("s2", moved.Fields["section_id"]);
            await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.MoveTaskOperation, new() { ["task"] = "t1", ["project"] = "https://app.todoist.com/app/project/work-6Jf8VQXxpwv56VQ8" }));
            Assert.Equal("{\"project_id\":\"6Jf8VQXxpwv56VQ8\"}", Last().Item3);
            Assert.True((bool?)Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.DeleteTaskOperation, new() { ["task"] = "t1" }))).Fields["deleted"]);
            Assert.Equal((HttpMethod.Delete, Api + "/tasks/t1", (string?)null), Last());
            var comment = Assert.Single(await Contexts.DrainAsync(Connector(), Context(todoist, TodoistConnector.AddCommentOperation, new() { ["task"] = "t1", ["content"] = "On it.\n**Soon.**" })));
            Assert.Equal((HttpMethod.Post, Api + "/comments", "{\"task_id\":\"t1\",\"content\":\"On it.\\n**Soon.**\"}"), Last());
            Assert.Equal(("c1", "t1"), ((string?)comment.Fields["id"], (string?)comment.Fields["task_id"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_todoists_refusal_is_its_words()
        {
            var todoist = new TodoistApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://api.todoist.com/api/v1") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(todoist, operation, parameters, endpoint)))).Message;

            Assert.Equal("'my list' is not a Todoist project's id (letters, digits, - and _) or its address.", await Refused(TodoistConnector.TasksOperation, new() { ["project"] = "my list" }));
            Assert.Equal("'s 1' is not a Todoist section's id (letters, digits, - and _).", await Refused(TodoistConnector.TasksOperation, new() { ["section"] = "s 1" }));
            Assert.Equal("'x/y' is not a Todoist task's id (letters, digits, - and _) or its address.", await Refused(TodoistConnector.TasksOperation, new() { ["ids"] = "a1, x/y" }));
            Assert.Equal("The label has a line break - one line only.", await Refused(TodoistConnector.TasksOperation, new() { ["label"] = "a\nb" }));
            Assert.Equal("A filter reads on its own - give the filter or 'project', not both (#Work in the filter names a project).", await Refused(TodoistConnector.TasksOperation, new() { ["filter"] = "today", ["project"] = "p1" }));
            Assert.Equal("'English' is not a language - two letters, en.", await Refused(TodoistConnector.TasksOperation, new() { ["filter"] = "today", ["lang"] = "English" }));
            Assert.Equal("The filter has a line break - one line only.", await Refused(TodoistConnector.TasksOperation, new() { ["filter"] = "today\n| overdue" }));
            Assert.Equal("Give the task's content.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["description"] = "x" }));
            Assert.Equal("The content has a line break - one line only.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "a\nb" }));
            Assert.Equal("'asap' is not a priority - 4 urgent, 3 high, 2 medium or 1 normal.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["priority"] = "asap" }));
            Assert.Equal("Give one due date - due_string or due_date, not both.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["due_string"] = "tomorrow", ["due_date"] = "2026-10-31" }));
            Assert.Equal("'31/10/2026' is not a day for the due day - 2026-10-31.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["due_date"] = "31/10/2026" }));
            Assert.Equal("'2026-10-31' is not a time - 2026-10-31T17:00:00+02:00.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["due_datetime"] = "2026-10-31" }));
            Assert.Equal("'soon' is not a time - 2026-10-31T17:00:00+02:00.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["due_datetime"] = "soon" }));
            Assert.Equal("'half' is not a duration - a whole number above 0.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["duration"] = "half" }));
            Assert.Equal("'0' is not a duration - a whole number above 0.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["duration"] = "0" }));
            Assert.Equal("'English' is not a language - two letters, en.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["due_string"] = "morgen", ["due_lang"] = "English" }));
            Assert.Equal("'hour' is not a duration unit - minute or day.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["duration"] = "2", ["duration_unit"] = "hour" }));
            Assert.Equal("'2026-02-30' is not a day for the deadline - 2026-10-31.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["deadline"] = "2026-02-30" }));
            Assert.Equal("'Ada Lovelace' is not a Todoist assignee's id (letters, digits, - and _).", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["assignee"] = "Ada Lovelace" }));
            Assert.Equal("The label has a line break - one line only.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["labels"] = "a\nb, c" }));
            Assert.Equal("'my list' is not a Todoist project's id (letters, digits, - and _) or its address.", await Refused(TodoistConnector.CreateTaskOperation, new() { ["content"] = "x", ["project"] = "my list" }));
            Assert.Equal("Give the line to add - Buy milk tomorrow #Errands.", await Refused(TodoistConnector.QuickAddOperation, new()));
            Assert.Equal("The text has a line break - one line only.", await Refused(TodoistConnector.QuickAddOperation, new() { ["text"] = "a\nb" }));
            Assert.Equal("Give something to change - content, description, labels, priority, a due date, an assignee, a duration or a deadline.", await Refused(TodoistConnector.UpdateTaskOperation, new() { ["task"] = "t1" }));
            Assert.Equal("'' is not a Todoist task's id (letters, digits, - and _) or its address.", await Refused(TodoistConnector.CloseTaskOperation, new()));
            Assert.Equal("Move it to one place - a project, a section or a parent task.", await Refused(TodoistConnector.MoveTaskOperation, new() { ["task"] = "t1" }));
            Assert.Equal("Move it to one place - a project, a section or a parent task.", await Refused(TodoistConnector.MoveTaskOperation, new() { ["task"] = "t1", ["project"] = "p1", ["section"] = "s1" }));
            Assert.Equal("Give the comment's text.", await Refused(TodoistConnector.AddCommentOperation, new() { ["task"] = "t1" }));
            Assert.StartsWith("Todoist has no operation 'archive_task' - tasks, get_task,", await Refused("archive_task", new() { ["task"] = "t1" }));
            Assert.Equal("'ftp://x.example' is not Todoist's API address - https://api.todoist.com/api/v1.", await Refused(TodoistConnector.ProjectsOperation, new(), "ftp://x.example"));
            Assert.Empty(todoist.Requests);

            var refusing = new TodoistApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"error\":\"Invalid argument value\",\"error_code\":20,\"error_extra\":{\"argument\":\"priority\"},\"error_tag\":\"INVALID_ARGUMENT_VALUE\",\"http_code\":400}") };
            Assert.Equal("HTTP 400: Invalid argument value (INVALID_ARGUMENT_VALUE)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, TodoistConnector.GetTaskOperation, new() { ["task"] = "t1" })))).Message);
            var plain = new TodoistApi { Answer = (_, _) => (HttpStatusCode.NotFound, "Task not found") };
            Assert.Equal("HTTP 404: Task not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(plain, TodoistConnector.DeleteTaskOperation, new() { ["task"] = "t1" })))).Message);
            var empty = new TodoistApi { Answer = (_, _) => (HttpStatusCode.Forbidden, "") };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(empty, TodoistConnector.ProjectsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var todoist = new TodoistApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":\"2671355\",\"full_name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(todoist), "https://api.todoist.com/api/v1", credentials: new Dictionary<string, string> { ["token"] = Token }));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/user", "Bearer " + Token), (todoist.Requests[0].Url, todoist.Requests[0].Authorization));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new TodoistApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\",\"error_code\":477,\"error_tag\":\"UNAUTHORIZED\",\"http_code\":401}") }), "https://api.todoist.com/api/v1", credentials: new Dictionary<string, string> { ["token"] = Token }));
            Assert.Equal((false, "HTTP 401: Unauthorized (UNAUTHORIZED)"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "todoist", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "tasks", "get_task", "create_task", "quick_add", "update_task", "close_task", "reopen_task", "move_task", "delete_task", "add_comment", "projects" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
