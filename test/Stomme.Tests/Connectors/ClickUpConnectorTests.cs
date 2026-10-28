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
using Stomme.Modules.Connectors.ClickUp;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - ClickUp (n8n's ClickUp node: a task created, got, listed, updated and deleted, a comment added): the API token as
    /// the Authorization header's whole value; a list's open tasks - or every one, by status and tag - read page by page up to the
    /// most asked for, each a plain record - a status, a priority, a person and a tag by name, its times in UTC; a task made on a
    /// list (its priority by name, its due date a day or a time), read by its address, changed with assignees added and removed,
    /// commented on and deleted; a list, a task, a user, a name's line break, a priority, a due date and an update of nothing
    /// refused before anything is sent; ClickUp's refusal its err; a test naming the user.
    /// </summary>
    public sealed class ClickUpConnectorTests
    {
        private const string Api = "https://clickup.example.test/api/v2";

        private static readonly Dictionary<string, string> Key = new() { ["api-key"] = "pk_1_TOKEN" };

        private sealed class ClickUpApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null, body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(ClickUpApi clickUp, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(clickUp), Api, operation, parameters, credentials: Key);

        private static string TaskJson(string id, string name) =>
            "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"text_content\":\"Twelve, please.\",\"status\":{\"status\":\"in progress\"},\"priority\":{\"id\":\"2\",\"priority\":\"high\"},"
                + "\"assignees\":[{\"id\":9,\"username\":\"Ada Lovelace\"}],\"tags\":[{\"name\":\"billing\"}],\"due_date\":\"1791817200000\",\"url\":\"https://app.clickup.example/t/" + id + "\","
                + "\"list\":{\"id\":\"900\",\"name\":\"Orders\"},\"date_created\":\"1790848800000\",\"date_updated\":1790935200000}";

        [Fact]
        public async Task A_lists_open_tasks_are_read_page_by_page_up_to_the_most_asked_for_with_the_token_as_the_header()
        {
            var clickUp = new ClickUpApi
            {
                Answer = (_, url, _) => url.Contains("page=1", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "{\"tasks\":[" + TaskJson("86c3", "Order 1003") + "," + TaskJson("86c4", "Order 1004") + "],\"last_page\":true}")
                    : (HttpStatusCode.OK, "{\"tasks\":[" + TaskJson("86c1", "Order 1001") + "," + TaskJson("86c2", "Order 1002") + "],\"last_page\":false}"),
            };
            var records = await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.TasksOperation, new() { ["list"] = "900", ["statuses"] = "to do, in progress", ["tags"] = "billing", ["max"] = "3" }));

            Assert.Equal(new[] { "86c1", "86c2", "86c3" }, records.Select(record => (string?)record.Fields["id"])); // the most asked for: no page more
            Assert.Equal(2, clickUp.Requests.Count);
            Assert.Equal($"{Api}/list/900/task?page=0&statuses%5B%5D=to%20do&statuses%5B%5D=in%20progress&tags%5B%5D=billing", clickUp.Requests[0].Url); // open ones only
            Assert.Equal("pk_1_TOKEN", clickUp.Requests[0].Authorization); // the token itself, no scheme before it
            var first = records[0].Fields;
            Assert.Equal(("Order 1001", "Twelve, please.", "in progress", "high", "Ada Lovelace", "billing", "2026-10-12T15:00:00Z", "Orders", "2026-10-01T10:00:00Z", "2026-10-02T10:00:00Z"),
                ((string?)first["name"], (string?)first["description"], (string?)first["status"], (string?)first["priority"], (string?)first["assignees"], (string?)first["tags"], (string?)first["due"], (string?)first["list"], (string?)first["created"], (string?)first["updated"]));

            var every = new ClickUpApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"tasks\":[" + TaskJson("86c9", "Order 1009") + "],\"last_page\":true}") };
            Assert.Single(await Contexts.DrainAsync(new ClickUpConnector(), Context(every, ClickUpConnector.TasksOperation, new() { ["list"] = "900", ["closed"] = "true" })));
            Assert.Equal($"{Api}/list/900/task?page=0&include_closed=true", Assert.Single(every.Requests).Url); // its last page: no page after it
        }

        [Fact]
        public async Task A_task_is_made_on_a_list_read_by_its_address_changed_commented_on_and_deleted()
        {
            var clickUp = new ClickUpApi
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("DELETE", _) => (HttpStatusCode.NoContent, ""),
                    ("POST", _) when url.EndsWith("/comment", StringComparison.Ordinal) => (HttpStatusCode.OK, "{\"id\":458,\"hist_id\":\"h1\",\"date\":1760000000000}"),
                    _ => (HttpStatusCode.OK, TaskJson("86c1", "Order 1001")),
                },
            };

            var made = await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.CreateTaskOperation, new()
            {
                ["list"] = "900", ["name"] = "Order 1001", ["description"] = "Twelve, **please**.", ["status"] = "to do", ["priority"] = "High", ["due"] = "2026-10-12", ["assignees"] = "9, 10", ["tags"] = "billing",
            }));
            Assert.Equal((HttpMethod.Post, $"{Api}/list/900/task"), (clickUp.Requests[0].Method, clickUp.Requests[0].Url));
            Assert.Equal("{\"name\":\"Order 1001\",\"markdown_content\":\"Twelve, **please**.\",\"status\":\"to do\",\"priority\":2,\"due_date\":1791763200000,\"due_date_time\":false,\"assignees\":[9,10],\"tags\":[\"billing\"]}", clickUp.Requests[0].Body);
            Assert.Equal("86c1", Assert.Single(made).Fields["id"]);
            await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "Call", ["due"] = "2026-10-12T17:00:00+02:00" }));
            Assert.Equal("{\"name\":\"Call\",\"due_date\":1791817200000,\"due_date_time\":true}", clickUp.Requests[1].Body); // a time: its moment

            await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.GetTaskOperation, new() { ["task"] = "https://app.clickup.com/t/86c1" }));
            Assert.Equal($"{Api}/task/86c1", clickUp.Requests[2].Url); // its address cut to its id

            await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.UpdateTaskOperation, new() { ["task"] = "86c1", ["status"] = "complete", ["add_assignees"] = "11", ["remove_assignees"] = "9" }));
            Assert.Equal((HttpMethod.Put, $"{Api}/task/86c1", "{\"status\":\"complete\",\"assignees\":{\"add\":[11],\"rem\":[9]}}"), (clickUp.Requests[3].Method, clickUp.Requests[3].Url, clickUp.Requests[3].Body));

            var comment = await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.CommentOperation, new() { ["task"] = "86c1", ["text"] = "Packed.", ["notify_all"] = "true" }));
            Assert.Equal(($"{Api}/task/86c1/comment", "{\"comment_text\":\"Packed.\",\"notify_all\":true}"), (clickUp.Requests[4].Url, clickUp.Requests[4].Body));
            Assert.Equal("458", Assert.Single(comment).Fields["comment_id"]);

            var removed = await Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, ClickUpConnector.DeleteTaskOperation, new() { ["task"] = "86c1" }));
            Assert.Equal((HttpMethod.Delete, $"{Api}/task/86c1"), (clickUp.Requests[5].Method, clickUp.Requests[5].Url));
            Assert.True((bool?)Assert.Single(removed).Fields["deleted"]);
        }

        [Fact]
        public async Task What_could_reach_the_path_or_mislead_clickup_is_refused_before_anything_is_sent_and_clickups_refusal_is_its_err()
        {
            var clickUp = new ClickUpApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ClickUpConnector(), Context(clickUp, operation, parameters)))).Message;

            Assert.StartsWith("'../900' is not a ClickUp list's id", await Refused(ClickUpConnector.TasksOperation, new() { ["list"] = "../900" }));
            Assert.StartsWith("'86c1/../x' is not a ClickUp task", await Refused(ClickUpConnector.GetTaskOperation, new() { ["task"] = "86c1/../x" }));
            Assert.Equal("'ada' is not a ClickUp user's id (digits).", await Refused(ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "x", ["assignees"] = "ada" }));
            Assert.Equal("The name has a line break - one line only.", await Refused(ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "Order\nBcc" }));
            Assert.StartsWith("'asap' is not a priority", await Refused(ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "x", ["priority"] = "asap" }));
            Assert.StartsWith("'next friday' is not a date", await Refused(ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "x", ["due"] = "next friday" }));
            Assert.StartsWith("Give something to change", await Refused(ClickUpConnector.UpdateTaskOperation, new() { ["task"] = "86c1" }));
            Assert.Empty(clickUp.Requests);

            var refusing = new ClickUpApi { Answer = (_, _, _) => (HttpStatusCode.BadRequest, "{\"err\":\"Status does not exist\",\"ECODE\":\"CRTSK_001\"}") };
            Assert.Equal("HTTP 400: Status does not exist", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new ClickUpConnector(), Context(refusing, ClickUpConnector.CreateTaskOperation, new() { ["list"] = "900", ["name"] = "x", ["status"] = "nope" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var clickUp = new ClickUpApi { Answer = (_, _, _) => (HttpStatusCode.OK, "{\"user\":{\"id\":9,\"username\":\"Ops Bot\",\"email\":\"ops@example.com\"}}") };
            var tested = await new ClickUpConnector().TestConnectionAsync(Contexts.Test(new StubEgress(clickUp), Api, credentials: Key));
            Assert.Equal((true, "Signed in as Ops Bot."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/user", "pk_1_TOKEN"), (clickUp.Requests[0].Url, clickUp.Requests[0].Authorization));
            var refused = await new ClickUpConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new ClickUpApi { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, "{\"err\":\"Token invalid\",\"ECODE\":\"OAUTH_025\"}") }), Api, credentials: Key));
            Assert.Equal((false, "HTTP 401: Token invalid"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.ApiKey, "clickup", ConnectorCategory.Productivity), (new ClickUpConnector().AuthType, new ClickUpConnector().ConnectorType, Assert.Single(new ClickUpConnector().Categories)));
            Assert.Equal(new[] { "tasks", "get_task", "create_task", "update_task", "delete_task", "comment" }, new ClickUpConnector().Operations.Select(operation => operation.Name));
        }
    }
}
