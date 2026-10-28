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
using Stomme.Modules.Connectors.MicrosoftToDo;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Microsoft To Do (n8n's Microsoft To Do node: its lists and tasks created, got, read, updated and deleted): an
    /// app-only token from the tenant's token endpoint, sent as a bearer, acting as the one user the connection names; a list's tasks
    /// read through Graph's next links - never one that leaves the user's address - up to the most asked for, a filter passed on,
    /// each task a plain record, its times in UTC; the lists, each a list's own record; a list made, got, renamed and deleted; a task
    /// made with its body, due time, reminder, importance, status and categories, got, changed and deleted; what could go wrong
    /// refused before anything is sent - not even a token asked for; Graph's refusal its code and message; a test naming the lists.
    /// </summary>
    public sealed class MicrosoftToDoConnectorTests
    {
        private const string Me = "https://graph.example.test/v1.0/users/ada%40contoso.com";

        private const string Tasks = Me + "/todo/lists/AAMk%2Fl1%3D/tasks";

        private static readonly Dictionary<string, string> Credentials = new() { ["client-id"] = "app-1", ["client-secret"] = "secret-1", ["token-url"] = "https://login.example.test/tenant/oauth2/v2.0/token" };

        /// <summary>Microsoft as a test sees it: the token endpoint, then Graph, each request recorded with its body.</summary>
        private sealed class Microsoft : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode, string)> Graph { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = url.StartsWith("https://login.example.test/", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""")
                    : Graph(request.Method, url);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }

            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> GraphRequests =>
                Requests.Where(request => !request.Url.StartsWith("https://login.example.test/", StringComparison.Ordinal)).ToList();
        }

        private static ConnectorExtractContext Context(Microsoft microsoft, string operation, Dictionary<string, string> parameters, string? user = "ada@contoso.com") =>
            Contexts.Extract(new StubEgress(microsoft), "https://graph.example.test/v1.0", operation, parameters,
                settings: user is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["user"] = user }, credentials: Credentials);

        private static MicrosoftToDoConnector Connector() => new(new OAuth2TokenProvider());

        private static string TaskJson(int n, string zone = "UTC", bool reminder = true) =>
            "{\"id\":\"AAMk-t" + n + "\",\"title\":\"Buy milk " + n + "\",\"body\":{\"content\":\"Café <b>oat</b> & more\",\"contentType\":\"html\"},\"status\":\"notStarted\",\"importance\":\"high\","
            + "\"dueDateTime\":{\"dateTime\":\"2026-10-31T00:00:00.0000000\",\"timeZone\":\"" + zone + "\"},\"isReminderOn\":" + (reminder ? "true" : "false") + ","
            + "\"reminderDateTime\":{\"dateTime\":\"2026-10-30T07:00:00.0000000\",\"timeZone\":\"UTC\"},\"categories\":[\"Errands\",\"Home\"],"
            + "\"createdDateTime\":\"2026-10-01T09:00:00Z\",\"lastModifiedDateTime\":\"2026-10-02T10:00:00Z\"}";

        private static string Page(int from, int count, string? next) =>
            "{\"value\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => TaskJson(n))) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";

        [Fact]
        public async Task A_lists_tasks_are_read_through_graphs_next_links_up_to_the_most_asked_for_by_an_app_only_token()
        {
            var microsoft = new Microsoft
            {
                Graph = (_, url) => (HttpStatusCode.OK, url.Contains("skiptoken", StringComparison.Ordinal) ? Page(101, 3, "https://evil.example/steal") : Page(1, 100, Tasks + "?$top=100&$skiptoken=p2")),
            };
            var records = await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.TasksOperation, new() { ["list"] = "AAMk/l1=", ["filter"] = "status ne 'completed'", ["max"] = "150" }));

            var token = microsoft.Requests[0];
            Assert.Equal((HttpMethod.Post, "https://login.example.test/tenant/oauth2/v2.0/token"), (token.Method, token.Url));
            Assert.Contains("grant_type=client_credentials", token.Body);
            Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", token.Body);
            Assert.Equal(new[] { Tasks + "?$top=100&$filter=status%20ne%20%27completed%27", Tasks + "?$top=100&$skiptoken=p2" }, microsoft.GraphRequests.Select(request => request.Url)); // the next link that leaves the user's address never followed
            Assert.All(microsoft.GraphRequests, request => Assert.Equal("Bearer access-1", request.Authorization));
            Assert.Equal(103, records.Count);
            var task = records[0].Fields;
            Assert.Equal(("AAMk-t1", "Buy milk 1", "Café <b>oat</b> & more", "notStarted", "high", "2026-10-31T00:00:00Z", "2026-10-30T07:00:00Z", (string?)null, "Errands, Home"),
                ((string?)task["id"], (string?)task["title"], (string?)task["body"], (string?)task["status"], (string?)task["importance"], (string?)task["due"], (string?)task["reminder"], (string?)task["completed_at"], (string?)task["categories"]));
            Assert.Equal(("2026-10-01T09:00:00Z", "2026-10-02T10:00:00Z"), ((string?)task["created"], (string?)task["updated"]));

            var capped = new Microsoft { Graph = microsoft.Graph };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(capped, MicrosoftToDoConnector.TasksOperation, new() { ["list"] = "AAMk/l1=", ["max"] = "2" }))).Count);
            Assert.Equal(Tasks + "?$top=2", Assert.Single(capped.GraphRequests).Url); // a page no bigger than asked for; no filter

            var ten = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, Page(1, 100, Tasks + "?$skiptoken=more")) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, MicrosoftToDoConnector.TasksOperation, new() { ["list"] = "AAMk/l1=" }))).Count); // by default up to 1,000
            Assert.Equal(10, ten.GraphRequests.Count);

            var zoned = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, "{\"value\":[" + TaskJson(1, "W. Europe Standard Time", reminder: false) + "]}") };
            var other = Assert.Single(await Contexts.DrainAsync(Connector(), Context(zoned, MicrosoftToDoConnector.TasksOperation, new() { ["list"] = "AAMk/l1=" }))).Fields;
            Assert.Equal(("2026-10-31T00:00:00.0000000 (W. Europe Standard Time)", (string?)null), ((string?)other["due"], (string?)other["reminder"])); // another zone as Graph wrote it; a reminder that is off none

            var lists = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"value":[{"id":"AAMk-l1","displayName":"Tasks","isOwner":true,"isShared":false,"wellknownListName":"defaultList"},{"id":"AAMk-l2","displayName":"Errands","isOwner":false,"isShared":true,"wellknownListName":"none"}]}""") };
            var read = await Contexts.DrainAsync(Connector(), Context(lists, MicrosoftToDoConnector.ListsOperation, new()));
            Assert.Equal(new[] { "Tasks", "Errands" }, read.Select(list => (string?)list.Fields["name"]));
            Assert.Equal(new[] { "id", "name", "owner", "shared", "well_known" }, read[0].Fields.Keys); // a list's own record, not a task's
            Assert.Equal((true, false, "defaultList"), ((bool?)read[0].Fields["owner"], (bool?)read[0].Fields["shared"], (string?)read[0].Fields["well_known"]));
            Assert.Equal((false, true), ((bool?)read[1].Fields["owner"], (bool?)read[1].Fields["shared"]));
            Assert.Equal(Me + "/todo/lists?$top=100", Assert.Single(lists.GraphRequests).Url);
        }

        [Fact]
        public async Task A_list_is_made_got_renamed_and_deleted_and_a_task_made_got_changed_and_deleted()
        {
            var finishedTask = TaskJson(7).Replace("\"status\":\"notStarted\"", "\"status\":\"completed\",\"completedDateTime\":{\"dateTime\":\"2026-10-12T09:30:00.0000000\",\"timeZone\":\"UTC\"}", StringComparison.Ordinal);
            var microsoft = new Microsoft
            {
                Graph = (method, url) => (method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, method == HttpMethod.Delete ? ""
                    : !url.Contains("/tasks", StringComparison.Ordinal) ? """{"id":"AAMk-l1","displayName":"Errands & more"}"""
                    : method == HttpMethod.Patch ? finishedTask : TaskJson(7)),
            };
            (string, string, string?) Last() => (microsoft.GraphRequests[^1].Method.Method, microsoft.GraphRequests[^1].Url, microsoft.GraphRequests[^1].Body);

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.CreateListOperation, new() { ["name"] = " Errands & more " })));
            Assert.Equal(("POST", Me + "/todo/lists", "{\"displayName\":\"Errands & more\"}"), Last());
            Assert.Equal(("AAMk-l1", "Errands & more"), ((string?)made.Fields["id"], (string?)made.Fields["name"]));
            await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.GetListOperation, new() { ["list"] = "AAMk/l1=" }));
            Assert.Equal(("GET", Me + "/todo/lists/AAMk%2Fl1%3D", (string?)null), Last());
            await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.RenameListOperation, new() { ["list"] = "AAMk/l1=", ["name"] = "Chores" }));
            Assert.Equal(("PATCH", Me + "/todo/lists/AAMk%2Fl1%3D", "{\"displayName\":\"Chores\"}"), Last());
            var dropped = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.DeleteListOperation, new() { ["list"] = "AAMk/l1=" })));
            Assert.Equal(("DELETE", Me + "/todo/lists/AAMk%2Fl1%3D", (string?)null), Last());
            Assert.Equal(("AAMk/l1=", true), ((string?)dropped.Fields["id"], (bool?)dropped.Fields["deleted"]));

            var task = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.CreateTaskOperation, new()
            {
                ["list"] = "AAMk/l1=", ["title"] = "Buy milk", ["body"] = "Café <b>oat</b>\n& more", ["html"] = "TRUE", ["due"] = "2026-11-01T00:00:00+02:00", ["reminder"] = "2026-10-30",
                ["importance"] = "HIGH", ["status"] = "inprogress", ["categories"] = "Errands, , Home",
            })));
            Assert.Equal(("POST", Tasks, "{\"title\":\"Buy milk\",\"body\":{\"content\":\"Café <b>oat</b>\\n& more\",\"contentType\":\"html\"},\"dueDateTime\":{\"dateTime\":\"2026-10-31T22:00:00\",\"timeZone\":\"UTC\"},"
                + "\"reminderDateTime\":{\"dateTime\":\"2026-10-30T00:00:00\",\"timeZone\":\"UTC\"},\"isReminderOn\":true,\"importance\":\"high\",\"status\":\"inProgress\",\"categories\":[\"Errands\",\"Home\"]}"), Last()); // in UTC; the words as Graph spells them
            Assert.Equal("AAMk-t7", (string?)task.Fields["id"]);
            await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.CreateTaskOperation, new() { ["list"] = "AAMk/l1=", ["title"] = "Call Ada", ["body"] = "About <the> order", ["due"] = "2026-10-31T17:00:00" }));
            Assert.Equal("{\"title\":\"Call Ada\",\"body\":{\"content\":\"About <the> order\",\"contentType\":\"text\"},\"dueDateTime\":{\"dateTime\":\"2026-10-31T17:00:00\",\"timeZone\":\"UTC\"}}", Last().Item3); // text by default; a time without an offset taken as UTC

            await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.GetTaskOperation, new() { ["list"] = "AAMk/l1=", ["task"] = "AAMk+t7=" }));
            Assert.Equal(("GET", Tasks + "/AAMk%2Bt7%3D", (string?)null), Last());
            var finished = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.UpdateTaskOperation, new() { ["list"] = "AAMk/l1=", ["task"] = "AAMk+t7=", ["status"] = "Completed" })));
            Assert.Equal(("PATCH", Tasks + "/AAMk%2Bt7%3D", "{\"status\":\"completed\"}"), Last());
            Assert.Equal(("completed", "2026-10-12T09:30:00Z"), ((string?)finished.Fields["status"], (string?)finished.Fields["completed_at"])); // when it was finished, in UTC
            var gone = Assert.Single(await Contexts.DrainAsync(Connector(), Context(microsoft, MicrosoftToDoConnector.DeleteTaskOperation, new() { ["list"] = "AAMk/l1=", ["task"] = "AAMk+t7=" })));
            Assert.Equal(("DELETE", Tasks + "/AAMk%2Bt7%3D", (string?)null), Last());
            Assert.Equal(("AAMk+t7=", true), ((string?)gone.Fields["id"], (bool?)gone.Fields["deleted"]));
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_graphs_refusal_is_its_words()
        {
            var microsoft = new Microsoft();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string? user = "ada@contoso.com") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(microsoft, operation, parameters, user)))).Message;
            var list = new Dictionary<string, string> { ["list"] = "AAMk-l1" };
            Dictionary<string, string> With(params (string Key, string Value)[] more) => list.Concat(more.Select(pair => KeyValuePair.Create(pair.Key, pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value);

            Assert.Equal("Name the user whose To Do the app acts on - the connection's user setting.", await Refused(MicrosoftToDoConnector.ListsOperation, new(), user: null));
            Assert.Equal("'ada lovelace' is not a user - an address or a user id.", await Refused(MicrosoftToDoConnector.ListsOperation, new(), user: "ada lovelace"));
            Assert.Equal("'ada/../x@contoso.com' is not a user - an address or a user id.", await Refused(MicrosoftToDoConnector.ListsOperation, new(), user: "ada/../x@contoso.com"));
            Assert.Equal("'my list' is not a list's id.", await Refused(MicrosoftToDoConnector.TasksOperation, new() { ["list"] = "my list" }));
            Assert.Equal("'' is not a list's id.", await Refused(MicrosoftToDoConnector.GetListOperation, new()));
            Assert.Equal("'t 7' is not a task's id.", await Refused(MicrosoftToDoConnector.GetTaskOperation, With(("task", "t 7"))));
            Assert.Equal("'' is not a task's id.", await Refused(MicrosoftToDoConnector.DeleteTaskOperation, list));
            Assert.Equal("Give the list's name.", await Refused(MicrosoftToDoConnector.CreateListOperation, new()));
            Assert.Equal("The name has a line break - one line only.", await Refused(MicrosoftToDoConnector.RenameListOperation, With(("name", "a\nb"))));
            Assert.Equal("The filter has a line break - one line only.", await Refused(MicrosoftToDoConnector.TasksOperation, With(("filter", "status eq 'a'\r\nor true"))));
            Assert.Equal("Give the task's title.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("body", "x"))));
            Assert.Equal("The title has a line break - one line only.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "a\nb"))));
            Assert.Equal("'soon' is not a time (due) - 2026-10-31, or 2026-10-31T17:00:00+02:00.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "x"), ("due", "soon"))));
            Assert.Equal("'later' is not a time (reminder) - 2026-10-31, or 2026-10-31T17:00:00+02:00.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "x"), ("reminder", "later"))));
            Assert.Equal("'urgent' is not an importance - low, normal or high.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "x"), ("importance", "urgent"))));
            Assert.Equal("'done' is not a status - notStarted, inProgress, completed, waitingOnOthers or deferred.", await Refused(MicrosoftToDoConnector.UpdateTaskOperation, With(("task", "t7"), ("status", "done"))));
            Assert.Equal("'yes' is not true or false (html).", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "x"), ("html", "yes"))));
            Assert.Equal("The category has a line break - one line only.", await Refused(MicrosoftToDoConnector.CreateTaskOperation, With(("title", "x"), ("categories", "a\nb, c"))));
            Assert.Equal("Give something to change - a title, a body, a due time, a reminder, an importance, a status or categories.", await Refused(MicrosoftToDoConnector.UpdateTaskOperation, With(("task", "t7"), ("html", "true"))));
            Assert.StartsWith("Microsoft To Do has no operation 'archive_task' - lists, get_list,", await Refused("archive_task", With(("task", "t7"))));
            Assert.Empty(microsoft.Requests); // not even a token asked for

            var refusing = new Microsoft { Graph = (_, _) => (HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound","message":"The specified object was not found in the store."}}""") };
            Assert.Equal("HTTP 404 ErrorItemNotFound: The specified object was not found in the store.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, MicrosoftToDoConnector.GetListOperation, list)))).Message);
        }

        [Fact]
        public async Task A_test_names_the_lists()
        {
            var settings = new Dictionary<string, string> { ["user"] = "ada@contoso.com" };
            var microsoft = new Microsoft { Graph = (_, _) => (HttpStatusCode.OK, """{"value":[{"id":"AAMk-l1","displayName":"Tasks"},{"id":"AAMk-l2","displayName":"Errands"}]}""") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(microsoft), "https://graph.example.test/v1.0", settings: settings, credentials: Credentials));
            Assert.Equal((true, "Signed in - 2 lists, Tasks first."), (tested.Success, tested.Message));
            Assert.Equal((Me + "/todo/lists?$top=100", "Bearer access-1"), (microsoft.GraphRequests[0].Url, microsoft.GraphRequests[0].Authorization));
            var none = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Microsoft()), "https://graph.example.test/v1.0", settings: settings, credentials: Credentials));
            Assert.Equal((true, "Signed in - no lists yet."), (none.Success, none.Message));
            var nobody = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new Microsoft()), "https://graph.example.test/v1.0", settings: new Dictionary<string, string>(), credentials: Credentials));
            Assert.Equal((false, "Name the user whose To Do the app acts on - the connection's user setting."), (nobody.Success, nobody.Message));
            Assert.Equal((AuthenticationType.OAuth2ClientCredentials, "microsofttodo", ConnectorCategory.Productivity), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "lists", "get_list", "create_list", "rename_list", "delete_list", "tasks", "get_task", "create_task", "update_task", "delete_task" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
