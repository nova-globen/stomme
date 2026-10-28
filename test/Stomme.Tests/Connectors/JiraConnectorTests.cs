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
using Stomme.Modules.Connectors.Jira;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Jira (n8n's Jira Software node: Get Many, Create, Update, Add comment and a transition): the account's email and
    /// API token by the Basic scheme; the issues a JQL query finds read page by page by Jira's token up to the most asked for, each
    /// field a plain value - a person their name, a status its name, a description's document its text; an issue created in a
    /// project, updated, commented on and moved by a transition named by itself or its status; an issue or a project key, a field's
    /// name and a summary's line break refused before anything is sent; Jira's refusal its first message; a test naming the account.
    /// </summary>
    public sealed class JiraConnectorTests
    {
        private const string Site = "https://acme.atlassian.example";

        private static readonly Dictionary<string, string> Account = new() { ["username"] = "ops@acme.example", ["password"] = "api-token-1" };

        private sealed class Jira : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                var (status, answer) = Answer(request.Method, url, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(Jira jira, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(jira), Site + "/browse/OPS-1", operation, parameters, credentials: Account);

        private const string Basic = "Basic b3BzQGFjbWUuZXhhbXBsZTphcGktdG9rZW4tMQ==";

        private static string Issue(string key, string summary) =>
            """{"id":"10@N","key":"@KEY","fields":{"summary":"@SUMMARY","status":{"name":"In Progress","id":"3"},"assignee":{"displayName":"Ada Lovelace","accountId":"a1"},"priority":{"name":"High"},"labels":["billing","q4"],"customfield_10010":{"value":"Gold","id":"7"},"story_points":5,"description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Twelve, "},{"type":"text","text":"please."}]},{"type":"paragraph","content":[{"type":"text","text":"Thanks."}]}]}}}"""
                .Replace("@N", key.Split('-')[1], StringComparison.Ordinal).Replace("@KEY", key, StringComparison.Ordinal).Replace("@SUMMARY", summary, StringComparison.Ordinal);

        [Fact]
        public async Task The_issues_a_query_finds_are_read_page_by_page_each_field_a_plain_value()
        {
            var jira = new Jira
            {
                Answer = (_, _, body) => JsonNode.Parse(body!)!["nextPageToken"] is null
                    ? (HttpStatusCode.OK, """{"issues":[""" + Issue("OPS-1", "Order 1001") + "," + Issue("OPS-2", "Order 1002") + """],"nextPageToken":"t2","isLast":false}""")
                    : (HttpStatusCode.OK, """{"issues":[""" + Issue("OPS-3", "Order 1003") + "," + Issue("OPS-4", "Order 1004") + """],"nextPageToken":"t3","isLast":false}"""),
            };
            var records = await Contexts.DrainAsync(new JiraConnector(), Context(jira, JiraConnector.SearchOperation, new() { ["jql"] = "project = OPS ORDER BY created", ["fields"] = "summary, status, assignee", ["max"] = "3" }));

            Assert.Equal(new[] { "OPS-1", "OPS-2", "OPS-3" }, records.Select(record => (string)record.Fields["key"]!)); // at most three - no third page
            Assert.Equal(2, jira.Requests.Count);
            var first = jira.Requests[0];
            Assert.Equal((HttpMethod.Post, $"{Site}/rest/api/3/search/jql", Basic), (first.Method, first.Url, first.Authorization)); // the site's address, a pasted issue's path dropped
            Assert.Equal("""{"jql":"project = OPS ORDER BY created","fields":["summary","status","assignee"],"maxResults":3}""", first.Body);
            Assert.Equal((1, "t2"), (JsonNode.Parse(jira.Requests[1].Body!)!["maxResults"]!.GetValue<int>(), JsonNode.Parse(jira.Requests[1].Body!)!["nextPageToken"]!.GetValue<string>()));

            var ops1 = records[0].Fields;
            Assert.Equal(("101", $"{Site}/browse/OPS-1", "Order 1001", "In Progress", "Ada Lovelace", "High"), ((string?)ops1["id"], (string?)ops1["url"], (string?)ops1["summary"], (string?)ops1["status"], (string?)ops1["assignee"], (string?)ops1["priority"]));
            Assert.Equal(("[\"billing\",\"q4\"]", "Gold", 5L, "Twelve, please.\nThanks."), ((string?)ops1["labels"], (string?)ops1["customfield_10010"], (long?)ops1["story_points"], (string?)ops1["description"]));
        }

        [Fact]
        public async Task An_issue_is_created_updated_commented_on_and_moved_by_a_transition()
        {
            var jira = new Jira { Answer = (_, _, _) => (HttpStatusCode.Created, """{"id":"10042","key":"OPS-42","self":"x"}""") };
            var created = await Contexts.DrainAsync(new JiraConnector(), Context(jira, JiraConnector.CreateOperation, new()
            {
                ["project"] = "OPS", ["issue_type"] = "Bug", ["summary"] = "Order 1001 stuck", ["description"] = "Twelve, please.\nThanks.", ["labels"] = "billing, q4", ["fields"] = """{"priority":{"name":"High"}}""",
            }));
            Assert.Equal((HttpMethod.Post, $"{Site}/rest/api/2/issue"), (jira.Requests[0].Method, jira.Requests[0].Url));
            Assert.Equal("""{"fields":{"priority":{"name":"High"},"summary":"Order 1001 stuck","description":"Twelve, please.\nThanks.","labels":["billing","q4"],"project":{"key":"OPS"},"issuetype":{"name":"Bug"}}}""", jira.Requests[0].Body);
            Assert.Equal(("10042", "OPS-42", $"{Site}/browse/OPS-42"), ((string?)Assert.Single(created).Fields["id"], (string?)created[0].Fields["key"], (string?)created[0].Fields["url"]));

            var byId = new Jira { Answer = jira.Answer }; // n8n's pickers keep a project's and a type's id
            await Contexts.DrainAsync(new JiraConnector(), Context(byId, JiraConnector.CreateOperation, new() { ["project"] = "10000", ["issue_type"] = "10001", ["summary"] = "x" }));
            Assert.Equal("""{"fields":{"summary":"x","project":{"id":"10000"},"issuetype":{"id":"10001"}}}""", byId.Requests[0].Body);

            var updating = new Jira { Answer = (_, _, _) => (HttpStatusCode.NoContent, "") };
            var updated = await Contexts.DrainAsync(new JiraConnector(), Context(updating, JiraConnector.UpdateOperation, new() { ["issue"] = "OPS-42", ["summary"] = "Order 1001 shipped" }));
            Assert.Equal((HttpMethod.Put, $"{Site}/rest/api/2/issue/OPS-42", """{"fields":{"summary":"Order 1001 shipped"}}"""), (updating.Requests[0].Method, updating.Requests[0].Url, updating.Requests[0].Body));
            Assert.Equal("summary", (string?)Assert.Single(updated).Fields["updated"]);

            var commenting = new Jira { Answer = (_, _, _) => (HttpStatusCode.Created, """{"id":"777"}""") };
            var comment = await Contexts.DrainAsync(new JiraConnector(), Context(commenting, JiraConnector.CommentOperation, new() { ["issue"] = "OPS-42", ["text"] = "Shipped today.\nTuesday." }));
            Assert.Equal(($"{Site}/rest/api/2/issue/OPS-42/comment", """{"body":"Shipped today.\nTuesday."}"""), (commenting.Requests[0].Url, commenting.Requests[0].Body));
            Assert.Equal("777", (string?)Assert.Single(comment).Fields["comment_id"]);

            var moving = new Jira
            {
                Answer = (method, _, _) => method == HttpMethod.Get
                    ? (HttpStatusCode.OK, """{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},{"id":"31","name":"Finish","to":{"name":"Done"}}]}""")
                    : (HttpStatusCode.NoContent, ""),
            };
            var moved = await Contexts.DrainAsync(new JiraConnector(), Context(moving, JiraConnector.TransitionOperation, new() { ["issue"] = "OPS-42", ["to"] = "done" }));
            Assert.Equal((HttpMethod.Post, $"{Site}/rest/api/2/issue/OPS-42/transitions", """{"transition":{"id":"31"}}"""), (moving.Requests[1].Method, moving.Requests[1].Url, moving.Requests[1].Body)); // named by the status it leads to
            Assert.Equal(("Finish", "Done"), ((string?)Assert.Single(moved).Fields["transition"], (string?)moved[0].Fields["status"]));
            var byName = new Jira { Answer = moving.Answer };
            await Contexts.DrainAsync(new JiraConnector(), Context(byName, JiraConnector.TransitionOperation, new() { ["issue"] = "OPS-42", ["to"] = "Start" }));
            Assert.Equal("""{"transition":{"id":"11"}}""", byName.Requests[1].Body);
            Assert.StartsWith("OPS-42 has no transition 'Archive' - it can go: Start, Finish.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new JiraConnector(), Context(new Jira { Answer = moving.Answer }, JiraConnector.TransitionOperation, new() { ["issue"] = "OPS-42", ["to"] = "Archive" })))).Message);
        }

        [Fact]
        public async Task What_could_reach_the_path_or_the_issue_is_refused_before_anything_is_sent_and_jiras_refusal_is_its_message()
        {
            var jira = new Jira();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new JiraConnector(), Context(jira, operation, parameters)))).Message;

            Assert.StartsWith("'OPS-1/../2' is not a Jira issue", await Refused(JiraConnector.CommentOperation, new() { ["issue"] = "OPS-1/../2", ["text"] = "x" }));
            Assert.StartsWith("'../ops' is not a Jira project key", await Refused(JiraConnector.CreateOperation, new() { ["project"] = "../ops", ["summary"] = "x" }));
            Assert.Equal("The summary has a line break - one line only.", await Refused(JiraConnector.CreateOperation, new() { ["project"] = "OPS", ["summary"] = "Order\nBcc" }));
            Assert.Equal("'my field' is not a Jira field's name.", await Refused(JiraConnector.UpdateOperation, new() { ["issue"] = "OPS-1", ["fields"] = """{"my field":1}""" }));
            Assert.Equal("'big deal' has a space - Jira's labels have none.", await Refused(JiraConnector.UpdateOperation, new() { ["issue"] = "OPS-1", ["labels"] = "billing, big deal" }));
            Assert.Equal("Give something to change - summary, description, labels or fields.", await Refused(JiraConnector.UpdateOperation, new() { ["issue"] = "OPS-1" }));
            Assert.Empty(jira.Requests);

            var refusing = new Jira { Answer = (_, _, _) => (HttpStatusCode.BadRequest, """{"errorMessages":[],"errors":{"summary":"You must specify a summary of the issue."}}""") };
            Assert.Equal("HTTP 400: summary: You must specify a summary of the issue.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new JiraConnector(), Context(refusing, JiraConnector.CreateOperation, new() { ["project"] = "OPS", ["summary"] = "x" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var jira = new Jira { Answer = (_, _, _) => (HttpStatusCode.OK, """{"accountId":"a1","displayName":"Ops Bot","emailAddress":"ops@acme.example"}""") };
            var tested = await new JiraConnector().TestConnectionAsync(Contexts.Test(new StubEgress(jira), Site, credentials: Account));
            Assert.Equal((true, "Signed in as Ops Bot."), (tested.Success, tested.Message));
            Assert.Equal(($"{Site}/rest/api/3/myself", Basic), (jira.Requests[0].Url, jira.Requests[0].Authorization));
            Assert.Equal((AuthenticationType.Basic, "jira", ConnectorCategory.ItsmAssetManagement), (new JiraConnector().AuthType, new JiraConnector().ConnectorType, Assert.Single(new JiraConnector().Categories)));
            Assert.Equal(new[] { "search", "create", "update", "comment", "transition" }, new JiraConnector().Operations.Select(operation => operation.Name));
        }
    }
}
