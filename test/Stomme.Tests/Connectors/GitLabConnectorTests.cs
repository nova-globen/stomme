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
using Stomme.Modules.Connectors.GitLab;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// GitLab (n8n's GitLab node: issues, a comment, a release, files - and merge requests and pipelines) over its REST API:
    /// the token in GitLab's PRIVATE-TOKEN header; any page's address cut back to the instance; a project by its path (subgroups
    /// too), its id or its address; issues by state, labels, assignee, search and change, newest change first, page by page by
    /// X-Next-Page up to the most asked for; an issue got, created and updated with its assignees by username (looked up) or id,
    /// closed or reopened, labels added and removed; a comment; merge requests; a release; a text file read and committed -
    /// created, or updated when it is there; a pipeline run with its variables; what is not a project, a number, a username, a
    /// ref, a path, a date, a flag, a state or one line refused before anything is sent; GitLab's refusal its words, a field's
    /// errors named; a test naming the user.
    /// </summary>
    public sealed class GitLabConnectorTests
    {
        private const string Api = "https://gitlab.com/api/v4";

        private const string Token = "glpat-test-1";

        private sealed class GitLabApi : HttpMessageHandler
        {
            public List<(HttpMethod Method, string Url, string? Token, string? Body)> Requests { get; } = new();

            public Func<HttpMethod, string, (HttpStatusCode Status, string Body, string? Next)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}", null);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                Requests.Add((request.Method, url, request.Headers.TryGetValues("PRIVATE-TOKEN", out var tokens) ? tokens.Single() : null, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var (status, answer, next) = Answer(request.Method, url);
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                response.Headers.Add("X-Next-Page", next ?? "");
                return response;
            }
        }

        private static ConnectorExtractContext Context(GitLabApi gitlab, string operation, Dictionary<string, string> parameters, string endpoint = "https://gitlab.com") =>
            Contexts.Extract(new StubEgress(gitlab), endpoint, operation, parameters, null, new Dictionary<string, string> { ["token"] = Token });

        private static GitLabConnector Connector() => new();

        private static string Issues(int from, int count) =>
            "[" + string.Join(",", Enumerable.Range(from, count).Select(iid => "{\"id\":" + (9000 + iid) + ",\"iid\":" + iid + ",\"title\":\"Fix " + iid + "\",\"state\":\"opened\",\"web_url\":\"https://gitlab.com/acme/shop/-/issues/" + iid + "\",\"author\":{\"username\":\"ada\"},"
                + "\"labels\":[\"bug\",\"customer\"],\"assignees\":[{\"username\":\"grace\"}],\"description\":\"It broke.\",\"due_date\":\"2026-10-31\",\"confidential\":false,\"created_at\":\"2026-10-01T10:00:00.000Z\",\"updated_at\":\"2026-10-02T10:00:00.000Z\",\"closed_at\":null}")) + "]";

        [Fact]
        public async Task A_projects_issues_are_read_page_by_page_up_to_the_most_asked_for_the_token_in_gitlabs_header()
        {
            var gitlab = new GitLabApi { Answer = (_, url) => url.EndsWith("&page=1", StringComparison.Ordinal) ? (HttpStatusCode.OK, Issues(1, 100), "2") : (HttpStatusCode.OK, Issues(101, 50), "3") };
            var records = await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.IssuesOperation, new()
            {
                ["project"] = "acme/team/shop", ["state"] = "Opened", ["labels"] = "bug, customer", ["assignee"] = "@grace", ["search"] = "printer fire", ["updated_since"] = "2026-10-01T02:00:00+02:00", ["max"] = "150",
            }, endpoint: "https://gitlab.com/acme/team/shop/-/issues"));

            Assert.Equal(150, records.Count); // the most asked for
            Assert.Equal(2, gitlab.Requests.Count);
            Assert.Equal(Api + "/projects/acme%2Fteam%2Fshop/issues?state=opened&labels=bug%2Ccustomer&assignee_username=grace&search=printer%20fire&updated_after=2026-10-01T00%3A00%3A00Z&order_by=updated_at&sort=desc&per_page=100&page=1", gitlab.Requests[0].Url); // a page's address: the instance's
            Assert.EndsWith("&per_page=50&page=2", gitlab.Requests[1].Url, StringComparison.Ordinal); // GitLab's next page, no bigger than what is left
            Assert.All(gitlab.Requests, request => Assert.Equal(Token, request.Token));
            var issue = records[0].Fields;
            Assert.Equal((1L, "Fix 1", "opened", "https://gitlab.com/acme/shop/-/issues/1", "ada", "bug, customer", "grace", "2026-10-31"),
                ((long?)issue["iid"], (string?)issue["title"], (string?)issue["state"], (string?)issue["url"], (string?)issue["author"], (string?)issue["labels"], (string?)issue["assignees"], (string?)issue["due_date"]));

            var last = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, Issues(1, 100), null) };
            Assert.Equal(100, (await Contexts.DrainAsync(Connector(), Context(last, GitLabConnector.IssuesOperation, new() { ["project"] = "42" }, endpoint: "http://127.0.0.1:5274/"))).Count);
            Assert.Equal("http://127.0.0.1:5274/api/v4/projects/42/issues?state=opened&order_by=updated_at&sort=desc&per_page=100&page=1", Assert.Single(last.Requests).Url); // an id; no next page: the last

            var ten = new GitLabApi { Answer = (_, url) => (HttpStatusCode.OK, Issues(1, 100), (int.Parse(url[(url.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            Assert.Equal(1000, (await Contexts.DrainAsync(Connector(), Context(ten, GitLabConnector.IssuesOperation, new() { ["project"] = "https://gitlab.com/acme/shop.git" }))).Count); // by default up to 1,000
            Assert.Equal(10, ten.Requests.Count);
            Assert.StartsWith(Api + "/projects/acme%2Fshop/issues?", ten.Requests[0].Url, StringComparison.Ordinal); // a clone address: its path
            var page = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, "[]", null) };
            await Contexts.DrainAsync(Connector(), Context(page, GitLabConnector.IssuesOperation, new() { ["project"] = "https://gitlab.com/acme/team/shop/-/merge_requests/7" }));
            Assert.StartsWith(Api + "/projects/acme%2Fteam%2Fshop/issues?", page.Requests[0].Url, StringComparison.Ordinal); // a page's address: the project before /-/

            var requests = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, "[{\"id\":1,\"iid\":7,\"title\":\"Faster checkout\",\"state\":\"merged\",\"web_url\":\"https://gitlab.com/acme/shop/-/merge_requests/7\",\"author\":{\"username\":\"ada\"},\"source_branch\":\"checkout\",\"target_branch\":\"main\",\"labels\":[\"perf\"],\"draft\":false,\"merged_at\":\"2026-10-03T10:00:00.000Z\"}]", null) };
            var merged = Assert.Single(await Contexts.DrainAsync(Connector(), Context(requests, GitLabConnector.MergeRequestsOperation, new() { ["project"] = "acme/shop", ["state"] = "merged", ["target_branch"] = "main", ["labels"] = "perf" }))).Fields;
            Assert.Equal(Api + "/projects/acme%2Fshop/merge_requests?state=merged&labels=perf&target_branch=main&order_by=updated_at&sort=desc&per_page=100&page=1", requests.Requests[0].Url);
            Assert.Equal((7L, "merged", "checkout", "main", "perf"), ((long?)merged["iid"], (string?)merged["state"], (string?)merged["source_branch"], (string?)merged["target_branch"], (string?)merged["labels"]));
        }

        [Fact]
        public async Task An_issue_is_got_created_updated_and_commented_on_and_a_release_files_and_a_pipeline()
        {
            var gitlab = new GitLabApi
            {
                Answer = (method, url) => (HttpStatusCode.OK, url switch
                {
                    _ when url.Contains("/users?username=grace", StringComparison.Ordinal) => "[{\"id\":21,\"username\":\"grace\"}]",
                    _ when url.Contains("/users?username=", StringComparison.Ordinal) => "[]",
                    _ when url.EndsWith("/notes", StringComparison.Ordinal) => "{\"id\":501,\"body\":\"Looking.\",\"author\":{\"username\":\"ada\"},\"created_at\":\"2026-10-12T08:00:00.000Z\"}",
                    _ when url.EndsWith("/releases", StringComparison.Ordinal) => "{\"tag_name\":\"v1.2.3\",\"name\":\"v1.2.3\",\"_links\":{\"self\":\"https://gitlab.com/acme/shop/-/releases/v1.2.3\"},\"commit\":{\"id\":\"abc123\"},\"created_at\":\"2026-10-12T08:00:00.000Z\"}",
                    _ when url.Contains("/repository/files/", StringComparison.Ordinal) && method == HttpMethod.Get => "{\"file_path\":\"docs/README.md\",\"ref\":\"main\",\"size\":12,\"last_commit_id\":\"abc123\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello, värld")) + "\"}",
                    _ when url.Contains("/repository/files/", StringComparison.Ordinal) => "{\"file_path\":\"docs/README.md\",\"branch\":\"main\"}",
                    _ when url.EndsWith("/pipeline", StringComparison.Ordinal) => "{\"id\":777,\"status\":\"created\",\"ref\":\"main\",\"sha\":\"abc123\",\"web_url\":\"https://gitlab.com/acme/shop/-/pipelines/777\"}",
                    _ => Issues(12, 1)[1..^1],
                }, null),
            };
            var project = Api + "/projects/acme%2Fshop";

            Assert.Equal(12L, Assert.Single(await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.GetIssueOperation, new() { ["project"] = "acme/shop", ["issue"] = "#12" }))).Fields["iid"]);
            Assert.Equal((HttpMethod.Get, project + "/issues/12"), (gitlab.Requests[^1].Method, gitlab.Requests[^1].Url));

            await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.CreateIssueOperation, new()
            {
                ["project"] = "acme/shop", ["title"] = "Printer on fire", ["description"] = "It broke.\nTwice.", ["labels"] = "bug, customer", ["add_labels"] = "ignored", ["remove_labels"] = "ignored", ["assignees"] = "@grace, 33", ["due_date"] = "2026-10-31", ["confidential"] = "TRUE",
            }));
            Assert.Equal((HttpMethod.Get, Api + "/users?username=grace"), (gitlab.Requests[^2].Method, gitlab.Requests[^2].Url)); // a username looked up; an id taken as it is
            Assert.Equal((HttpMethod.Post, project + "/issues", "{\"title\":\"Printer on fire\",\"description\":\"It broke.\\nTwice.\",\"labels\":\"bug,customer\",\"due_date\":\"2026-10-31\",\"confidential\":true,\"assignee_ids\":[21,33]}"),
                (gitlab.Requests[^1].Method, gitlab.Requests[^1].Url, gitlab.Requests[^1].Body));

            gitlab.Requests.Clear();
            await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.UpdateIssueOperation, new() { ["project"] = "acme/shop", ["issue"] = "12", ["state"] = "Close", ["add_labels"] = "done", ["remove_labels"] = "bug" }));
            Assert.Equal((HttpMethod.Put, project + "/issues/12", "{\"add_labels\":\"done\",\"remove_labels\":\"bug\",\"state_event\":\"close\"}"), (Assert.Single(gitlab.Requests).Method, gitlab.Requests[0].Url, gitlab.Requests[0].Body)); // nothing looked up

            var note = Assert.Single(await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.CommentOperation, new() { ["project"] = "acme/shop", ["issue"] = "12", ["body"] = "Looking." })));
            Assert.Equal((HttpMethod.Post, project + "/issues/12/notes", "{\"body\":\"Looking.\"}"), (gitlab.Requests[^1].Method, gitlab.Requests[^1].Url, gitlab.Requests[^1].Body));
            Assert.Equal((501L, 12L, "ada"), ((long?)note.Fields["id"], (long?)note.Fields["issue"], (string?)note.Fields["author"]));

            var release = Assert.Single(await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.ReleaseOperation, new() { ["project"] = "acme/shop", ["tag"] = "v1.2.3", ["description"] = "Fixes.", ["ref"] = "main" })));
            Assert.Equal((HttpMethod.Post, project + "/releases", "{\"tag_name\":\"v1.2.3\",\"name\":\"v1.2.3\",\"description\":\"Fixes.\",\"ref\":\"main\"}"), (gitlab.Requests[^1].Method, gitlab.Requests[^1].Url, gitlab.Requests[^1].Body)); // no name: the tag
            Assert.Equal(("v1.2.3", "https://gitlab.com/acme/shop/-/releases/v1.2.3", "abc123"), ((string?)release.Fields["tag"], (string?)release.Fields["url"], (string?)release.Fields["commit"]));

            var file = Assert.Single(await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.FileOperation, new() { ["project"] = "acme/shop", ["path"] = "/docs/README.md" })));
            Assert.Equal(project + "/repository/files/docs%2FREADME.md?ref=main", gitlab.Requests[^1].Url); // the path one segment, main by default
            Assert.Equal(("docs/README.md", "Hello, värld", 12L), ((string?)file.Fields["path"], (string?)file.Fields["content"], (long?)file.Fields["size"]));
            await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.FileOperation, new() { ["project"] = "acme/shop", ["path"] = "docs/README.md", ["ref"] = "release/1.2" }));
            Assert.Equal(project + "/repository/files/docs%2FREADME.md?ref=release%2F1.2", gitlab.Requests[^1].Url); // a ref given

            gitlab.Requests.Clear();
            var created = Assert.Single(await Contexts.DrainAsync(Connector(), Context(new GitLabApi { Answer = gitlab.Answer }, GitLabConnector.PipelineOperation, new() { ["project"] = "acme/shop", ["ref"] = "main", ["variables"] = "{\"DEPLOY\": \"true\", \"COUNT\": 3}" })));
            Assert.Equal((777L, "created", "https://gitlab.com/acme/shop/-/pipelines/777"), ((long?)created.Fields["id"], (string?)created.Fields["status"], (string?)created.Fields["url"]));

            var pipelines = new GitLabApi { Answer = gitlab.Answer };
            await Contexts.DrainAsync(Connector(), Context(pipelines, GitLabConnector.PipelineOperation, new() { ["project"] = "acme/shop", ["ref"] = "release/1.2", ["variables"] = "{\"DEPLOY\": \"true\", \"COUNT\": 3}" }));
            Assert.Equal((HttpMethod.Post, project + "/pipeline", "{\"ref\":\"release/1.2\",\"variables\":[{\"key\":\"DEPLOY\",\"value\":\"true\"},{\"key\":\"COUNT\",\"value\":\"3\"}]}"), (pipelines.Requests[0].Method, pipelines.Requests[0].Url, pipelines.Requests[0].Body));
        }

        [Fact]
        public async Task A_file_is_committed_created_or_updated_as_gitlab_has_it()
        {
            foreach (var (there, method, created) in new[] { (HttpStatusCode.NotFound, HttpMethod.Post, true), (HttpStatusCode.OK, HttpMethod.Put, false) })
            {
                var gitlab = new GitLabApi { Answer = (asked, _) => asked == HttpMethod.Head ? (there, "", null) : (HttpStatusCode.OK, "{\"file_path\":\"docs/README.md\",\"branch\":\"main\"}", null) };
                var written = Assert.Single(await Contexts.DrainAsync(Connector(), Context(gitlab, GitLabConnector.CommitFileOperation, new() { ["project"] = "acme/shop", ["path"] = "docs/README.md", ["branch"] = "main", ["content"] = "Hello\nworld", ["message"] = "Docs" })));
                Assert.Equal((HttpMethod.Head, Api + "/projects/acme%2Fshop/repository/files/docs%2FREADME.md?ref=main"), (gitlab.Requests[0].Method, gitlab.Requests[0].Url));
                Assert.Equal((method, "{\"branch\":\"main\",\"content\":\"Hello\\nworld\",\"commit_message\":\"Docs\",\"encoding\":\"text\"}"), (gitlab.Requests[1].Method, gitlab.Requests[1].Body));
                Assert.Equal(("docs/README.md", "main", created), ((string?)written.Fields["path"], (string?)written.Fields["branch"], (bool?)written.Fields["created"]));
            }

            var forbidden = new GitLabApi { Answer = (_, _) => (HttpStatusCode.Forbidden, "", null) };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(forbidden, GitLabConnector.CommitFileOperation, new() { ["project"] = "acme/shop", ["path"] = "a.md", ["branch"] = "main", ["content"] = "x", ["message"] = "m" })))).Message);
            Assert.Single(forbidden.Requests); // nothing written after a refusal
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_sent_and_gitlabs_refusal_is_its_words()
        {
            var gitlab = new GitLabApi();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = "https://gitlab.com") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(gitlab, operation, parameters, endpoint)))).Message;

            Assert.Equal("'shop' is not a GitLab project - group/project, its id or its address.", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "shop" }));
            Assert.Equal("'acme/../shop' is not a GitLab project - group/project, its id or its address.", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "acme/../shop" }));
            Assert.Equal("Give the project - group/project, its id or its address.", await Refused(GitLabConnector.IssuesOperation, new()));
            Assert.StartsWith("'open' is not a state", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "acme/shop", ["state"] = "open" }));
            Assert.Equal("'grace hopper' is not a GitLab username.", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "acme/shop", ["assignee"] = "grace hopper" }));
            Assert.Equal("'last week' is not a time - 2026-10-01T00:00:00Z.", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "acme/shop", ["updated_since"] = "last week" }));
            Assert.Equal("'main..dev' is not a branch's name.", await Refused(GitLabConnector.MergeRequestsOperation, new() { ["project"] = "acme/shop", ["target_branch"] = "main..dev" }));
            Assert.Equal("'twelve' is not an issue's number (#12).", await Refused(GitLabConnector.GetIssueOperation, new() { ["project"] = "acme/shop", ["issue"] = "twelve" }));
            Assert.Equal("Give the issue's title.", await Refused(GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop" }));
            Assert.Equal("The title has a line break - one line only.", await Refused(GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop", ["title"] = "a\nb" }));
            Assert.Equal("'31/10' is not a due date - 2026-10-31.", await Refused(GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop", ["title"] = "x", ["due_date"] = "31/10" }));
            Assert.Equal("'maybe' is not true or false (confidential).", await Refused(GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop", ["title"] = "x", ["confidential"] = "maybe" }));
            Assert.StartsWith("'closed' is not a state change", await Refused(GitLabConnector.UpdateIssueOperation, new() { ["project"] = "acme/shop", ["issue"] = "12", ["state"] = "closed" }));
            Assert.Equal("Give something to change - a title, a description, labels, assignees, a due date, confidential or a state.", await Refused(GitLabConnector.UpdateIssueOperation, new() { ["project"] = "acme/shop", ["issue"] = "12" }));
            Assert.Equal("Give the comment.", await Refused(GitLabConnector.CommentOperation, new() { ["project"] = "acme/shop", ["issue"] = "12" }));
            Assert.Equal("'v1 2' is not a tag's name.", await Refused(GitLabConnector.ReleaseOperation, new() { ["project"] = "acme/shop", ["tag"] = "v1 2" }));
            Assert.Equal("'docs/../secrets.txt' is not a file's path - docs/README.md.", await Refused(GitLabConnector.FileOperation, new() { ["project"] = "acme/shop", ["path"] = "docs/../secrets.txt" }));
            Assert.Equal("The commit message has a line break - one line only.", await Refused(GitLabConnector.CommitFileOperation, new() { ["project"] = "acme/shop", ["path"] = "a.md", ["branch"] = "main", ["content"] = "x", ["message"] = "a\nb" }));
            Assert.Equal("The variables are a JSON object - {\"DEPLOY\": \"true\"}.", await Refused(GitLabConnector.PipelineOperation, new() { ["project"] = "acme/shop", ["ref"] = "main", ["variables"] = "[1]" }));
            Assert.Equal("'DEPLOY NOW' is not a variable's name (letters, digits and _).", await Refused(GitLabConnector.PipelineOperation, new() { ["project"] = "acme/shop", ["ref"] = "main", ["variables"] = "{\"DEPLOY NOW\": \"1\"}" }));
            Assert.StartsWith("GitLab has no operation 'lock_issue'", await Refused("lock_issue", new() { ["project"] = "acme/shop" }));
            Assert.Equal("'ftp://x.example' is not a GitLab instance's address - https://gitlab.com.", await Refused(GitLabConnector.IssuesOperation, new() { ["project"] = "acme/shop" }, "ftp://x.example"));
            Assert.Empty(gitlab.Requests);

            var nobody = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, "[]", null) };
            Assert.Equal("GitLab has no user ghost.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(nobody, GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop", ["title"] = "x", ["assignees"] = "ghost" })))).Message);
            Assert.DoesNotContain(nobody.Requests, request => request.Method == HttpMethod.Post); // nothing made when a user is not found

            var refusing = new GitLabApi { Answer = (_, _) => (HttpStatusCode.BadRequest, "{\"message\":{\"title\":[\"can't be blank\"],\"due_date\":[\"is invalid\"]}}", null) };
            Assert.Equal("HTTP 400: title can't be blank; due_date is invalid",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(refusing, GitLabConnector.CreateIssueOperation, new() { ["project"] = "acme/shop", ["title"] = "x" })))).Message);
            var missing = new GitLabApi { Answer = (_, _) => (HttpStatusCode.NotFound, "{\"message\":\"404 Project Not Found\"}", null) };
            Assert.Equal("HTTP 404: 404 Project Not Found", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, GitLabConnector.IssuesOperation, new() { ["project"] = "acme/gone" })))).Message);
            var binary = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"file_path\":\"logo.png\",\"content\":\"" + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0xFF, 0xFE }) + "\"}", null) };
            Assert.Equal("logo.png is not a UTF-8 text file.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(binary, GitLabConnector.FileOperation, new() { ["project"] = "acme/shop", ["path"] = "logo.png" })))).Message);
        }

        [Fact]
        public async Task A_test_names_the_user()
        {
            var gitlab = new GitLabApi { Answer = (_, _) => (HttpStatusCode.OK, "{\"id\":7,\"username\":\"ada\",\"name\":\"Ada Lovelace\"}", null) };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(gitlab), "https://gitlab.com/acme/shop", credentials: new Dictionary<string, string> { ["token"] = Token }));
            Assert.Equal((true, "Signed in as ada (Ada Lovelace)."), (tested.Success, tested.Message));
            Assert.Equal((Api + "/user", Token), (gitlab.Requests[0].Url, gitlab.Requests[0].Token));
            var refused = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(new GitLabApi { Answer = (_, _) => (HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\",\"error_description\":\"Token was revoked. You have to re-authorize from the user.\"}", null) }),
                "https://gitlab.com", credentials: new Dictionary<string, string> { ["token"] = Token }));
            Assert.Equal((false, "HTTP 401: Token was revoked. You have to re-authorize from the user."), (refused.Success, refused.Message));
            Assert.Equal((AuthenticationType.Bearer, "gitlab", ConnectorCategory.Development), (Connector().AuthType, Connector().ConnectorType, Assert.Single(Connector().Categories)));
            Assert.Equal(new[] { "issues", "get_issue", "create_issue", "update_issue", "comment", "merge_requests", "release", "file", "commit_file", "pipeline" }, Connector().Operations.Select(operation => operation.Name));
        }
    }
}
