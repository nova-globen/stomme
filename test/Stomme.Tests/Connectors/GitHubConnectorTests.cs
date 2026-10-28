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
using Stomme.Modules.Connectors.GitHub;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - GitHub (n8n's GitHub node: an issue created, edited, got and commented on, a repository's issues got, a release
    /// created, a file got, created and edited, a workflow dispatched): a personal access token as a bearer, with the headers
    /// GitHub asks for (its media type, its API version, a User-Agent); a repository's issues read page by page by its Link header
    /// up to the most asked for - never to another host - each a plain record, a pull request said; an issue made, read, changed and
    /// commented on; a release published; a text file read and committed over the sha it had; a workflow started with its inputs as
    /// text; an owner, a repository, an issue, a workflow or a path that could leave the repository refused before anything is
    /// sent; GitHub's refusal its message and its first error; a test naming the account.
    /// </summary>
    public sealed class GitHubConnectorTests
    {
        private const string Api = "https://api.github.example";

        private static readonly Dictionary<string, string> Token = new() { ["token"] = "ghp_test123" };

        private sealed class GitHub : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = new();

            public List<HttpRequestMessage> Sent { get; } = new();

            public Func<HttpMethod, string, string?, (HttpStatusCode Status, string Body, string? Link)> Answer { get; set; } = (_, _, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(), body));
                Sent.Add(request);
                var (status, answer, link) = Answer(request.Method, url, body);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (link is not null)
                {
                    response.Headers.TryAddWithoutValidation("Link", link);
                }

                return response;
            }
        }

        private static ConnectorExtractContext Context(GitHub github, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(github), Api, operation, parameters, credentials: Token);

        private static Dictionary<string, string> Shop(params (string Name, string Value)[] more)
        {
            var parameters = new Dictionary<string, string> { ["owner"] = "acme", ["repository"] = "shop" };
            foreach (var (name, value) in more)
            {
                parameters[name] = value;
            }

            return parameters;
        }

        private static string Issue(int number, string title, bool pull = false) =>
            """{"number":@N,"title":"@T","state":"open","html_url":"https://github.example/acme/shop/issues/@N","user":{"login":"ada"},"labels":[{"name":"bug"},{"name":"billing"}],"assignees":[{"login":"grace"},{"login":"linus"}],"body":"Twelve, please.","comments":2,"created_at":"2026-10-01T10:00:00Z","updated_at":"2026-10-02T10:00:00Z","closed_at":null@PR}"""
                .Replace("@N", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("@T", title, StringComparison.Ordinal)
                .Replace("@PR", pull ? ",\"pull_request\":{\"url\":\"x\"}" : "", StringComparison.Ordinal);

        [Fact]
        public async Task A_repositorys_issues_are_read_page_by_page_by_the_link_header_up_to_the_most_asked_for()
        {
            var github = new GitHub
            {
                Answer = (_, url, _) => url.Contains("page=2", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, "[" + Issue(3, "Order 1003", pull: true) + "," + Issue(4, "Order 1004") + "]", $"<{Api}/repositories/1/issues?state=all&per_page=3&page=3>; rel=\"next\"")
                    : (HttpStatusCode.OK, "[" + Issue(1, "Order 1001") + "," + Issue(2, "Order 1002") + "]", $"<{Api}/repositories/1/issues?state=all&per_page=3&page=2>; rel=\"next\", <{Api}/repositories/1/issues?state=all&per_page=3&page=5>; rel=\"last\""),
            };
            var records = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.IssuesOperation,
                Shop(("state", "all"), ("labels", "bug, billing"), ("assignee", "grace"), ("since", "2026-10-01"), ("max", "3"))));

            Assert.Equal(new long?[] { 1, 2, 3 }, records.Select(record => (long?)record.Fields["number"])); // the most asked for: no page more
            Assert.Equal(2, github.Requests.Count);
            Assert.Equal($"{Api}/repos/acme/shop/issues?state=all&per_page=3&labels=bug%2Cbilling&assignee=grace&since=2026-10-01T00%3A00%3A00Z", github.Requests[0].Url);
            Assert.Equal($"{Api}/repositories/1/issues?state=all&per_page=3&page=2", github.Requests[1].Url); // GitHub's own next page
            var sent = github.Sent[0];
            Assert.Equal(("Bearer ghp_test123", "application/vnd.github+json", "2022-11-28", "Stomme"),
                (github.Requests[0].Authorization, sent.Headers.Accept.Single().MediaType, sent.Headers.GetValues("X-GitHub-Api-Version").Single(), sent.Headers.UserAgent.ToString()));
            var first = records[0].Fields;
            Assert.Equal(("Order 1001", "open", "https://github.example/acme/shop/issues/1", "ada", "bug, billing", "grace, linus", "Twelve, please.", 2L, false),
                ((string?)first["title"], (string?)first["state"], (string?)first["url"], (string?)first["author"], (string?)first["labels"], (string?)first["assignees"], (string?)first["body"], (long?)first["comments"], (bool?)first["pull_request"]));
            Assert.True((bool?)records[2].Fields["pull_request"]); // a pull request is an issue to GitHub, and says so

            var elsewhere = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, "[" + Issue(1, "Order 1001") + "]", "<https://elsewhere.example/issues?page=2>; rel=\"next\"") };
            Assert.Single(await Contexts.DrainAsync(new GitHubConnector(), Context(elsewhere, GitHubConnector.IssuesOperation, Shop())));
            Assert.Single(elsewhere.Requests); // a next page on another host is not followed - the token would go with it
            Assert.Equal($"{Api}/repos/acme/shop/issues?state=open&per_page=100", elsewhere.Requests[0].Url);
        }

        [Fact]
        public async Task An_issue_is_created_read_changed_and_commented_on()
        {
            var github = new GitHub
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("POST", _) when url.EndsWith("/comments", StringComparison.Ordinal) => (HttpStatusCode.Created, """{"id":777,"html_url":"https://github.example/acme/shop/issues/42#issuecomment-777"}""", null),
                    ("POST", _) => (HttpStatusCode.Created, """{"number":42,"html_url":"https://github.example/acme/shop/issues/42"}""", null),
                    ("PATCH", _) => (HttpStatusCode.OK, """{"number":42,"html_url":"https://github.example/acme/shop/issues/42","state":"closed"}""", null),
                    _ => (HttpStatusCode.OK, Issue(42, "Order 1001 stuck"), null),
                },
            };

            var created = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.CreateIssueOperation, new()
            {
                ["repository"] = "acme/shop", ["title"] = "Order 1001 stuck", ["body"] = "Twelve, please.\nThanks.", ["labels"] = "bug, billing", ["assignees"] = "grace",
            }));
            Assert.Equal((HttpMethod.Post, $"{Api}/repos/acme/shop/issues"), (github.Requests[0].Method, github.Requests[0].Url)); // owner/name
            Assert.Equal("""{"title":"Order 1001 stuck","body":"Twelve, please.\nThanks.","labels":["bug","billing"],"assignees":["grace"]}""", github.Requests[0].Body);
            Assert.Equal((42L, "https://github.example/acme/shop/issues/42"), ((long?)Assert.Single(created).Fields["number"], (string?)created[0].Fields["url"]));

            var read = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.GetIssueOperation, new() { ["repository"] = "https://github.com/acme/shop.git", ["issue"] = "#42" }));
            Assert.Equal($"{Api}/repos/acme/shop/issues/42", github.Requests[1].Url); // its address and a #number forgiven
            Assert.Equal("Order 1001 stuck", Assert.Single(read).Fields["title"]);

            var changed = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.UpdateIssueOperation, Shop(("issue", "42"), ("state", "Closed"), ("state_reason", "completed"), ("labels", "done"))));
            Assert.Equal((HttpMethod.Patch, $"{Api}/repos/acme/shop/issues/42", """{"labels":["done"],"state":"closed","state_reason":"completed"}"""), (github.Requests[2].Method, github.Requests[2].Url, github.Requests[2].Body));
            Assert.Equal(("closed", "labels, state, state_reason"), ((string?)Assert.Single(changed).Fields["state"], (string?)changed[0].Fields["updated"]));

            var comment = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.CommentOperation, Shop(("issue", "42"), ("body", "Shipped."))));
            Assert.Equal(($"{Api}/repos/acme/shop/issues/42/comments", """{"body":"Shipped."}"""), (github.Requests[3].Url, github.Requests[3].Body));
            Assert.Equal((42L, 777L), ((long?)Assert.Single(comment).Fields["issue"], (long?)comment[0].Fields["comment_id"]));
        }

        [Fact]
        public async Task A_release_is_published_a_file_read_and_committed_over_its_sha_and_a_workflow_started()
        {
            var github = new GitHub
            {
                Answer = (method, url, _) => (method.Method, url) switch
                {
                    ("POST", _) when url.EndsWith("/releases", StringComparison.Ordinal) => (HttpStatusCode.Created, """{"id":9,"tag_name":"v1.2.3","html_url":"https://github.example/acme/shop/releases/tag/v1.2.3"}""", null),
                    ("POST", _) => (HttpStatusCode.NoContent, "", null),
                    ("GET", _) when url.Contains("/contents/docs/new.md", StringComparison.Ordinal) => (HttpStatusCode.NotFound, """{"message":"Not Found"}""", null),
                    ("GET", _) => (HttpStatusCode.OK, """{"type":"file","encoding":"base64","path":"docs/status page.md","sha":"abc123","size":12,"html_url":"https://github.example/acme/shop/blob/main/docs/status%20page.md","content":"SGVsbG8s\nIHdvcmxkIQ==\n"}""", null),
                    _ => (HttpStatusCode.OK, """{"content":{"path":"docs/status page.md","sha":"def456"},"commit":{"sha":"c0ffee","html_url":"https://github.example/acme/shop/commit/c0ffee"}}""", null),
                },
            };

            var release = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.ReleaseOperation, Shop(("tag", "v1.2.3"), ("name", "Shop 1.2.3"), ("body", "Faster checkout."), ("prerelease", "true"), ("target", "main"))));
            Assert.Equal(($"{Api}/repos/acme/shop/releases", """{"tag_name":"v1.2.3","draft":false,"prerelease":true,"name":"Shop 1.2.3","body":"Faster checkout.","target_commitish":"main"}"""), (github.Requests[0].Url, github.Requests[0].Body));
            Assert.Equal((9L, "https://github.example/acme/shop/releases/tag/v1.2.3"), ((long?)Assert.Single(release).Fields["release_id"], (string?)release[0].Fields["url"]));

            var file = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.FileOperation, Shop(("path", "/docs/status page.md"), ("ref", "release/1.2"))));
            Assert.Equal($"{Api}/repos/acme/shop/contents/docs/status%20page.md?ref=release%2F1.2", github.Requests[1].Url); // each segment escaped
            Assert.Equal(("Hello, world!", "abc123", 12L), ((string?)Assert.Single(file).Fields["content"], (string?)file[0].Fields["sha"], (long?)file[0].Fields["size"]));

            var replaced = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.CommitFileOperation, Shop(("path", "docs/status page.md"), ("content", "All good."), ("message", "Status"), ("branch", "main"))));
            Assert.Equal(($"{Api}/repos/acme/shop/contents/docs/status%20page.md?ref=main", HttpMethod.Put), (github.Requests[2].Url, github.Requests[3].Method)); // the sha read first
            Assert.Equal("""{"message":"Status","content":"QWxsIGdvb2Qu","branch":"main","sha":"abc123"}""", github.Requests[3].Body);
            Assert.Equal(("c0ffee", false), ((string?)Assert.Single(replaced).Fields["commit"], (bool?)replaced[0].Fields["created"]));
            var made = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.CommitFileOperation, Shop(("path", "docs/new.md"), ("content", "New."), ("message", "Add"))));
            Assert.Equal("""{"message":"Add","content":"TmV3Lg=="}""", github.Requests[5].Body); // none there: created, no sha
            Assert.True((bool?)Assert.Single(made).Fields["created"]);

            var started = await Contexts.DrainAsync(new GitHubConnector(), Context(github, GitHubConnector.DispatchOperation, Shop(("workflow", "deploy.yml"), ("ref", "main"), ("inputs", """{"environment":"uat","count":3}"""))));
            Assert.Equal(($"{Api}/repos/acme/shop/actions/workflows/deploy.yml/dispatches", """{"ref":"main","inputs":{"environment":"uat","count":"3"}}"""), (github.Requests[6].Url, github.Requests[6].Body)); // each input as text
            Assert.True((bool?)Assert.Single(started).Fields["dispatched"]);
        }

        [Fact]
        public async Task What_could_leave_the_repository_is_refused_before_anything_is_sent_and_githubs_refusal_is_its_message()
        {
            var github = new GitHub();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(github, operation, parameters)))).Message;

            Assert.StartsWith("'../acme' is not a GitHub owner", await Refused(GitHubConnector.IssuesOperation, new() { ["owner"] = "../acme", ["repository"] = "shop" }));
            Assert.StartsWith("'shop/../../x' is not a GitHub repository's name", await Refused(GitHubConnector.IssuesOperation, new() { ["owner"] = "acme", ["repository"] = "shop/../../x" }));
            Assert.StartsWith("'..' is not a GitHub repository's name", await Refused(GitHubConnector.IssuesOperation, new() { ["owner"] = "acme", ["repository"] = ".." }));
            Assert.StartsWith("'1/../2' is not an issue's number", await Refused(GitHubConnector.CommentOperation, Shop(("issue", "1/../2"), ("body", "x"))));
            Assert.StartsWith("'../deploy.yml' is not a workflow", await Refused(GitHubConnector.DispatchOperation, Shop(("workflow", "../deploy.yml"), ("ref", "main"))));
            Assert.StartsWith("'docs/../../secrets.md' is not a file's path", await Refused(GitHubConnector.FileOperation, Shop(("path", "docs/../../secrets.md"))));
            Assert.Equal("The title has a line break - one line only.", await Refused(GitHubConnector.CreateIssueOperation, Shop(("title", "Order\nstuck"))));
            Assert.Equal("Give something to change - title, body, state, labels or assignees.", await Refused(GitHubConnector.UpdateIssueOperation, Shop(("issue", "42"))));
            Assert.StartsWith("'merged' is not a state", await Refused(GitHubConnector.UpdateIssueOperation, Shop(("issue", "42"), ("state", "merged"))));
            Assert.Equal("The inputs are not JSON - {\"environment\": \"uat\"}.", await Refused(GitHubConnector.DispatchOperation, Shop(("workflow", "12345"), ("ref", "main"), ("inputs", "environment=uat"))));
            Assert.Empty(github.Requests);

            var binary = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """{"type":"file","encoding":"base64","path":"logo.png","sha":"a","size":4,"content":"/9j/4A=="}""", null) };
            Assert.Equal("'logo.png' is not a text file.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(binary, GitHubConnector.FileOperation, Shop(("path", "logo.png")))))).Message);
            var folder = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """[{"type":"file","path":"docs/a.md"}]""", null) };
            Assert.StartsWith("'docs' is a folder", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(folder, GitHubConnector.FileOperation, Shop(("path", "docs")))))).Message);
            var submodule = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """{"type":"submodule","path":"vendor/lib","sha":"a","size":0}""", null) };
            Assert.Equal("'vendor/lib' is a submodule, not a file.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(submodule, GitHubConnector.FileOperation, Shop(("path", "vendor/lib")))))).Message);
            var big = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """{"type":"file","encoding":"none","path":"exports/orders.csv","sha":"a","size":2000000,"content":""}""", null) };
            Assert.StartsWith("'exports/orders.csv' is over the 1 MB", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(big, GitHubConnector.FileOperation, Shop(("path", "exports/orders.csv")))))).Message);
            var oddName = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """{"type":"file","encoding":"base64","path":"notes/q&a #1.md","sha":"a","size":2,"content":"aGk="}""", null) };
            await Contexts.DrainAsync(new GitHubConnector(), Context(oddName, GitHubConnector.FileOperation, Shop(("path", "notes/q&a #1.md"))));
            Assert.Equal($"{Api}/repos/acme/shop/contents/notes/q%26a%20%231.md", oddName.Requests[0].Url); // a # or & in a name stays the name's

            var refusing = new GitHub { Answer = (_, _, _) => (HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"resource":"Issue","code":"missing_field","field":"title"}]}""", null) };
            Assert.Equal("HTTP 422: Validation Failed (Issue title missing_field)", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new GitHubConnector(), Context(refusing, GitHubConnector.CreateIssueOperation, Shop(("title", "x")))))).Message);
        }

        [Fact]
        public async Task A_test_names_the_account()
        {
            var github = new GitHub { Answer = (_, _, _) => (HttpStatusCode.OK, """{"login":"ops-bot","id":1}""", null) };
            var tested = await new GitHubConnector().TestConnectionAsync(Contexts.Test(new StubEgress(github), Api + "/", credentials: Token));
            Assert.Equal((true, "Signed in as ops-bot."), (tested.Success, tested.Message));
            Assert.Equal(($"{Api}/user", "Bearer ghp_test123"), (github.Requests[0].Url, github.Requests[0].Authorization));
            var refused = await new GitHubConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new GitHub { Answer = (_, _, _) => (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""", null) }), Api, credentials: Token));
            Assert.Equal((false, "HTTP 401: Bad credentials"), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "github", ConnectorCategory.Development), (new GitHubConnector().AuthType, new GitHubConnector().ConnectorType, Assert.Single(new GitHubConnector().Categories)));
            Assert.Equal(new[] { "issues", "get_issue", "create_issue", "update_issue", "comment", "release", "file", "commit_file", "dispatch" }, new GitHubConnector().Operations.Select(operation => operation.Name));
        }
    }
}
