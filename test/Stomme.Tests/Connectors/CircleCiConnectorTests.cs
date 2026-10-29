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
using Stomme.Modules.Connectors.CircleCi;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// CircleCI (n8n's CircleCI node) without a server: every project, number, branch, tag, parameter and id held to
    /// CircleCI's shape before anything is sent; a project as CircleCI's slug or as n8n's provider and org/repo, the step's or the
    /// connection's; the token in CircleCI's header and never in an address; a project's pipelines page by page by CircleCI's
    /// page token up to the most, on a branch; one by its number; one triggered on a branch or at a tag with its parameters; a
    /// pipeline's workflows, every page; each pipeline's branch, tag, revision and actor beside CircleCI's fields; CircleCI's
    /// message on a failure; a test naming who the token belongs to.
    /// </summary>
    public sealed class CircleCiConnectorTests
    {
        private const string Api = "https://circleci.com/api/v2";

        private const string PipelineId = "5034460f-c7c4-4c43-9457-de07e2029e7b";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "circle-kafe-token" };

        private sealed record Sent(string Method, string Url, string? Token, string? Body);

        private sealed class Circle : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Circle(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Circle-Token", out var token) ? token.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static string Pipeline(int number, string branch = "main") =>
            $"{{\"id\":\"{Guid.NewGuid():D}\",\"number\":{number},\"state\":\"created\",\"created_at\":\"2026-10-18T08:00:00Z\",\"project_slug\":\"gh/kafe/kassa\",\"trigger\":{{\"type\":\"webhook\",\"actor\":{{\"login\":\"ada\"}}}},\"vcs\":{{\"branch\":\"{branch}\",\"revision\":\"a1b2c3d\",\"origin_repository_url\":\"https://github.com/kafe/kassa\"}}}}";

        private static string Page(IEnumerable<string> items, string? next) => $"{{\"items\":[{string.Join(",", items)}],\"next_page_token\":{(next is null ? "null" : $"\"{next}\"")}}}";

        private static Task<List<ConnectorRecord>> Run(Circle circle, string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "") =>
            Contexts.DrainAsync(new CircleCiConnector(), Contexts.Extract(new StubEgress(circle), endpoint, operation, parameters, settings ?? new Dictionary<string, string>(), Credentials));

        [Fact]
        public void It_declares_itself_as_n8ns_circleci_node()
        {
            IConnector connector = new CircleCiConnector();
            Assert.Equal(("circleci", "CircleCI", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "pipelines", "pipeline", "pipeline_trigger", "pipeline_workflows" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_circlecis_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, Dictionary<string, string>? settings = null, string endpoint = "")
            {
                var circle = new Circle(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(circle, operation, parameters, settings, endpoint));
                Assert.Empty(circle.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.Equal("CircleCI has no operation 'jobs' - pipelines, pipeline, pipeline_trigger or pipeline_workflows.", await Refused("jobs", new()));
            Assert.Equal("Give the project - gh/kafe/kassa (project) - or set the connection's.", await Refused("pipelines", new()));
            Assert.Equal("'kassa' is not a project - gh/kafe/kassa, or kafe/kassa with its provider (project).", await Refused("pipelines", new() { ["project"] = "kassa" }));
            Assert.Equal("'gl/kafe/kassa' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "gl/kafe/kassa" }));
            Assert.Equal("'gh/kafe/kas sa' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "gh/kafe/kas sa" }));
            Assert.Equal("'gh/kafe/..%2F' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "gh/kafe/..%2F" }));
            Assert.Equal("'gh/kafe/..' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "gh/kafe/.." }));
            Assert.Equal("'./kassa' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "./kassa" }));
            Assert.Equal("'circleci/kafe/kassa' is not a project - gh/kafe/kassa, bb/kafe/kassa or circleci/<organization id>/<project id> (project).", await Refused("pipelines", new() { ["project"] = "circleci/kafe/kassa" }));
            Assert.Equal("'gitlab' is not github, bitbucket or circleci (vcs).", await Refused("pipelines", new() { ["project"] = "kafe/kassa", ["vcs"] = "gitlab" }));
            Assert.Equal("'gitlab' is not github, bitbucket or circleci (vcs).", await Refused("pipelines", new() { ["project"] = "kafe/kassa" }, new() { ["vcs"] = "gitlab" }));
            Assert.Equal("'main branch' is not a branch's name (branch).", await Refused("pipelines", new() { ["project"] = "gh/kafe/kassa", ["branch"] = "main branch" }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused("pipelines", new() { ["project"] = "gh/kafe/kassa", ["max"] = "0" }));
            Assert.Equal("Give the pipeline's number - 142 (number).", await Refused("pipeline", new() { ["project"] = "gh/kafe/kassa" }));
            Assert.Equal("'#142' is not a pipeline's number - 142 (number).", await Refused("pipeline", new() { ["project"] = "gh/kafe/kassa", ["number"] = "#142" }));
            Assert.Equal("'0' is not a pipeline's number - 142 (number).", await Refused("pipeline", new() { ["project"] = "gh/kafe/kassa", ["number"] = "0" }));
            Assert.Equal("Give a branch or a tag, not both - CircleCI builds one (branch, tag).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["branch"] = "main", ["tag"] = "v1.4.0" }));
            Assert.Equal("'feature..x' is not a branch's name (branch).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["branch"] = "feature..x" }));
            Assert.Equal("'v1.4.0^' is not a tag's name (tag).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["tag"] = "v1.4.0^" }));
            Assert.Equal("'release/' is not a branch's name (branch).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["branch"] = "release/" }));
            Assert.Equal("The pipeline parameters are a JSON object - {\"deploy\": true} (parameters).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["parameters"] = "deploy=true" }));
            Assert.Equal("The pipeline parameters are a JSON object - {\"deploy\": true} (parameters).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["query"] = "[true]" }));
            Assert.Equal("The pipeline parameter 'target' is not a string, a number or true or false (parameters).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["parameters"] = "{\"target\":{\"env\":\"uat\"}}" }));
            Assert.Equal("The pipeline parameter 'none' is not a string, a number or true or false (parameters).", await Refused("pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["parameters"] = "{\"none\":null}" }));
            Assert.Equal("Give the pipeline's id - the id a trigger or a list answered (pipeline).", await Refused("pipeline_workflows", new()));
            Assert.Equal("'142' is not a pipeline's id - 5034460f-c7c4-4c43-9457-de07e2029e7b (pipeline).", await Refused("pipeline_workflows", new() { ["pipeline"] = "142" }));
            Assert.Equal("'https://circleci.com/api/v1.1' is not CircleCI's address - its own, https://circleci.com, or a CircleCI server's.", await Refused("pipelines", new() { ["project"] = "gh/kafe/kassa" }, endpoint: "https://circleci.com/api/v1.1"));
        }

        [Fact]
        public async Task A_projects_pipelines_are_read_page_by_page_up_to_the_most()
        {
            var circle = new Circle(sent => (HttpStatusCode.OK, sent.Url.Contains("page-token=p2", StringComparison.Ordinal) ? Page(Enumerable.Range(21, 20).Select(n => Pipeline(200 - n)), "p3")
                : sent.Url.Contains("page-token=p3", StringComparison.Ordinal) ? Page(Enumerable.Range(41, 20).Select(n => Pipeline(200 - n)), null)
                : Page(Enumerable.Range(1, 20).Select(n => Pipeline(200 - n)), "p2")));
            var pipelines = await Run(circle, "pipelines", new() { ["project"] = "kafe/kassa", ["branch"] = "release/2026.10", ["max"] = "25" });
            Assert.Equal(25, pipelines.Count);
            Assert.Equal(new[] { Api + "/project/gh/kafe/kassa/pipeline?branch=release%2F2026.10", Api + "/project/gh/kafe/kassa/pipeline?branch=release%2F2026.10&page-token=p2" }, circle.Requests.Select(sent => sent.Url));
            Assert.All(circle.Requests, sent => Assert.Equal(("GET", "circle-kafe-token", (string?)null), (sent.Method, sent.Token, sent.Body)));
            var first = pipelines[0].Fields;
            Assert.Equal((199L, "created", "main", "a1b2c3d", "ada", (string?)null), ((long?)first["number"], (string?)first["state"], (string?)first["branch"], (string?)first["revision"], (string?)first["actor"], (string?)first["tag"]));
            Assert.Contains("\"origin_repository_url\":\"https://github.com/kafe/kassa\"", (string?)first["vcs"], StringComparison.Ordinal); // CircleCI's own, as it is

            // The project as CircleCI's slug, or as n8n's provider and org/repo - the step's, the connection's.
            var one = new Circle(_ => (HttpStatusCode.OK, Page(new[] { Pipeline(1) }, null)));
            await Run(one, "pipelines", new() { ["project"] = "bb/kafe/kassa" });
            await Run(one, "pipelines", new() { ["project"] = "kafe/kassa", ["vcs"] = "bitbucket" });
            await Run(one, "pipelines", new(), new() { ["project"] = "kafe/kassa", ["vcs"] = "bitbucket" });
            await Run(one, "pipelines", new() { ["project"] = "kafe/kassa-app" }, new() { ["project"] = "kafe/kassa", ["vcs"] = "Bitbucket" });
            await Run(one, "pipelines", new() { ["project"] = "kafe/kassa", ["vcs"] = "github" }, new() { ["vcs"] = "bitbucket" }); // the step's provider first
            await Run(one, "pipelines", new() { ["project"] = "/github/kafe/kassa/" });
            await Run(one, "pipelines", new() { ["project"] = "circleci/9c2f1e8a-3a4b-4c5d-8e9f-0a1b2c3d4e5f/0d1e2f3a-4b5c-4d6e-8f70-81a2b3c4d5e6" });
            Assert.Equal(new[]
            {
                "/project/bb/kafe/kassa/pipeline", "/project/bb/kafe/kassa/pipeline", "/project/bb/kafe/kassa/pipeline", "/project/bb/kafe/kassa-app/pipeline", "/project/gh/kafe/kassa/pipeline", "/project/gh/kafe/kassa/pipeline",
                "/project/circleci/9c2f1e8a-3a4b-4c5d-8e9f-0a1b2c3d4e5f/0d1e2f3a-4b5c-4d6e-8f70-81a2b3c4d5e6/pipeline",
            }, one.Requests.Select(sent => sent.Url[Api.Length..]));
        }

        [Fact]
        public async Task A_pipeline_is_read_by_its_number_triggered_and_its_workflows_listed()
        {
            var circle = new Circle(sent => (HttpStatusCode.OK, sent switch
            {
                { Method: "POST" } => $"{{\"id\":\"{PipelineId}\",\"state\":\"pending\",\"number\":143,\"created_at\":\"2026-10-18T09:00:00Z\"}}",
                _ when sent.Url.EndsWith("/workflow", StringComparison.Ordinal) => Page(new[] { "{\"id\":\"w1\",\"name\":\"build\",\"status\":\"running\",\"pipeline_number\":143}" }, "w2"),
                _ when sent.Url.Contains("page-token=w2", StringComparison.Ordinal) => Page(new[] { "{\"id\":\"w2\",\"name\":\"deploy\",\"status\":\"on_hold\",\"pipeline_number\":143}" }, null),
                _ => Pipeline(142),
            }));

            var read = Assert.Single(await Run(circle, "pipeline", new() { ["project"] = "gh/kafe/kassa", ["number"] = "142" })).Fields;
            Assert.Equal((142L, "main"), ((long?)read["number"], (string?)read["branch"]));
            var started = Assert.Single(await Run(circle, "pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["branch"] = "release/2026.10", ["parameters"] = "{\"deploy\": true, \"target\": \"uat\", \"replicas\": 2}" })).Fields;
            Assert.Equal((PipelineId, "pending", 143L, (string?)null), ((string?)started["id"], (string?)started["state"], (long?)started["number"], (string?)started["branch"]));
            await Run(circle, "pipeline_trigger", new() { ["project"] = "gh/kafe/kassa", ["tag"] = "v1.4.0", ["query"] = "{\n  \"deploy\": false\n}" });
            await Run(circle, "pipeline_trigger", new() { ["project"] = "gh/kafe/kassa" });
            var workflows = await Run(circle, "pipeline_workflows", new() { ["pipeline"] = PipelineId.ToUpperInvariant() });
            Assert.Equal(new[] { ("build", "running"), ("deploy", "on_hold") }, workflows.Select(workflow => ((string?)workflow.Fields["name"], (string?)workflow.Fields["status"])));

            Assert.Equal(new[]
            {
                ("GET", Api + "/project/gh/kafe/kassa/pipeline/142", (string?)null),
                ("POST", Api + "/project/gh/kafe/kassa/pipeline", "{\"branch\":\"release/2026.10\",\"parameters\":{\"deploy\":true,\"target\":\"uat\",\"replicas\":2}}"),
                ("POST", Api + "/project/gh/kafe/kassa/pipeline", "{\"tag\":\"v1.4.0\",\"parameters\":{\"deploy\":false}}"),
                ("POST", Api + "/project/gh/kafe/kassa/pipeline", "{}"), // the project's default branch
                ("GET", Api + $"/pipeline/{PipelineId}/workflow", null),
                ("GET", Api + $"/pipeline/{PipelineId}/workflow?page-token=w2", null),
            }, circle.Requests.Select(sent => (sent.Method, sent.Url, sent.Body)));
            Assert.DoesNotContain(circle.Requests, sent => sent.Url.Contains("circle-kafe-token", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_failure_is_circlecis_message_and_what_is_not_its_answer_is_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "pipelines") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Circle(_ => (status, body)), operation, new() { ["project"] = "gh/kafe/kassa", ["number"] = "1" }))).Message;

            Assert.Equal("CircleCI answered HTTP 401: You must log in first. - check the API token.", await Failed(HttpStatusCode.Unauthorized, "{\"message\":\"You must log in first.\"}"));
            Assert.Equal("CircleCI answered HTTP 404: Project not found - check the project and that the token's user can see it.", await Failed(HttpStatusCode.NotFound, "{\"message\":\"Project not found\"}"));
            Assert.Equal("CircleCI answered HTTP 400: Pipeline parameter 'replicas' is not declared.", await Failed(HttpStatusCode.BadRequest, "{\"message\":\"Pipeline parameter 'replicas' is not declared.\"}", "pipeline_trigger"));
            Assert.Equal("CircleCI answered HTTP 500.", await Failed(HttpStatusCode.InternalServerError, "<html>fel</html>"));
            Assert.Equal("CircleCI answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));
            Assert.Equal("CircleCI answered with what is not its answer - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "[]", "pipeline"));
            Assert.Equal("CircleCI answered without its items - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{}"));
        }

        [Fact]
        public async Task A_test_names_who_the_token_belongs_to()
        {
            var circle = new Circle(_ => (HttpStatusCode.OK, "{\"name\":\"Ada Lovelace\",\"login\":\"ada\",\"id\":\"8a7e3c7d-0000-4000-8000-000000000001\"}"));
            var tested = await new CircleCiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(circle), "https://circleci.com/api/v2/", new Dictionary<string, string>(), Credentials));
            Assert.Equal((true, "Signed in to CircleCI as ada."), (tested.Success, tested.Message));
            Assert.Equal(("GET", Api + "/me", "circle-kafe-token"), (circle.Requests.Single().Method, circle.Requests.Single().Url, circle.Requests.Single().Token));

            async Task<ConnectionTestResult> Test(HttpStatusCode status, string body) =>
                await new CircleCiConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new Circle(_ => (status, body))), "https://circleci.example", new Dictionary<string, string>(), Credentials));

            var refused = await Test(HttpStatusCode.Unauthorized, "{\"message\":\"You must log in first.\"}");
            Assert.Equal((false, "CircleCI answered HTTP 401: You must log in first. - check the API token."), (refused.Success, refused.Message));
            var nobody = await Test(HttpStatusCode.OK, "{}");
            Assert.Equal((false, "CircleCI answered without who the token belongs to - check the address: a sign-in page or a proxy answers so."), (nobody.Success, nobody.Message));
            var signIn = await Test(HttpStatusCode.OK, "<html><body>Sign in</body></html>");
            Assert.False(signIn.Success);
        }
    }
}
