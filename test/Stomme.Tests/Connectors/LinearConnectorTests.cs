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
using Stomme.Modules.Connectors.Linear;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Linear (n8n's Linear node: issues, a comment, a link) over its GraphQL API: a personal key as Linear takes it and an
    /// OAuth token as a bearer; an app page's address taken to the API; issues by team, state, assignee (me too), label, priority
    /// and change, newest change first, page by page by cursor up to the most asked for, each a plain record; an issue got,
    /// created and updated with its team by key, its state by name in the team, its assignee by address and its labels by name -
    /// each looked up, and refused when none is found - deleted, commented on and linked; the teams; what is not an identifier, a
    /// key, an address, a priority, a time, a date, an estimate or one line refused before anything is sent; Linear's refusal its
    /// message and code; a test naming the user.
    /// </summary>
    public sealed class LinearConnectorTests
    {
        private const string Api = "https://api.linear.app/graphql";

        private const string PersonalKey = "lin_api_test_1";

        private const string TeamId = "11111111-2222-3333-4444-555555555555";

        private sealed class LinearApi : HttpMessageHandler
        {
            public List<(string Url, string? Authorization, string Query, JsonObject Variables)> Requests { get; } = new();

            public Func<string, JsonObject, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"data\":{}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var query = body["query"]!.GetValue<string>();
                var variables = body["variables"] as JsonObject ?? new JsonObject();
                Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null, query, variables));
                var (status, answer) = Answer(query, variables);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }

        private static ConnectorExtractContext Context(LinearApi linear, string operation, Dictionary<string, string> parameters, string key = PersonalKey, string endpoint = "https://api.linear.app") =>
            Contexts.Extract(new StubEgress(linear), endpoint, operation, parameters, null, new Dictionary<string, string> { ["api-key"] = key });

        private static LinearConnector Connector() => new();

        private static string Issue(int number, string state = "Todo") =>
            "{\"id\":\"i" + number + "\",\"identifier\":\"ENG-" + number + "\",\"title\":\"Fix " + number + "\",\"description\":\"It broke.\",\"url\":\"https://linear.app/acme/issue/ENG-" + number + "\",\"priority\":2,\"priorityLabel\":\"High\",\"estimate\":3,\"dueDate\":\"2026-10-31\","
            + "\"createdAt\":\"2026-10-01T10:00:00.000Z\",\"updatedAt\":\"2026-10-02T10:00:00.000Z\",\"completedAt\":null,\"state\":{\"name\":\"" + state + "\"},\"team\":{\"key\":\"ENG\"},\"assignee\":{\"email\":\"ada@example.com\"},\"labels\":{\"nodes\":[{\"name\":\"Bug\"},{\"name\":\"Customer\"}]}}";

        private static string Page(int from, int count, bool more, string? cursor) =>
            "{\"data\":{\"issues\":{\"nodes\":[" + string.Join(",", Enumerable.Range(from, count).Select(number => Issue(number))) + "],\"pageInfo\":{\"hasNextPage\":" + (more ? "true" : "false") + ",\"endCursor\":" + (cursor is null ? "null" : "\"" + cursor + "\"") + "}}}}";

        [Fact]
        public async Task Issues_are_read_page_by_page_by_cursor_up_to_the_most_asked_for_by_the_keys_own_header()
        {
            var linear = new LinearApi { Answer = (_, variables) => variables["after"] is null ? (HttpStatusCode.OK, Page(1, 100, true, "c1")) : (HttpStatusCode.OK, Page(101, 100, true, "c2")) };
            var records = await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.IssuesOperation, new()
            {
                ["team"] = "eng", ["state"] = "In Progress", ["assignee"] = "ada@example.com", ["label"] = "Bug", ["priority"] = "2", ["updated_since"] = "2026-10-01T02:00:00+02:00", ["max"] = "150",
            }, endpoint: "https://linear.app/acme/team/ENG/active"));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal(2, linear.Requests.Count);
            Assert.All(linear.Requests, request => Assert.Equal((Api, PersonalKey), (request.Url, request.Authorization))); // an app page's address: the API; a personal key alone
            Assert.Contains("orderBy: updatedAt", linear.Requests[0].Query, StringComparison.Ordinal);
            Assert.Equal("{\"first\":100,\"after\":null,\"filter\":{\"team\":{\"key\":{\"eq\":\"ENG\"}},\"state\":{\"name\":{\"eq\":\"In Progress\"}},\"assignee\":{\"email\":{\"eq\":\"ada@example.com\"}},\"labels\":{\"some\":{\"name\":{\"eq\":\"Bug\"}}},\"priority\":{\"eq\":2},\"updatedAt\":{\"gt\":\"2026-10-01T00:00:00.000Z\"}}}",
                linear.Requests[0].Variables.ToJsonString());
            Assert.Equal(("c1", 50), (linear.Requests[1].Variables["after"]!.GetValue<string>(), linear.Requests[1].Variables["first"]!.GetValue<int>())); // the cursor; a page no bigger than what is left
            var issue = records[0].Fields;
            Assert.Equal(("ENG-1", "Fix 1", "https://linear.app/acme/issue/ENG-1", 2L, "High", "Todo", "ENG", "ada@example.com", "Bug, Customer", 3.0, "2026-10-31"),
                ((string?)issue["identifier"], (string?)issue["title"], (string?)issue["url"], (long?)issue["priority"], (string?)issue["priority_label"], (string?)issue["state"], (string?)issue["team"], (string?)issue["assignee"], (string?)issue["labels"], (double?)issue["estimate"], (string?)issue["due_date"]));

            var ten = new LinearApi { Answer = (_, variables) => (HttpStatusCode.OK, Page(1, 100, true, "c" + NextCursor(variables))) };
            static string NextCursor(JsonObject variables) => variables["after"]?.GetValue<string>() is { } after ? after + "x" : "0";
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, LinearConnector.IssuesOperation, new()))).Count); // by default up to 1,000
            Assert.Equal(10, ten.Requests.Count);

            var last = new LinearApi { Answer = (_, _) => (HttpStatusCode.OK, Page(1, 2, false, "c9")) };
            Assert.Equal(2, (await Contexts.DrainAsync(Connector(), Context(last, LinearConnector.IssuesOperation, new() { ["assignee"] = "me", ["team"] = TeamId }, key: "oauth_token_1", endpoint: "http://127.0.0.1:5275"))).Count);
            var only = Assert.Single(last.Requests); // no next page, though a cursor came: the last
            Assert.Equal(("http://127.0.0.1:5275/graphql", "Bearer oauth_token_1", "{\"team\":{\"id\":{\"eq\":\"" + TeamId + "\"}},\"assignee\":{\"isMe\":{\"eq\":true}}}"), (only.Url, only.Authorization, only.Variables["filter"]!.ToJsonString())); // a stand-in as given; an OAuth token a bearer
        }

        [Fact]
        public async Task An_issue_is_created_and_updated_by_names_looked_up_and_got_deleted_commented_on_and_linked()
        {
            var linear = new LinearApi
            {
                Answer = (query, variables) => (HttpStatusCode.OK, query switch
                {
                    _ when query.Contains("teams(filter", StringComparison.Ordinal) => variables["key"]!.GetValue<string>() == "ENG" ? "{\"data\":{\"teams\":{\"nodes\":[{\"id\":\"" + TeamId + "\"}]}}}" : "{\"data\":{\"teams\":{\"nodes\":[]}}}",
                    _ when query.Contains("users(filter", StringComparison.Ordinal) => "{\"data\":{\"users\":{\"nodes\":[{\"id\":\"u-ada\"}]}}}",
                    _ when query.Contains("viewer", StringComparison.Ordinal) => "{\"data\":{\"viewer\":{\"id\":\"u-me\"}}}",
                    _ when query.Contains("workflowStates", StringComparison.Ordinal) => "{\"data\":{\"workflowStates\":{\"nodes\":[{\"id\":\"s-progress\"}]}}}",
                    _ when query.Contains("issueLabels", StringComparison.Ordinal) => "{\"data\":{\"issueLabels\":{\"nodes\":[{\"id\":\"l-bug-ws\",\"name\":\"Bug\",\"team\":null},{\"id\":\"l-bug-eng\",\"name\":\"Bug\",\"team\":{\"id\":\"" + TeamId + "\"}},{\"id\":\"l-other\",\"name\":\"Customer\",\"team\":{\"id\":\"elsewhere\"}},{\"id\":\"l-cust\",\"name\":\"Customer\",\"team\":null}]}}}",
                    _ when query.Contains("IssueTeam", StringComparison.Ordinal) => "{\"data\":{\"issue\":{\"team\":{\"id\":\"" + TeamId + "\"}}}}",
                    _ when query.Contains("issueCreate", StringComparison.Ordinal) => "{\"data\":{\"issueCreate\":{\"success\":true,\"issue\":" + Issue(7) + "}}}",
                    _ when query.Contains("issueUpdate", StringComparison.Ordinal) => "{\"data\":{\"issueUpdate\":{\"success\":true,\"issue\":" + Issue(7, "In Progress") + "}}}",
                    _ when query.Contains("issueDelete", StringComparison.Ordinal) => "{\"data\":{\"issueDelete\":{\"success\":true}}}",
                    _ when query.Contains("commentCreate", StringComparison.Ordinal) => "{\"data\":{\"commentCreate\":{\"success\":true,\"comment\":{\"id\":\"c1\",\"body\":\"Looking.\",\"url\":\"https://linear.app/acme/issue/ENG-7#comment-c1\",\"createdAt\":\"2026-10-12T08:00:00.000Z\"}}}}",
                    _ when query.Contains("attachmentLinkURL", StringComparison.Ordinal) => "{\"data\":{\"attachmentLinkURL\":{\"success\":true,\"attachment\":{\"id\":\"a1\",\"url\":\"https://shop.example.com/orders/1001\",\"title\":\"Order 1001\"}}}}",
                    _ => "{\"data\":{\"issue\":" + Issue(7) + "}}",
                }),
            };

            var made = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.CreateIssueOperation, new()
            {
                ["team"] = "eng", ["title"] = "Printer on fire", ["description"] = "It broke.\nTwice.", ["assignee"] = "ada@example.com", ["priority"] = "1", ["state"] = "in progress", ["labels"] = "bug, Customer", ["estimate"] = "3", ["due_date"] = "2026-10-31",
            })));
            var create = linear.Requests.Single(request => request.Query.Contains("issueCreate", StringComparison.Ordinal));
            Assert.Equal("{\"title\":\"Printer on fire\",\"description\":\"It broke.\\nTwice.\",\"priority\":1,\"estimate\":3,\"dueDate\":\"2026-10-31\",\"teamId\":\"" + TeamId + "\",\"assigneeId\":\"u-ada\",\"stateId\":\"s-progress\",\"labelIds\":[\"l-bug-eng\",\"l-cust\"]}",
                create.Variables["input"]!.ToJsonString()); // the team's own Bug before the workspace's; another team's Customer not taken
            Assert.Equal(("ENG", "in progress"), (linear.Requests.Single(request => request.Query.Contains("teams(filter", StringComparison.Ordinal)).Variables["key"]!.GetValue<string>(), linear.Requests.Single(request => request.Query.Contains("workflowStates", StringComparison.Ordinal)).Variables["name"]!.GetValue<string>()));
            Assert.Equal(TeamId, linear.Requests.Single(request => request.Query.Contains("workflowStates", StringComparison.Ordinal)).Variables["team"]!.GetValue<string>());
            Assert.Contains("name: { eqIgnoreCase: $name }", linear.Requests.Single(request => request.Query.Contains("workflowStates", StringComparison.Ordinal)).Query, StringComparison.Ordinal); // a person's 'in progress' is Linear's 'In Progress'
            Assert.Equal("ENG-7", made.Fields["identifier"]);

            linear.Requests.Clear();
            var updated = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.UpdateIssueOperation, new() { ["issue"] = "eng-7", ["state"] = "In Progress", ["assignee"] = "ME" })));
            Assert.Equal(TeamId, linear.Requests.Single(request => request.Query.Contains("workflowStates", StringComparison.Ordinal)).Variables["team"]!.GetValue<string>()); // the issue's own team
            var update = linear.Requests.Single(request => request.Query.Contains("issueUpdate", StringComparison.Ordinal));
            Assert.Equal(("ENG-7", "{\"assigneeId\":\"u-me\",\"stateId\":\"s-progress\"}"), (update.Variables["id"]!.GetValue<string>(), update.Variables["input"]!.ToJsonString()));
            Assert.Equal("In Progress", updated.Fields["state"]);

            linear.Requests.Clear();
            await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.UpdateIssueOperation, new() { ["issue"] = "ENG-7", ["title"] = "Printer fixed" }));
            Assert.Equal("{\"title\":\"Printer fixed\"}", Assert.Single(linear.Requests).Variables["input"]!.ToJsonString()); // nothing to look up: one call

            linear.Requests.Clear();
            const string UserId = "aaaaaaaa-0000-4000-8000-000000000001", StateId = "bbbbbbbb-0000-4000-8000-000000000002", LabelId = "cccccccc-0000-4000-8000-000000000003";
            await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.CreateIssueOperation, new() { ["team"] = TeamId, ["title"] = "From n8n", ["assignee"] = UserId, ["state"] = StateId, ["labels"] = LabelId }));
            Assert.Equal("{\"title\":\"From n8n\",\"teamId\":\"" + TeamId + "\",\"assigneeId\":\"" + UserId + "\",\"stateId\":\"" + StateId + "\",\"labelIds\":[\"" + LabelId + "\"]}",
                Assert.Single(linear.Requests).Variables["input"]!.ToJsonString()); // ids, as n8n gives them: nothing looked up
            linear.Requests.Clear();
            await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.UpdateIssueOperation, new() { ["issue"] = "ENG-7", ["state"] = StateId, ["labels"] = LabelId }));
            Assert.Equal("{\"stateId\":\"" + StateId + "\",\"labelIds\":[\"" + LabelId + "\"]}", Assert.Single(linear.Requests).Variables["input"]!.ToJsonString()); // ids: not even the issue's team asked for
            Assert.Equal("The team has no label 'Nope'.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.UpdateIssueOperation, new() { ["issue"] = "ENG-7", ["labels"] = "Bug, Nope" })))).Message);
            linear.Requests.Clear();
            await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.IssuesOperation, new() { ["state"] = StateId, ["assignee"] = UserId }));
            Assert.Equal("{\"state\":{\"id\":{\"eq\":\"" + StateId + "\"}},\"assignee\":{\"id\":{\"eq\":\"" + UserId + "\"}}}", Assert.Single(linear.Requests).Variables["filter"]!.ToJsonString());

            Assert.Equal("Fix 7", Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.GetIssueOperation, new() { ["issue"] = "ENG-7" }))).Fields["title"]);
            var deleted = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.DeleteIssueOperation, new() { ["issue"] = "ENG-7" })));
            Assert.Equal(("ENG-7", true), ((string?)deleted.Fields["issue"], (bool?)deleted.Fields["deleted"]));
            Assert.Equal("ENG-7", linear.Requests[^1].Variables["id"]!.GetValue<string>());
            var comment = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.AddCommentOperation, new() { ["issue"] = "ENG-7", ["body"] = "Looking.\nNow." })));
            Assert.Equal("{\"input\":{\"issueId\":\"ENG-7\",\"body\":\"Looking.\\nNow.\"}}", linear.Requests[^1].Variables.ToJsonString());
            Assert.Equal("c1", comment.Fields["id"]);
            var link = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.AddLinkOperation, new() { ["issue"] = "ENG-7", ["url"] = "https://shop.example.com/orders/1001", ["title"] = "Order 1001" })));
            Assert.Equal("{\"issueId\":\"ENG-7\",\"url\":\"https://shop.example.com/orders/1001\",\"title\":\"Order 1001\"}", linear.Requests[^1].Variables.ToJsonString());
            Assert.Equal("a1", link.Fields["id"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_linears_refusal_is_its_words()
        {
            var linear = new LinearApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://api.linear.app") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(linear, operation, parameters, endpoint: endpoint)))).Message;

            Assert.Equal("'ENG 7' is not an issue's identifier (ENG-123) or id.", await Refused(LinearConnector.GetIssueOperation, new() { ["issue"] = "ENG 7" }));
            Assert.Equal("'Eng Team' is not a team's key (ENG) or id.", await Refused(LinearConnector.IssuesOperation, new() { ["team"] = "Eng Team" }));
            Assert.Equal("'ada' is not an address, or me.", await Refused(LinearConnector.IssuesOperation, new() { ["assignee"] = "ada" }));
            Assert.Equal("'5' is not a priority - 0 none, 1 urgent, 2 high, 3 medium, 4 low.", await Refused(LinearConnector.IssuesOperation, new() { ["priority"] = "5" }));
            Assert.Equal("'last week' is not a time - 2026-10-01T00:00:00Z.", await Refused(LinearConnector.IssuesOperation, new() { ["updated_since"] = "last week" }));
            Assert.Equal("The state has a line break - one line only.", await Refused(LinearConnector.IssuesOperation, new() { ["state"] = "Todo\nDone" }));
            Assert.Equal("Give the team - its key, ENG, or its id.", await Refused(LinearConnector.CreateIssueOperation, new() { ["title"] = "x" }));
            Assert.Equal("'Eng Team' is not a team's key (ENG) or id.", await Refused(LinearConnector.CreateIssueOperation, new() { ["team"] = "Eng Team", ["title"] = "x" }));
            Assert.Equal("Give the issue's title.", await Refused(LinearConnector.CreateIssueOperation, new() { ["team"] = "ENG" }));
            Assert.Equal("The title has a line break - one line only.", await Refused(LinearConnector.CreateIssueOperation, new() { ["team"] = "ENG", ["title"] = "a\nb" }));
            Assert.Equal("'31/10' is not a due date - 2026-10-31.", await Refused(LinearConnector.CreateIssueOperation, new() { ["team"] = "ENG", ["title"] = "x", ["due_date"] = "31/10" }));
            Assert.Equal("'a lot' is not an estimate - a whole number of points.", await Refused(LinearConnector.CreateIssueOperation, new() { ["team"] = "ENG", ["title"] = "x", ["estimate"] = "a lot" }));
            Assert.Equal("Give something to change - a title, a description, an assignee, a priority, a state, labels, an estimate, a due date or a team.", await Refused(LinearConnector.UpdateIssueOperation, new() { ["issue"] = "ENG-7" }));
            Assert.Equal("Give the comment.", await Refused(LinearConnector.AddCommentOperation, new() { ["issue"] = "ENG-7" }));
            Assert.Equal("'shop/orders/1' is not a link's address - https://...", await Refused(LinearConnector.AddLinkOperation, new() { ["issue"] = "ENG-7", ["url"] = "shop/orders/1" }));
            Assert.Equal("'ftp://shop.example.com/orders/1' is not a link's address - https://...", await Refused(LinearConnector.AddLinkOperation, new() { ["issue"] = "ENG-7", ["url"] = "ftp://shop.example.com/orders/1" }));
            Assert.StartsWith("Linear has no operation 'archive_issue'", await Refused("archive_issue", new() { ["issue"] = "ENG-7" }));
            Assert.Equal("'ftp://x.example' is not Linear's API address - https://api.linear.app.", await Refused(LinearConnector.TeamsOperation, new(), "ftp://x.example"));
            Assert.Empty(linear.Requests);

            var nobody = new LinearApi { Answer = (query, _) => (HttpStatusCode.OK, query.Contains("teams(filter", StringComparison.Ordinal) ? "{\"data\":{\"teams\":{\"nodes\":[]}}}" : "{\"data\":{}}") };
            Assert.Equal("Linear has no team OPS.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(nobody, LinearConnector.CreateIssueOperation, new() { ["team"] = "ops", ["title"] = "x" })))).Message);
            Assert.DoesNotContain(nobody.Requests, request => request.Query.Contains("issueCreate", StringComparison.Ordinal)); // nothing made when a name is not found

            var undone = new LinearApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"data\":{\"issueDelete\":{\"success\":false}}}") };
            Assert.Equal("Linear did not do it (issueDelete).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(undone, LinearConnector.DeleteIssueOperation, new() { ["issue"] = "ENG-7" })))).Message);

            var refusing = new LinearApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"errors\":[{\"message\":\"Entity not found: Issue\",\"extensions\":{\"code\":\"INVALID_INPUT\",\"userPresentableMessage\":\"Could not find referenced Issue.\"}}],\"data\":null}") };
            Assert.Equal("Linear said: Could not find referenced Issue. (INVALID_INPUT)",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, LinearConnector.GetIssueOperation, new() { ["issue"] = "ENG-404" })))).Message);
            var unauthorized = new LinearApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"Authentication required, not authenticated\",\"extensions\":{\"code\":\"AUTHENTICATION_ERROR\"}}]}") };
            Assert.Equal("HTTP 400: Authentication required, not authenticated (AUTHENTICATION_ERROR)",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(unauthorized, LinearConnector.TeamsOperation, new())))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user_and_the_teams_are_listed()
        {
            var linear = new LinearApi { Answer = (query, _) => (HttpStatusCode.OK, query.Contains("viewer", StringComparison.Ordinal) ? "{\"data\":{\"viewer\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}}}" : "{\"data\":{\"teams\":{\"nodes\":[{\"id\":\"" + TeamId + "\",\"key\":\"ENG\",\"name\":\"Engineering\"}]}}}") };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(linear), "https://api.linear.app", credentials: new Dictionary<string, string> { ["api-key"] = PersonalKey }));
            Assert.Equal((true, "Signed in as Ada Lovelace (ada@example.com)."), (tested.Success, tested.Message));
            var team = Assert.Single(await Contexts.DrainAsync(Connector(), Context(linear, LinearConnector.TeamsOperation, new()))).Fields;
            Assert.Equal((TeamId, "ENG", "Engineering"), ((string?)team["id"], (string?)team["key"], (string?)team["name"]));
            Assert.Equal((AuthenticationType.ApiKey, "linear"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(new[] { ConnectorCategory.Development, ConnectorCategory.Productivity }, Connector().Categories);
            Assert.Equal(new[] { "issues", "get_issue", "create_issue", "update_issue", "delete_issue", "add_comment", "add_link", "teams" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
