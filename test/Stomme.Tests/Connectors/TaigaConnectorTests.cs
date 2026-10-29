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
using Stomme.Infrastructure.Connectivity;
using Stomme.Modules.Connectors.Taiga;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Taiga (n8n's Taiga node) without a server: every id, subject, colour, order and flag held to Taiga's shape before
    /// anything is sent - not even the sign-in; signed in with the user's name and password for a token, the token a bearer and
    /// the password in no address; a list's project read first, its filters under Taiga's own names (n8n's statusIsClosed and
    /// userStory are names Taiga ignores), page by page as Taiga's next-page header says; a status, priority or assignee by name;
    /// a change carrying the version read first; tags by name; Taiga's message and the field it refused; a test naming the user.
    /// </summary>
    public sealed class TaigaConnectorTests
    {
        private const string Address = "https://taiga.kafe.example";

        private const string Token = "eyJhbGciOiJIUzI1NiJ9.e30.kafe";

        private static readonly Dictionary<string, string> Credentials = new() { ["username"] = "kafe", ["password"] = "rostad-bona-7" };

        private sealed record Sent(string Method, string Url, string? Authorization, string? Body);

        private sealed class Taiga : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body, string? Next)> answer;

            public Taiga(Func<Sent, (HttpStatusCode Status, string Body, string? Next)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            public List<Sent> Calls => Requests.Where(sent => !sent.Url.EndsWith("/auth", StringComparison.Ordinal)).ToList();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body);
                Requests.Add(sent);
                var (status, text, next) = sent.Url.EndsWith("/auth", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, $"{{\"id\":5,\"username\":\"kafe\",\"auth_token\":\"{Token}\",\"refresh\":\"r\"}}", null)
                    : answer(sent);
                var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
                if (next is not null)
                {
                    response.Headers.TryAddWithoutValidation("x-pagination-next", next);
                }

                return response;
            }
        }

        private static (HttpStatusCode, string, string?) Ok(string body, string? next = null) => (HttpStatusCode.OK, body, next);

        private static Task<List<ConnectorRecord>> Run(Taiga taiga, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new TaigaConnector(), Contexts.Extract(new StubEgress(taiga), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api/v1" + route;

        private const string Issue = "{\"id\":1,\"ref\":1,\"subject\":\"Kaffet tog slut\",\"project\":2,\"status\":1,\"status_extra_info\":{\"name\":\"New\",\"is_closed\":false},\"tags\":[[\"kaffe\",null],[\"kassa\",\"#E44057\"]],\"version\":3,\"is_blocked\":false}";

        [Fact]
        public void It_declares_itself_as_n8ns_taiga_node()
        {
            IConnector connector = new TaigaConnector();
            Assert.Equal(("taiga", "Taiga", AuthenticationType.Basic), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(
                new[]
                {
                    "projects", "epics", "epic", "epic_create", "epic_update", "epic_delete", "issues", "issue", "issue_create", "issue_update", "issue_delete",
                    "tasks", "task", "task_create", "task_update", "task_delete", "user_stories", "user_story", "user_story_create", "user_story_update", "user_story_delete",
                },
                ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_taigas_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address, Dictionary<string, string>? credentials = null)
            {
                var taiga = new Taiga(_ => Ok("{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TaigaConnector(), Contexts.Extract(new StubEgress(taiga), endpoint, operation, parameters, new Dictionary<string, string>(), credentials ?? Credentials)));
                Assert.Empty(taiga.Requests); // refused before anything was sent - the sign-in too
                return refusal.Message;
            }

            Assert.StartsWith("Taiga has no operation 'issue_archive' - projects, or for each of epic, issue, task and user_story", await Refused("issue_archive", new()));
            Assert.StartsWith("Taiga has no operation 'sprints'", await Refused("sprints", new()));
            Assert.Equal("Give the project - its number, 42 (project).", await Refused("issues", new()));
            Assert.Equal("'kafe' is not a project's number - 42 (project).", await Refused("tasks", new() { ["project"] = "kafe" }));
            Assert.Equal("Give the issue - its number, 42 (issue).", await Refused("issue", new()));
            Assert.Equal("'-1' is not a user story's number - 42 (user_story).", await Refused("user_story_delete", new() { ["user_story"] = "-1" }));
            Assert.Equal("Give the issue's subject (subject).", await Refused("issue_create", new() { ["project"] = "2" }));
            Assert.Equal("A subject is one line of at most 500 characters (subject).", await Refused("task_create", new() { ["project"] = "2", ["subject"] = "Två\nrader" }));
            Assert.Equal("Give something to change on the epic - a subject, a description, a status, an assignee, tags.", await Refused("epic_update", new() { ["epic"] = "1" }));
            Assert.Equal("'blue' is not a colour - #0000FF (color).", await Refused("epic_create", new() { ["project"] = "2", ["subject"] = "Ny kassa", ["color"] = "blue" }));
            Assert.Equal("'yes' is not true or false (is_blocked).", await Refused("issue_update", new() { ["issue"] = "1", ["is_blocked"] = "yes" }));
            Assert.Equal("'0' is not a number above 0 (us_order).", await Refused("task_update", new() { ["task"] = "1", ["us_order"] = "0" }));
            Assert.Equal("'kafe' is not a user story's number - 42 (user_story).", await Refused("task_create", new() { ["project"] = "2", ["subject"] = "x", ["user_story"] = "kafe" }));
            Assert.Equal("'newest' is not an order - assigned_to, created_date, modified_date, owner, priority, severity, status, subject, type; a minus first for newest first (order_by).", await Refused("issues", new() { ["project"] = "2", ["order_by"] = "newest" }));
            Assert.Equal("'maybe' is not true or false (closed).", await Refused("epics", new() { ["project"] = "2", ["closed"] = "maybe" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("projects", new() { ["max"] = "0" }));
            Assert.Equal("Give Taiga's address - https://taiga.kafe.example.", await Refused("projects", new(), ""));
            Assert.Equal("'https://taiga.kafe.example/?x=1' is not Taiga's address - https://taiga.kafe.example, with no query.", await Refused("projects", new(), "https://taiga.kafe.example/?x=1"));
            Assert.Equal("The Taiga connection has no username.", await Refused("projects", new(), credentials: new() { ["password"] = "x" }));
            Assert.Equal("The Taiga connection has no password.", await Refused("projects", new(), credentials: new() { ["username"] = "kafe" }));
        }

        [Fact]
        public async Task It_signs_in_for_a_token_and_lists_under_taigas_own_filter_names_page_by_page()
        {
            var taiga = new Taiga(sent => sent.Url switch
            {
                var url when url.Contains("/projects/2", StringComparison.Ordinal) => Ok("{\"id\":2,\"name\":\"Kafé\"}"),
                var url when url.Contains("/users?", StringComparison.Ordinal) => Ok("[{\"id\":5,\"username\":\"kafe\"},{\"id\":7,\"username\":\"ada\"}]"),
                var url when url.Contains("/issue-statuses?", StringComparison.Ordinal) => Ok("[{\"id\":1,\"name\":\"New\"},{\"id\":2,\"name\":\"In progress\"}]"),
                var url when url.Contains("page=1&", StringComparison.Ordinal) => Ok("[" + string.Join(",", Enumerable.Range(1, 100).Select(n => Issue.Replace("\"id\":1,", $"\"id\":{n},", StringComparison.Ordinal))) + "]", Api("/issues?page=2")), // a full page, and more
                _ => Ok("[" + string.Join(",", Enumerable.Range(101, 100).Select(n => Issue.Replace("\"id\":1,", $"\"id\":{n},", StringComparison.Ordinal))) + "]"), // a second full page: max ends it
            });
            var issues = await Run(taiga, "issues", new() { ["project"] = "2", ["assigned_to"] = "Ada", ["status"] = "in progress", ["tags"] = "kaffe, kassa", ["closed"] = "false", ["max"] = "150" }, Address + "/api/v1");
            Assert.Equal(Enumerable.Range(1, 150).Select(n => (long?)n), issues.Select(issue => (long?)issue.Fields["id"])); // at most max, though the page brought more
            var auth = taiga.Requests[0];
            Assert.Equal(("POST", Address + "/api/v1/auth", null), (auth.Method, auth.Url, auth.Authorization)); // the API's own address given: Taiga's
            Assert.Equal(JsonNode.Parse("{\"type\":\"normal\",\"username\":\"kafe\",\"password\":\"rostad-bona-7\"}")!.ToJsonString(), JsonNode.Parse(auth.Body!)!.ToJsonString());
            Assert.All(taiga.Calls, sent => Assert.Equal("Bearer " + Token, sent.Authorization));
            Assert.DoesNotContain(taiga.Requests, sent => sent.Url.Contains("rostad", StringComparison.Ordinal));
            var lists = taiga.Calls.Where(sent => sent.Url.Contains("/issues?", StringComparison.Ordinal)).Select(sent => sent.Url).ToList();
            Assert.Equal(Api("/projects/2"), taiga.Calls[0].Url); // the project read first: Taiga answers a list for one it does not have with nothing
            Assert.Equal(new[]
            {
                Api("/issues?project=2&assigned_to=7&status=2&tags=kaffe%2Ckassa&status__is_closed=false&page=1&page_size=100"),
                Api("/issues?project=2&assigned_to=7&status=2&tags=kaffe%2Ckassa&status__is_closed=false&page=2&page_size=100"),
            }, lists); // names resolved in the project; n8n's statusIsClosed under Taiga's own name; one page size throughout
            Assert.Equal(("[\"kaffe\",\"kassa\"]", "New", 3L), ((string?)issues[0].Fields["tags"], (string?)issues[0].Fields["status_name"], (long?)issues[0].Fields["version"]));

            // A full page without Taiga's next-page header is the last: a page past it is Taiga's 404.
            var full = new Taiga(sent => sent.Url.Contains("/projects/", StringComparison.Ordinal) ? Ok("{\"id\":2}") : Ok("[" + string.Join(",", Enumerable.Range(1, 100).Select(n => Issue.Replace("\"id\":1,", $"\"id\":{n},", StringComparison.Ordinal))) + "]"));
            Assert.Equal(100, (await Run(full, "issues", new() { ["project"] = "2", ["max"] = "150" })).Count);
            Assert.Single(full.Calls, sent => sent.Url.Contains("/issues?", StringComparison.Ordinal));

            // A user story's status by its name, among the user stories' statuses - each kind has its own.
            var storyStatus = new Taiga(sent => sent.Url.Contains("/projects/", StringComparison.Ordinal) ? Ok("{\"id\":2}") : sent.Url.Contains("/userstory-statuses?", StringComparison.Ordinal) ? Ok("[{\"id\":9,\"name\":\"Ready\"}]") : sent.Url.Contains("-statuses?", StringComparison.Ordinal) ? Ok("[{\"id\":1,\"name\":\"Ready\"}]") : Ok("[]"));
            await Run(storyStatus, "user_stories", new() { ["project"] = "2", ["status"] = "Ready" });
            Assert.Equal(Api("/userstories?project=2&status=9&page=1&page_size=50"), storyStatus.Calls[^1].Url);

            var stories = new Taiga(sent => sent.Url.Contains("/projects/", StringComparison.Ordinal) ? Ok("{\"id\":2}") : Ok("[]"));
            Assert.Empty(await Run(stories, "tasks", new() { ["project"] = "2", ["user_story"] = "4", ["milestone"] = "9" }));
            Assert.Equal(Api("/tasks?project=2&user_story=4&milestone=9&page=1&page_size=50"), stories.Calls[^1].Url); // n8n's userStory: a name Taiga ignores
            await Run(stories, "user_stories", new() { ["project"] = "2", ["archived"] = "true", ["epic"] = "3" });
            Assert.Equal(Api("/userstories?project=2&epic=3&status__is_archived=true&page=1&page_size=50"), stories.Calls[^1].Url);

            var missing = new Taiga(_ => (HttpStatusCode.NotFound, "{\"_error_message\":\"No Project matches the given query.\"}", null));
            Assert.Equal("Taiga answered HTTP 404: No Project matches the given query.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(missing, "epics", new() { ["project"] = "999" }))).Message);
            Assert.Single(missing.Calls); // no list asked of a project that is not there

            var unknown = new Taiga(sent => sent.Url.Contains("/projects/", StringComparison.Ordinal) ? Ok("{\"id\":2}") : Ok("[{\"id\":1,\"name\":\"New\"},{\"id\":2,\"name\":\"In progress\"}]"));
            Assert.Equal("The project has no status 'Klar' - it has New, In progress (status).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "issues", new() { ["project"] = "2", ["status"] = "Klar" }))).Message);
        }

        [Fact]
        public async Task An_item_is_made_with_names_resolved_and_changed_with_the_version_read_first()
        {
            var taiga = new Taiga(sent => (sent.Method, sent.Url) switch
            {
                ("GET", var url) when url.Contains("/priorities?", StringComparison.Ordinal) => Ok("[{\"id\":1,\"name\":\"Low\"},{\"id\":3,\"name\":\"High\"}]"),
                ("GET", var url) when url.Contains("/issue-statuses?", StringComparison.Ordinal) => Ok("[{\"id\":2,\"name\":\"In progress\"}]"),
                ("GET", var url) when url.Contains("/users?", StringComparison.Ordinal) => Ok("[{\"id\":5,\"username\":\"kafe\"}]"),
                ("GET", _) => Ok(Issue),
                _ => (HttpStatusCode.Created, Issue, null),
            });
            var made = Assert.Single(await Run(taiga, "issue_create", new() { ["project"] = "2", ["subject"] = "Kaffet tog slut", ["query"] = "Två säckar\nmörkrost", ["priority"] = "High", ["assigned_to"] = "kafe", ["tags"] = "kaffe,kassa", ["is_blocked"] = "true", ["blocked_note"] = "Leverantören" })).Fields;
            var created = taiga.Calls.Single(sent => sent.Method == "POST");
            Assert.Equal(Api("/issues"), created.Url);
            Assert.Equal(
                JsonNode.Parse("{\"project\":2,\"subject\":\"Kaffet tog slut\",\"description\":\"Två säckar\\nmörkrost\",\"assigned_to\":5,\"tags\":[\"kaffe\",\"kassa\"],\"is_blocked\":true,\"blocked_note\":\"Leverantören\",\"priority\":3}")!.ToJsonString(),
                JsonNode.Parse(created.Body!)!.ToJsonString());
            Assert.Equal((1L, "Kaffet tog slut"), ((long?)made["id"], (string?)made["subject"]));

            var changing = new Taiga(sent => sent.Method == "GET" ? sent.Url.Contains("/issue-statuses?", StringComparison.Ordinal) ? Ok("[{\"id\":2,\"name\":\"In progress\"}]") : Ok(Issue) : Ok(Issue.Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal)));
            var changed = Assert.Single(await Run(changing, "issue_update", new() { ["issue"] = "1", ["status"] = "In progress", ["subject"] = "Kaffet tog slut igen" })).Fields;
            Assert.Equal(new[] { ("GET", Api("/issues/1")), ("GET", Api("/issue-statuses?project=2&page=1&page_size=100")), ("PATCH", Api("/issues/1")) }, changing.Calls.Select(sent => (sent.Method, sent.Url)));
            Assert.Equal(JsonNode.Parse("{\"subject\":\"Kaffet tog slut igen\",\"status\":2,\"version\":3}")!.ToJsonString(), JsonNode.Parse(changing.Calls[^1].Body!)!.ToJsonString()); // the version read, as Taiga requires
            Assert.Equal(4L, (long?)changed["version"]);

            var epic = new Taiga(_ => (HttpStatusCode.Created, "{\"id\":1,\"subject\":\"Ny kassa\",\"color\":\"#0000FF\",\"tags\":[]}", null));
            await Run(epic, "epic_create", new() { ["project"] = "2", ["subject"] = "Ny kassa", ["color"] = "0000ff", ["status"] = "7" });
            Assert.Equal(JsonNode.Parse("{\"project\":2,\"subject\":\"Ny kassa\",\"status\":7,\"color\":\"#0000FF\"}")!.ToJsonString(), JsonNode.Parse(epic.Calls.Single().Body!)!.ToJsonString()); // a number taken as it is, nothing looked up
            var task = new Taiga(_ => (HttpStatusCode.Created, "{\"id\":3,\"subject\":\"Koppla kortläsaren\",\"tags\":[]}", null));
            await Run(task, "task_create", new() { ["project"] = "2", ["subject"] = "Koppla kortläsaren", ["user_story"] = "1", ["taskboard_order"] = "2" });
            Assert.Equal(JsonNode.Parse("{\"project\":2,\"subject\":\"Koppla kortläsaren\",\"user_story\":1,\"taskboard_order\":2}")!.ToJsonString(), JsonNode.Parse(task.Calls.Single().Body!)!.ToJsonString());
            Assert.Equal(Api("/tasks"), task.Calls.Single().Url);

            var deleted = new Taiga(_ => (HttpStatusCode.NoContent, string.Empty, null));
            Assert.Equal((4L, "user_story_delete"), ((long?)Assert.Single(await Run(deleted, "user_story_delete", new() { ["user_story"] = "4" })).Fields["id"], (string?)(await Run(deleted, "user_story_delete", new() { ["user_story"] = "4" }))[0].Fields["done"]));
            Assert.Equal(("DELETE", Api("/userstories/4")), (deleted.Calls[0].Method, deleted.Calls[0].Url));
        }

        [Fact]
        public async Task Taigas_message_is_said_and_an_answer_that_is_not_taigas_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "issue", Dictionary<string, string>? parameters = null) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Taiga(_ => (status, body, null)), operation, parameters ?? new() { ["issue"] = "1" }))).Message;

            Assert.Equal("Taiga answered HTTP 404: No Issue matches the given query.", await Failed(HttpStatusCode.NotFound, "{\"_error_message\":\"No Issue matches the given query.\"}"));
            Assert.Equal("Taiga answered HTTP 404. - it is not there, or the user may not see it.", await Failed(HttpStatusCode.NotFound, "{\"_error_message\": \"\"}")); // its answer for a project
            Assert.Equal("Taiga answered HTTP 400: version: The version doesn't match with the current one.", await Failed(HttpStatusCode.BadRequest, "{\"version\":\"The version doesn't match with the current one\"}"));
            Assert.Equal("Taiga answered HTTP 400: subject: This field is required.", await Failed(HttpStatusCode.BadRequest, "{\"subject\":[\"This field is required.\"]}"));
            Assert.Equal("Taiga answered HTTP 403: You do not have permission to perform this action. - Taiga says so too of an item that is not there.", await Failed(HttpStatusCode.Forbidden, "{\"_error_message\":\"You do not have permission to perform this action.\",\"_error_type\":\"taiga.base.exceptions.PermissionDenied\"}", "issue_delete"));
            Assert.Equal("Taiga answered HTTP 403: You do not have permission to perform this action. - the user may not do this in that project.", await Failed(HttpStatusCode.Forbidden, "{\"_error_message\":\"You do not have permission to perform this action.\"}"));
            Assert.Equal("Taiga answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Taiga</html>"));
            Assert.Equal("Taiga answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"id\":1}", "projects", new()));
        }

        [Fact]
        public async Task A_test_names_the_user_and_a_refused_sign_in_is_said()
        {
            var connector = new TaigaConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Taiga(_ => Ok("{}"))), Address, null, Credentials));
            Assert.Equal((true, "Taiga answers: signed in as kafe."), (ok.Success, ok.Message));
            var refused = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Refusing()), Address, null, Credentials));
            Assert.Equal((false, "Taiga refused the sign-in (HTTP 401): No active account found with the given credentials. - check the username and the password."), (refused.Success, refused.Message));
        }

        private sealed class Refusing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"No active account found with the given credentials\",\"code\":\"invalid_credentials\"}", Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>
    /// Taiga LIVE, against this machine's throwaway Taiga 6.10 (scripts/taiga-test-server.ps1) - fail-not-skip without it
    /// (fails rather than skips): a test signing in and an unknown user refused in Taiga's words; a project of the test's own, an epic, a user story
    /// and its task, and an issue made - its priority and assignee by name - changed by status name with the version read
    /// first, listed by tag and by Taiga's own closed filter, page by page; a task listed by its user story, where n8n's filter
    /// returned every task; everything deleted again, and a list for a project that is not there refused.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class TaigaConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> SignIn => new() { ["username"] = TaigaTestEndpoint.Username, ["password"] = TaigaTestEndpoint.Password };

        private static HostConnectorEgress Egress => new(new[] { new Uri(TaigaTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new TaigaConnector(), Contexts.Extract(Egress, TaigaTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), SignIn, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        [Fact]
        public async Task A_test_signs_in_and_an_unknown_user_is_refused()
        {
            var tested = await new TaigaConnector().TestConnectionAsync(Contexts.Test(Egress, TaigaTestEndpoint.Endpoint, credentials: SignIn, timeoutSeconds: 60));
            Assert.Equal((true, $"Taiga answers: signed in as {TaigaTestEndpoint.Username}."), (tested.Success, tested.Message));
            var wrong = await new TaigaConnector().TestConnectionAsync(Contexts.Test(Egress, TaigaTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["username"] = "nobody-" + Guid.NewGuid().ToString("N")[..8], ["password"] = "x" }, timeoutSeconds: 60));
            Assert.Equal((false, "Taiga refused the sign-in (HTTP 401): No active account found with the given credentials. - check the username and the password."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_projects_work_is_made_changed_found_and_deleted()
        {
            var project = await TaigaTestEndpoint.MakeProjectAsync("Kafé " + Guid.NewGuid().ToString("N")[..8]);
            output.WriteLine($"project {project}");
            try
            {
                var p = project.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var epic = await One("epic_create", new() { ["project"] = p, ["subject"] = "Ny kassa", ["color"] = "#0000FF" });
                var story = await One("user_story_create", new() { ["project"] = p, ["subject"] = "Som kund vill jag betala med kort" });
                var storyId = story["id"]!.ToString()!;
                var task = await One("task_create", new() { ["project"] = p, ["subject"] = "Koppla kortläsaren", ["user_story"] = storyId });
                await One("task_create", new() { ["project"] = p, ["subject"] = "Utan berättelse" });
                Assert.Equal(task["id"], Assert.Single(await All("tasks", new() { ["project"] = p, ["user_story"] = storyId })).Fields["id"]); // n8n's userStory returned both

                var issue = await One("issue_create", new() { ["project"] = p, ["subject"] = "Kaffet tog slut", ["description"] = "Två säckar", ["priority"] = "High", ["assigned_to"] = TaigaTestEndpoint.Username, ["tags"] = "kaffe, kassa" });
                var issueId = issue["id"]!.ToString()!;
                Assert.Equal(("[\"kaffe\",\"kassa\"]", "New"), ((string?)issue["tags"], (string?)issue["status_name"]));
                var changed = await One("issue_update", new() { ["issue"] = issueId, ["status"] = "Closed", ["subject"] = "Kaffet tog slut igen" });
                Assert.Equal(("Closed", "Kaffet tog slut igen"), ((string?)changed["status_name"], (string?)changed["subject"]));
                Assert.Equal("Kaffet tog slut igen", (string?)(await One("issue", new() { ["issue"] = issueId }))["subject"]);
                Assert.Single(await All("issues", new() { ["project"] = p, ["tags"] = "kaffe", ["closed"] = "true" }));
                Assert.Empty(await All("issues", new() { ["project"] = p, ["closed"] = "false" })); // n8n's statusIsClosed returned the closed one too

                for (var i = 0; i < 3; i++)
                {
                    await One("issue_create", new() { ["project"] = p, ["subject"] = $"Fler {i}" });
                }

                Assert.Equal(4, (await All("issues", new() { ["project"] = p, ["max"] = "10" })).Count);
                Assert.Equal(3, (await All("issues", new() { ["project"] = p, ["max"] = "3" })).Count);

                await One("task_delete", new() { ["task"] = task["id"]!.ToString()! });
                Assert.Equal($"Taiga answered HTTP 404: No Task matches the given query.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("task", new() { ["task"] = task["id"]!.ToString()! }))).Message);
                await One("user_story_delete", new() { ["user_story"] = storyId });
                await One("epic_delete", new() { ["epic"] = epic["id"]!.ToString()! });
                await One("issue_delete", new() { ["issue"] = issueId });
            }
            finally
            {
                await TaigaTestEndpoint.DeleteProjectAsync(project);
            }

            Assert.Equal("Taiga answered HTTP 404. - it is not there, or the user may not see it.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("issues", new() { ["project"] = project.ToString(System.Globalization.CultureInfo.InvariantCulture) }))).Message); // Taiga says nothing of a project
        }
    }
}
