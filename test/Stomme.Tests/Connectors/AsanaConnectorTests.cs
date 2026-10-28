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
using Stomme.Modules.Connectors.Asana;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Asana (n8n's Asana node: a task created, got, listed, updated, moved and deleted, a comment added): a personal
    /// access token as a bearer; a project's open tasks - or every one - read page by page by Asana's offset up to the most asked
    /// for, each a plain record - a person, a project, a tag and a section by name; a task made in a project or a workspace, its
    /// due date a day or a time, read by its address, changed, completed, moved to a section, commented on and deleted; a task, a
    /// project, a section, an assignee, a name's line break, a due date, a flag and an update of nothing refused before anything is
    /// sent; Asana's refusal its first error; a test naming the user.
    /// </summary>
    public sealed class AsanaConnectorTests
    {
        private const string Api = "https://asana.example.test/api/1.0";

        private const string Fields = "opt_fields=name,notes,completed,completed_at,due_on,due_at,assignee.name,projects.name,tags.name,memberships.section.name,permalink_url,created_at,modified_at";

        private static readonly Dictionary<string, string> Token = new() { ["token"] = "pat-1" };

        private sealed class AsanaApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(AsanaApi asana, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(asana), Api, operation, parameters, credentials: Token);

        private static string TaskJson(string gid, string name) =>
            "{\"gid\":\"" + gid + "\",\"name\":\"" + name + "\",\"notes\":\"Twelve, please.\",\"completed\":false,\"completed_at\":null,\"due_on\":\"2026-10-12\",\"due_at\":null,"
                + "\"assignee\":{\"gid\":\"9\",\"name\":\"Ada Lovelace\"},\"projects\":[{\"gid\":\"100\",\"name\":\"Orders\"}],\"tags\":[{\"gid\":\"7\",\"name\":\"billing\"}],"
                + "\"memberships\":[{\"section\":{\"gid\":\"300\",\"name\":\"To do\"}}],\"permalink_url\":\"https://app.asana.example/0/100/" + gid + "\",\"created_at\":\"2026-10-01T10:00:00.000Z\",\"modified_at\":\"2026-10-02T10:00:00.000Z\"}";

        [Fact]
        public async Task A_projects_open_tasks_are_read_page_by_page_by_asanas_offset_up_to_the_most_asked_for()
        {
            var asana = new AsanaApi
            {
                Answer = (_, url, _) => url.Contains("offset=o2", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"data\":[" + TaskJson("203", "Order 1003") + "," + TaskJson("204", "Order 1004") + "],\"next_page\":{\"offset\":\"o3\"}}")
                    : (HttpStatusCode.OK, "{\"data\":[" + TaskJson("201", "Order 1001") + "," + TaskJson("202", "Order 1002") + "],\"next_page\":{\"offset\":\"o2\"}}"),
            };
            var records = await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.TasksOperation, new() { ["project"] = "https://app.asana.com/0/100/list", ["modified_since"] = "2026-10-01", ["max"] = "3" }));

            Assert.Equal(new[] { "201", "202", "203" }, records.Select(record => (string?)record.Fields["gid"])); // the most asked for: no page more
            Assert.Equal(2, asana.Requests.Count);
            Assert.Equal($"{Api}/projects/100/tasks?{Fields}&completed_since=now&modified_since=2026-10-01T00%3A00%3A00.000Z&limit=3", asana.Requests[0].Url); // its address cut to its gid; open ones only
            Assert.Equal($"{Api}/projects/100/tasks?{Fields}&completed_since=now&modified_since=2026-10-01T00%3A00%3A00.000Z&limit=1&offset=o2", asana.Requests[1].Url);
            Assert.Equal("Bearer pat-1", asana.Requests[0].Authorization);
            var first = records[0].Fields;
            Assert.Equal(("Order 1001", "Twelve, please.", false, "2026-10-12", "Ada Lovelace", "Orders", "billing", "To do", "https://app.asana.example/0/100/201"),
                ((string?)first["name"], (string?)first["notes"], (bool?)first["completed"], (string?)first["due_on"], (string?)first["assignee"], (string?)first["projects"], (string?)first["tags"], (string?)first["section"], (string?)first["url"]));

            var every = new AsanaApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"data\":[]}") };
            await Contexts.DrainAsync(new AsanaConnector(), Context(every, AsanaConnector.TasksOperation, new() { ["project"] = "100", ["completed"] = "true" }));
            Assert.Equal($"{Api}/projects/100/tasks?{Fields}&limit=100", every.Requests[0].Url); // completed too: no completed_since
        }

        [Fact]
        public async Task A_task_is_made_read_by_its_address_changed_completed_moved_commented_on_and_deleted()
        {
            var asana = new AsanaApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("DELETE", _) => (HttpStatusCode.OK, "{\"data\":{}}"),
                    ("POST", _) when url.EndsWith("/stories", StringComparison.Ordinal) => (HttpStatusCode.Created, "{\"data\":{\"gid\":\"555\",\"text\":\"Packed.\"}}"),
                    ("POST", _) when url.EndsWith("/addTask", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"data\":{}}"),
                    _ => (HttpStatusCode.OK, "{\"data\":" + TaskJson("201", "Order 1001") + "}"),
                },
            };

            var made = await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.CreateTaskOperation, new()
            {
                ["project"] = "100", ["name"] = "Order 1001", ["notes"] = "Twelve, please.\nThanks.", ["assignee"] = "ada@example.com", ["due"] = "2026-10-12",
            }));
            Assert.Equal((HttpMethod.Post, $"{Api}/tasks?{Fields}"), (asana.Requests[0].Method, asana.Requests[0].Url));
            Assert.Equal("{\"data\":{\"name\":\"Order 1001\",\"notes\":\"Twelve, please.\\nThanks.\",\"assignee\":\"ada@example.com\",\"due_on\":\"2026-10-12\",\"projects\":[\"100\"]}}", asana.Requests[0].Body);
            Assert.Equal("201", Assert.Single(made).Fields["gid"]);
            await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.CreateTaskOperation, new() { ["workspace"] = "1", ["name"] = "Call", ["due"] = "2026-10-12T17:00:00+02:00", ["assignee"] = "me" }));
            Assert.Equal("{\"data\":{\"name\":\"Call\",\"assignee\":\"me\",\"due_at\":\"2026-10-12T15:00:00.000Z\",\"workspace\":\"1\"}}", asana.Requests[1].Body); // a time: due_at, in UTC

            await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.GetTaskOperation, new() { ["task"] = "https://app.asana.com/1/1/project/100/task/201" }));
            Assert.Equal($"{Api}/tasks/201?{Fields}", asana.Requests[2].Url); // its address cut to its gid

            await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.UpdateTaskOperation, new() { ["task"] = "201", ["completed"] = "True", ["notes"] = "Shipped." }));
            Assert.Equal((HttpMethod.Put, $"{Api}/tasks/201?{Fields}", "{\"data\":{\"notes\":\"Shipped.\",\"completed\":true}}"), (asana.Requests[3].Method, asana.Requests[3].Url, asana.Requests[3].Body));

            var moved = await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.MoveTaskOperation, new() { ["task"] = "201", ["section"] = "301" }));
            Assert.Equal(($"{Api}/sections/301/addTask", "{\"data\":{\"task\":\"201\"}}"), (asana.Requests[4].Url, asana.Requests[4].Body));
            Assert.Equal("301", Assert.Single(moved).Fields["section"]);

            var comment = await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.CommentOperation, new() { ["task"] = "201", ["text"] = "Packed." }));
            Assert.Equal(($"{Api}/tasks/201/stories", "{\"data\":{\"text\":\"Packed.\"}}"), (asana.Requests[5].Url, asana.Requests[5].Body));
            Assert.Equal("555", Assert.Single(comment).Fields["comment_gid"]);

            var removed = await Contexts.DrainAsync(new AsanaConnector(), Context(asana, AsanaConnector.DeleteTaskOperation, new() { ["task"] = "201" }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/tasks/201"), (asana.Requests[6].Method, asana.Requests[6].Url));
            Assert.True((bool?)Assert.Single(removed).Fields["deleted"]);
        }

        [Fact]
        public async Task What_could_reach_the_path_or_mislead_asana_is_refused_before_anything_is_sent_and_asanas_refusal_is_its_first_error()
        {
            var asana = new AsanaApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AsanaConnector(), Context(asana, operation, parameters)))).Message;

            Assert.StartsWith("'201/../x' is not an Asana task", await Refused(AsanaConnector.GetTaskOperation, new() { ["task"] = "201/../x" }));
            Assert.StartsWith("'orders' is not an Asana project", await Refused(AsanaConnector.TasksOperation, new() { ["project"] = "orders" }));
            Assert.StartsWith("'todo' is not an Asana section", await Refused(AsanaConnector.MoveTaskOperation, new() { ["task"] = "201", ["section"] = "todo" }));
            Assert.Equal("Name the project - or the workspace - the task goes in.", await Refused(AsanaConnector.CreateTaskOperation, new() { ["name"] = "x" }));
            Assert.StartsWith("'Ada Lovelace' is not an assignee", await Refused(AsanaConnector.CreateTaskOperation, new() { ["project"] = "100", ["name"] = "x", ["assignee"] = "Ada Lovelace" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(AsanaConnector.CreateTaskOperation, new() { ["project"] = "100", ["name"] = "Order\nBcc" }));
            Assert.StartsWith("'next friday' is not a time", await Refused(AsanaConnector.CreateTaskOperation, new() { ["project"] = "100", ["name"] = "x", ["due"] = "next friday" }));
            Assert.StartsWith("'yes' is not true or false", await Refused(AsanaConnector.UpdateTaskOperation, new() { ["task"] = "201", ["completed"] = "yes" }));
            Assert.StartsWith("Give something to change", await Refused(AsanaConnector.UpdateTaskOperation, new() { ["task"] = "201" }));
            Assert.Empty(asana.Requests);

            var refusing = new AsanaApi { Answer = (_, _, _) => (HttpStatusCode.Forbidden, "{\"errors\":[{\"message\":\"You do not have access to this project.\",\"help\":\"...\"}]}") };
            Assert.Equal("HTTP 403: You do not have access to this project.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new AsanaConnector(), Context(refusing, AsanaConnector.TasksOperation, new() { ["project"] = "100" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var asana = new AsanaApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"data\":{\"gid\":\"9\",\"name\":\"Ops Bot\",\"email\":\"ops@example.com\"}}") };
            var tested = await new AsanaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(asana), Api, credentials: Token));
            Assert.Equal((true, "Signed in as Ops Bot."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/users/me?opt_fields=name,email", "Bearer pat-1"), (asana.Requests[0].Url, asana.Requests[0].Authorization));
            var refused = await new AsanaConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new AsanaApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"Not Authorized\"}]}") }), Api, credentials: Token));
            Assert.Equal((false, "HTTP 401: Not Authorized"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "asana", ConnectorCategory.Productivity), (new AsanaConnector().AuthType, new AsanaConnector().ConnectorType, Assert.Single(new AsanaConnector().Categories)));
            Assert.Equal(new[] { "tasks", "get_task", "create_task", "update_task", "move_task", "delete_task", "comment" }, new AsanaConnector().Operations.Select(operation => operation.Name));
        }
    }
}
