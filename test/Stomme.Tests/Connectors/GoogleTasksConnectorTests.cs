#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.GoogleTasks;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Google Tasks (n8n's Google Tasks node: a task created, got, listed, updated and deleted - and moved, the lists read,
    /// a list's finished ones cleared): the refresh token exchanged at the token endpoint, the access token sent as a bearer; a
    /// list's tasks page by page by Google's token up to the most asked for, a page no bigger than what is left, the finished ones
    /// too by default and n8n's other defaults, times in UTC; each task a plain record; the lists; a task made under a parent after
    /// a sibling, changed, finished and reopened, moved, deleted; a list cleared; what could go wrong refused before anything is
    /// sent - not even a token asked for; Google's refusal its status word and message; a test naming the lists.
    /// </summary>
    public sealed class GoogleTasksConnectorTests
    {
        private const string Api = "https://tasks.example.test/tasks/v1";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = "secret-1", ["refresh-token"] = "refresh-1" };

        private sealed class Google : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Tasks { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                if (url.StartsWith("https://oauth2.example.test/token", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
                }

                var (status, answer) = Tasks(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> TaskRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://oauth2.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Google google, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(google), Api, operation, parameters,
                settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials);

        private static GoogleTasksConnector Connector() => new(new OAuth2TokenProvider());

        private static string TaskJson(int n, string status = "needsAction") =>
            "{\"id\":\"t" + n + "\",\"title\":\"Buy milk " + n + "\",\"notes\":\"Café <b>oat</b> & more\",\"status\":\"" + status + "\",\"due\":\"2026-10-31T00:00:00.000Z\","
            + (status == "completed" ? "\"completed\":\"2026-10-12T09:00:00.000Z\"," : "") + "\"parent\":\"p1\",\"position\":\"00000000000000000001\",\"links\":[{\"type\":\"email\",\"link\":\"https://mail.google.com/x\"}],"
            + "\"webViewLink\":\"https://tasks.google.com/task/t" + n + "\",\"updated\":\"2026-10-02T10:00:00.000Z\"}";

        private static string Page(int from, int count, string? next) =>
            "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => TaskJson(n))) + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";

        [Fact]
        public async Task A_lists_tasks_are_read_page_by_page_up_to_the_most_asked_for_by_the_exchanged_token()
        {
            var google = new Google { Tasks = (_, url) => (HttpStatusCode.OK, url.Contains("pageToken=p%2B2", StringComparison.Ordinal) ? Page(101, 100, null) : Page(1, 100, "p+2")) };
            var records = await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.TasksOperation, new()
            {
                ["list"] = "MDE2NzY5", ["show_completed"] = "False", ["show_hidden"] = "true", ["due_min"] = "2026-10-01", ["due_max"] = "2026-10-31T17:00:00+02:00", ["updated_min"] = "2026-10-01T09:00:00", ["max"] = "150",
            }));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal("https://oauth2.example.test/token", google.Requests[0].Url); // the token first
            Assert.All(google.TaskRequests, request => Assert.Equal("Bearer access-1", request.Authorization));
            const string query = "showCompleted=false&showDeleted=false&showHidden=true&dueMin=2026-10-01T00%3A00%3A00Z&dueMax=2026-10-31T15%3A00%3A00Z&updatedMin=2026-10-01T09%3A00%3A00Z";
            Assert.Equal(new[] { Api + "/lists/MDE2NzY5/tasks?" + query + "&maxResults=100", Api + "/lists/MDE2NzY5/tasks?" + query + "&maxResults=50&pageToken=p%2B2" }, google.TaskRequests.Select(request => request.Url));
            var task = records[0].Fields;
            Assert.Equal(("t1", "Buy milk 1", "Café <b>oat</b> & more", "needsAction", false, "2026-10-31", "p1", "https://mail.google.com/x", "https://tasks.google.com/task/t1"),
                ((string?)task["id"], (string?)task["title"], (string?)task["notes"], (string?)task["status"], (bool?)task["completed"], (string?)task["due"], (string?)task["parent"], (string?)task["links"], (string?)task["url"]));

            var defaults = new Google { Tasks = (_, _) => (HttpStatusCode.OK, Page(1, 3, null)) };
            Assert.Equal(3, (await Contexts.DrainAsync(Connector(), Context(defaults, GoogleTasksConnector.TasksOperation, new()))).Count);
            Assert.Equal(Api + "/lists/@default/tasks?showCompleted=true&showDeleted=false&showHidden=false&maxResults=100", Assert.Single(defaults.TaskRequests).Url); // the user's own list; n8n's defaults; no token, the last

            var ten = new Google { Tasks = (_, _) => (HttpStatusCode.OK, Page(1, 100, "more")) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, GoogleTasksConnector.TasksOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(10, ten.TaskRequests.Count);

            var lists = new Google { Tasks = (_, _) => (HttpStatusCode.OK, "{\"items\":[{\"id\":\"MDE2NzY5\",\"title\":\"My Tasks\",\"updated\":\"2026-10-02T10:00:00.000Z\"},{\"id\":\"QWx0\",\"title\":\"Errands\"}]}") };
            var read = await Contexts.DrainAsync(Connector(), Context(lists, GoogleTasksConnector.TaskListsOperation, new()));
            Assert.Equal(new[] { "My Tasks", "Errands" }, read.Select(list => (string?)list.Fields["title"]));
            Assert.Equal(new[] { "id", "title", "updated" }, read[0].Fields.Keys); // a list's own record, not a task's
            Assert.Equal(Api + "/users/@me/lists?maxResults=100", Assert.Single(lists.TaskRequests).Url);
        }

        [Fact]
        public async Task A_task_is_made_got_changed_finished_moved_and_deleted_and_a_list_cleared()
        {
            var google = new Google { Tasks = (method, _) => (method == HttpMethod.Delete || method == HttpMethod.Post ? HttpStatusCode.OK : HttpStatusCode.OK, method == HttpMethod.Delete ? "" : TaskJson(7, "completed")) };
            (HttpMethod, string, string?) Last() => (google.TaskRequests[^1].Method, google.TaskRequests[^1].Url, google.TaskRequests[^1].Body);

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.CreateTaskOperation, new()
            {
                ["title"] = "Buy milk", ["notes"] = "Café <b>oat</b>\n& more", ["due"] = "2026-10-31T23:30:00-02:00", ["parent"] = "p1", ["previous"] = "t6",
            })));
            Assert.Equal((HttpMethod.Post, Api + "/lists/@default/tasks?parent=p1&previous=t6", "{\"title\":\"Buy milk\",\"notes\":\"Café <b>oat</b>\\n& more\",\"due\":\"2026-11-01T00:00:00.000Z\"}"), Last()); // the day in UTC
            Assert.Equal((true, "2026-10-12T09:00:00.000Z"), ((bool?)made.Fields["completed"], (string?)made.Fields["completed_at"]));
            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.CreateTaskOperation, new() { ["list"] = "QWx0", ["title"] = "Done already", ["completed"] = "true" }));
            Assert.Equal((HttpMethod.Post, Api + "/lists/QWx0/tasks", "{\"title\":\"Done already\",\"status\":\"completed\"}"), Last());

            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.GetTaskOperation, new() { ["task"] = "t7" }));
            Assert.Equal((HttpMethod.Get, Api + "/lists/@default/tasks/t7", (string?)null), Last());
            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.UpdateTaskOperation, new() { ["task"] = "t7", ["title"] = "Buy oat milk", ["due"] = "2026-11-02" }));
            Assert.Equal(("PATCH", Api + "/lists/@default/tasks/t7", "{\"title\":\"Buy oat milk\",\"due\":\"2026-11-02T00:00:00.000Z\"}"), (Last().Item1.Method, Last().Item2, Last().Item3));
            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.UpdateTaskOperation, new() { ["task"] = "t7", ["completed"] = "FALSE" }));
            Assert.Equal("{\"status\":\"needsAction\",\"completed\":null}", Last().Item3); // to do again: when it was finished forgotten

            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.MoveTaskOperation, new() { ["task"] = "t7", ["parent"] = "p2", ["previous"] = "t3", ["to_list"] = "QWx0" }));
            Assert.Equal((HttpMethod.Post, Api + "/lists/@default/tasks/t7/move?parent=p2&previous=t3&destinationTasklist=QWx0", (string?)null), Last());
            await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.MoveTaskOperation, new() { ["task"] = "t7" }));
            Assert.Equal(Api + "/lists/@default/tasks/t7/move", Last().Item2); // to the top

            var gone = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.DeleteTaskOperation, new() { ["task"] = "t7" })));
            Assert.Equal((HttpMethod.Delete, Api + "/lists/@default/tasks/t7", (string?)null), Last());
            Assert.Equal(("t7", true), ((string?)gone.Fields["id"], (bool?)gone.Fields["deleted"]));
            var cleared = Assert.Single(await Contexts.DrainAsync(Connector(), Context(google, GoogleTasksConnector.ClearCompletedOperation, new() { ["list"] = "QWx0" })));
            Assert.Equal((HttpMethod.Post, Api + "/lists/QWx0/clear", (string?)null), Last());
            Assert.True((bool?)cleared.Fields["cleared"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_googles_refusal_is_its_words()
        {
            var google = new Google();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(google, operation, parameters)))).Message;

            Assert.Equal("'my list' is not a task list's id.", await Refused(GoogleTasksConnector.TasksOperation, new() { ["list"] = "my list" }));
            Assert.Equal("'yes' is not true or false (show completed).", await Refused(GoogleTasksConnector.TasksOperation, new() { ["show_completed"] = "yes" }));
            Assert.Equal("'1' is not true or false (show deleted).", await Refused(GoogleTasksConnector.TasksOperation, new() { ["show_deleted"] = "1" }));
            Assert.Equal("'soon' is not a time (due min) - 2026-10-01, or 2026-10-01T09:00:00+02:00.", await Refused(GoogleTasksConnector.TasksOperation, new() { ["due_min"] = "soon" }));
            Assert.Equal("'t 7' is not a task's id.", await Refused(GoogleTasksConnector.GetTaskOperation, new() { ["task"] = "t 7" }));
            Assert.Equal("'' is not a task's id.", await Refused(GoogleTasksConnector.DeleteTaskOperation, new()));
            Assert.Equal("Give the task's title.", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["notes"] = "x" }));
            Assert.Equal("The title has a line break - one line only.", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["title"] = "a\nb" }));
            Assert.Equal("'31/10/2026' is not a day - 2026-10-31.", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["title"] = "x", ["due"] = "31/10/2026" }));
            Assert.Equal("'2026-02-30' is not a day - 2026-10-31.", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["title"] = "x", ["due"] = "2026-02-30" }));
            Assert.Equal("'done' is not true or false (completed).", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["title"] = "x", ["completed"] = "done" }));
            Assert.Equal("'p 1' is not a parent task's id.", await Refused(GoogleTasksConnector.CreateTaskOperation, new() { ["title"] = "x", ["parent"] = "p 1" }));
            Assert.Equal("'t/6' is not a sibling's id.", await Refused(GoogleTasksConnector.MoveTaskOperation, new() { ["task"] = "t7", ["previous"] = "t/6" }));
            Assert.Equal("'other list' is not a task list's id.", await Refused(GoogleTasksConnector.MoveTaskOperation, new() { ["task"] = "t7", ["to_list"] = "other list" }));
            Assert.Equal("Give something to change - a title, notes, a due day or completed.", await Refused(GoogleTasksConnector.UpdateTaskOperation, new() { ["task"] = "t7" }));
            Assert.StartsWith("Google Tasks has no operation 'archive_task' - task_lists, tasks,", await Refused("archive_task", new() { ["task"] = "t7" }));
            Assert.Empty(google.Requests); // not even a token asked for

            var refusing = new Google { Tasks = (_, _) => (HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"Task list not found.\",\"status\":\"NOT_FOUND\"}}") };
            Assert.Equal("HTTP 404 NOT_FOUND: Task list not found.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, GoogleTasksConnector.TasksOperation, new() { ["list"] = "QWx0" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_lists()
        {
            var google = new Google { Tasks = (_, _) => (HttpStatusCode.OK, "{\"items\":[{\"id\":\"MDE2NzY5\",\"title\":\"My Tasks\"},{\"id\":\"QWx0\",\"title\":\"Errands\"}]}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(google), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in - 2 task lists, My Tasks first."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/users/@me/lists?maxResults=100", "Bearer access-1"), (google.TaskRequests[0].Url, google.TaskRequests[0].Authorization));
            var none = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Google { Tasks = (_, _) => (HttpStatusCode.OK, "{}") }), Api, settings: new Dictionary<string, string> { ["token_url"] = "https://oauth2.example.test/token" }, credentials: Credentials));
            Assert.Equal((true, "Signed in - no task lists yet."), (none.Success, none.Message));
            Assert.Equal((AuthenticationType.OAuth2RefreshToken, "googletasks", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "task_lists", "tasks", "get_task", "create_task", "update_task", "move_task", "delete_task", "clear_completed" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
