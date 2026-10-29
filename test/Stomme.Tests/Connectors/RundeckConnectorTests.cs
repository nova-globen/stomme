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
using Stomme.Modules.Connectors.Rundeck;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Rundeck (n8n's Rundeck node) without a server: every id, name, number and status held to Rundeck's shape before
    /// anything is sent; the token in its header and in no address, API 44 whatever address is given; a job run with its options
    /// as Rundeck's options object - never n8n's argString - each checked against the job's own definition first, and waited for
    /// with its output's last lines; a job's metadata, a project's jobs by group, a job's executions by status, an execution and
    /// its output; an abort Rundeck refused said; Rundeck's message, an API version it does not speak named; a test counting projects.
    /// </summary>
    public sealed class RundeckConnectorTests
    {
        private const string Address = "https://rundeck.kafe.example";

        private const string Job = "ff50cae3-0ccc-4113-8b9e-f51f0642b3c4";

        private static readonly Dictionary<string, string> Credentials = new() { ["api-key"] = "ortPFuT3Fgh4YcmDkUE88RHWRAGzhNEv" };

        private const string Definition = "[ { \"id\" : \"" + Job + "\", \"name\" : \"Dagens meny\", \"group\" : \"kassa\", \"options\" : [ { \"name\" : \"day\", \"required\" : true, \"value\" : \"måndag\" }, { \"name\" : \"dish\" } ] } ]";

        private const string Running = "{\"id\":4,\"href\":\"https://rundeck.kafe.example/api/59/execution/4\",\"permalink\":\"https://rundeck.kafe.example/project/kafe/execution/show/4\",\"status\":\"running\",\"project\":\"kafe\",\"executionType\":\"user\",\"user\":\"kafe\",\"date-started\":{\"unixtime\":1792636141482,\"date\":\"2026-10-22T02:29:01Z\"},\"job\":{\"id\":\"" + Job + "\",\"name\":\"Dagens meny\",\"group\":\"kassa\",\"project\":\"kafe\",\"options\":{\"dish\":\"fisk -day söndag\",\"day\":\"tisdag\"}},\"argstring\":\"-day tisdag -dish \\\"fisk -day söndag\\\"\"}";

        private sealed record Sent(string Method, string Url, string? Token, string? Body);

        private sealed class Rundeck : HttpMessageHandler
        {
            private readonly Func<Sent, (HttpStatusCode Status, string Body)> answer;

            public Rundeck(Func<Sent, (HttpStatusCode Status, string Body)> answer) => this.answer = answer;

            public List<Sent> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var sent = new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("X-Rundeck-Auth-Token", out var token) ? token.Single() : null, body);
                Requests.Add(sent);
                var (status, text) = answer(sent);
                return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "text/html" : "application/json") };
            }
        }

        private static Task<List<ConnectorRecord>> Run(Rundeck rundeck, string operation, Dictionary<string, string> parameters, string endpoint = Address) =>
            Contexts.DrainAsync(new RundeckConnector(), Contexts.Extract(new StubEgress(rundeck), endpoint, operation, parameters, new Dictionary<string, string>(), Credentials));

        private static string Api(string route) => Address + "/api/44" + route;

        [Fact]
        public void It_declares_itself_as_n8ns_rundeck_node()
        {
            IConnector connector = new RundeckConnector();
            Assert.Equal(("rundeck", "Rundeck", AuthenticationType.ApiKey), (connector.ConnectorType, connector.DisplayName, connector.AuthType));
            Assert.Equal(new[] { "run", "job", "jobs", "executions", "execution", "execution_output", "abort" }, ((IConnectorExtractor)connector).Operations.Select(operation => operation.Name));
        }

        [Fact]
        public async Task Every_parameter_is_held_to_rundecks_shape_before_anything_is_sent()
        {
            async Task<string> Refused(string operation, Dictionary<string, string> parameters, string endpoint = Address)
            {
                var rundeck = new Rundeck(_ => (HttpStatusCode.OK, "{}"));
                var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(rundeck, operation, parameters, endpoint));
                Assert.Empty(rundeck.Requests); // refused before anything was sent
                return refusal.Message;
            }

            Assert.Equal("Rundeck has no operation 'delete' - run, job, jobs, executions, execution, execution_output or abort.", await Refused("delete", new()));
            Assert.Equal("Give the job - its id, ff50cae3-0ccc-4113-8b9e-f51f0642b3c4 (job).", await Refused("run", new()));
            Assert.Equal("'ff50cae3/../../system' is not a job's id - ff50cae3-0ccc-4113-8b9e-f51f0642b3c4 (job).", await Refused("job", new() { ["job"] = "ff50cae3/../../system" }));
            Assert.Equal("'dag ens' is not an option's name - letters, digits, _ . and - (option.dag ens).", await Refused("run", new() { ["job"] = Job, ["option.dag ens"] = "x" }));
            Assert.Equal("A node filter is one line (filter).", await Refused("run", new() { ["job"] = Job, ["filter"] = "name: kassa1\ntags: x" }));
            Assert.Equal("'3601' is not a number from 0 to 3600 (wait).", await Refused("run", new() { ["job"] = Job, ["wait"] = "3601" }));
            Assert.Equal("Give the project - its name, kafe (project).", await Refused("jobs", new()));
            Assert.Equal("'kafe/../x' is not a project's name - letters, digits, _ - . and + (project).", await Refused("jobs", new() { ["project"] = "kafe/../x" }));
            Assert.Equal("'0' is not a number from 1 to 2147483647 (max).", await Refused("jobs", new() { ["project"] = "kafe", ["max"] = "0" }));
            Assert.Equal("'done' is not a status - running, succeeded, failed or aborted (status).", await Refused("executions", new() { ["job"] = Job, ["status"] = "done" }));
            Assert.Equal("Give the execution - its number, 42 (execution).", await Refused("execution", new()));
            Assert.Equal("'-4' is not an execution's number - 42 (execution).", await Refused("abort", new() { ["execution"] = "-4" }));
            Assert.Equal("'0' is not a number from 1 to 100000 (lines).", await Refused("execution_output", new() { ["execution"] = "4", ["lines"] = "0" }));
            Assert.Equal("Give Rundeck's address - https://rundeck.kafe.example.", await Refused("execution", new() { ["execution"] = "4" }, ""));
            Assert.Equal("'https://rundeck.kafe.example/?x=1' is not Rundeck's address - https://rundeck.kafe.example, with no query.", await Refused("execution", new() { ["execution"] = "4" }, "https://rundeck.kafe.example/?x=1"));
            Assert.Equal("The Rundeck connection has no API token.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new RundeckConnector(), Contexts.Extract(new StubEgress(new Rundeck(_ => (HttpStatusCode.OK, "{}"))), Address, "execution", new Dictionary<string, string> { ["execution"] = "4" }, new Dictionary<string, string>(), new Dictionary<string, string>())))).Message);
        }

        [Fact]
        public async Task A_job_runs_with_its_options_as_rundecks_options_checked_against_its_definition()
        {
            var rundeck = new Rundeck(sent => (HttpStatusCode.OK, sent.Method == "GET" ? Definition : Running));
            var run = Assert.Single(await Run(rundeck, "run", new() { ["job"] = Job, ["option.day"] = "tisdag", ["option.dish"] = "fisk -day söndag", ["filter"] = "name: kassa1" }, Address + "/api/18")).Fields;
            Assert.Equal(new[] { ("GET", Api($"/job/{Job}?format=json")), ("POST", Api($"/job/{Job}/run")) }, rundeck.Requests.Select(sent => (sent.Method, sent.Url))); // API 44 whatever the address said
            Assert.All(rundeck.Requests, sent => Assert.Equal("ortPFuT3Fgh4YcmDkUE88RHWRAGzhNEv", sent.Token));
            Assert.DoesNotContain(rundeck.Requests, sent => sent.Url.Contains("ortPFuT3", StringComparison.Ordinal));
            Assert.Equal(JsonNode.Parse("{\"options\":{\"day\":\"tisdag\",\"dish\":\"fisk -day söndag\"},\"filter\":\"name: kassa1\"}")!.ToJsonString(), JsonNode.Parse(rundeck.Requests[1].Body!)!.ToJsonString()); // one value each, never an argString Rundeck splits on its dashes
            Assert.Equal((4L, "running", "kafe", Job, "Dagens meny", "kassa", "2026-10-22T02:29:01Z"), ((long?)run["id"], (string?)run["status"], (string?)run["project"], (string?)run["job"], (string?)run["job_name"], (string?)run["group"], (string?)run["started"]));
            Assert.Equal("{\"dish\":\"fisk -day söndag\",\"day\":\"tisdag\"}", (string?)run["options"]);

            var unknown = new Rundeck(sent => (HttpStatusCode.OK, sent.Method == "GET" ? Definition : Running));
            Assert.Equal("The job has no option 'dagen' - it takes day, dish (option.dagen).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(unknown, "run", new() { ["job"] = Job, ["option.dagen"] = "tisdag" }))).Message);
            Assert.DoesNotContain(unknown.Requests, sent => sent.Method == "POST"); // Rundeck would have run it with the option it does not have
            var none = new Rundeck(_ => (HttpStatusCode.OK, "[ { \"id\" : \"x\" } ]"));
            Assert.Equal("The job has no option 'day' - it takes none (option.day).", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(none, "run", new() { ["job"] = Job, ["option.day"] = "x" }))).Message);

            var bare = new Rundeck(sent => (HttpStatusCode.OK, sent.Method == "GET" ? Definition : Running));
            await Run(bare, "run", new() { ["job"] = Job });
            Assert.Equal("{\"options\":{}}", bare.Requests[1].Body); // no filter: the job's own nodes
        }

        [Fact]
        public async Task A_waited_run_comes_back_finished_with_its_outputs_last_lines()
        {
            var polled = 0;
            var rundeck = new Rundeck(sent => (HttpStatusCode.OK, (sent.Method, sent.Url) switch
            {
                ("GET", var url) when url.EndsWith("?format=json", StringComparison.Ordinal) => Definition,
                ("POST", _) => Running,
                ("GET", var url) when url.Contains("/output", StringComparison.Ordinal) => "{\"id\":\"4\",\"execState\":\"failed\",\"entries\":[{\"absolute_time\":\"2026-10-22T02:29:01Z\",\"log\":\"trasig\",\"level\":\"NORMAL\",\"node\":\"kassa1\",\"stepctx\":\"1\"},{\"absolute_time\":\"2026-10-22T02:29:01Z\",\"log\":\"Result: 3\",\"level\":\"ERROR\",\"node\":\"kassa1\",\"stepctx\":\"1\"}]}",
                _ => ++polled < 2 ? Running : Running.Replace("\"status\":\"running\"", "\"status\":\"failed\",\"date-ended\":{\"date\":\"2026-10-22T02:29:02Z\"}", StringComparison.Ordinal),
            }));
            var run = Assert.Single(await Run(rundeck, "run", new() { ["job"] = Job, ["wait"] = "30" })).Fields;
            Assert.Equal(("failed", true, "trasig\nResult: 3", "2026-10-22T02:29:02Z"), ((string?)run["status"], (bool?)run["finished"], (string?)run["output"], (string?)run["ended"]));
            Assert.Equal(Api("/execution/4/output?lastlines=20"), rundeck.Requests[^1].Url);
            Assert.Equal(2, rundeck.Requests.Count(sent => sent.Url == Api("/execution/4")));

            // A wait that runs out comes back with the execution as it stands: still running, not finished.
            var slow = new Rundeck(sent => (HttpStatusCode.OK, sent.Url.EndsWith("?format=json", StringComparison.Ordinal) ? Definition : sent.Url.Contains("/output", StringComparison.Ordinal) ? "{\"id\":\"4\",\"execState\":\"running\",\"entries\":[]}" : Running));
            var unfinished = Assert.Single(await Run(slow, "run", new() { ["job"] = Job, ["wait"] = "1" })).Fields;
            Assert.Equal(("running", false, ""), ((string?)unfinished["status"], (bool?)unfinished["finished"], (string?)unfinished["output"]));
        }

        [Fact]
        public async Task Jobs_executions_an_execution_and_its_output_are_read()
        {
            var rundeck = new Rundeck(sent => (HttpStatusCode.OK, sent.Url switch
            {
                var url when url.Contains("/project/", StringComparison.Ordinal) => $"[{{\"id\":\"{Job}\",\"name\":\"Dagens meny\",\"group\":\"kassa\",\"project\":\"kafe\",\"scheduled\":false,\"enabled\":true}},{{\"id\":\"e7cf1904\",\"name\":\"Långsam\",\"group\":\"kassa\"}}]",
                var url when url.Contains("/executions", StringComparison.Ordinal) => "{\"paging\":{\"count\":1,\"total\":4,\"offset\":0,\"max\":1},\"executions\":[" + Running.Replace("\"status\":\"running\"", "\"status\":\"succeeded\"", StringComparison.Ordinal) + "]}",
                var url when url.Contains("/output", StringComparison.Ordinal) => "{\"id\":\"4\",\"execState\":\"succeeded\",\"entries\":[{\"absolute_time\":\"2026-10-22T02:28:08Z\",\"log\":\"meny tisdag fisk\",\"level\":\"NORMAL\",\"node\":\"kassa1\",\"stepctx\":\"1\"}]}",
                var url when url.EndsWith("/info", StringComparison.Ordinal) => $"{{\"id\":\"{Job}\",\"name\":\"Dagens meny\",\"group\":\"kassa\",\"enabled\":true,\"averageDuration\":758}}",
                _ => Running,
            }));
            var jobs = await Run(rundeck, "jobs", new() { ["project"] = "kafe", ["group"] = "kassa/dagligt", ["name"] = "Dagens meny", ["max"] = "1" });
            Assert.Equal((Job, true), ((string?)Assert.Single(jobs).Fields["id"], (bool?)jobs[0].Fields["enabled"]));
            Assert.Equal(Api("/project/kafe/jobs?groupPath=kassa%2Fdagligt&jobExactFilter=Dagens%20meny"), rundeck.Requests[^1].Url);
            await Run(rundeck, "jobs", new() { ["project"] = "kafe" });
            Assert.Equal(Api("/project/kafe/jobs"), rundeck.Requests[^1].Url);
            var executions = await Run(rundeck, "executions", new() { ["job"] = Job, ["status"] = "Succeeded", ["max"] = "1" });
            Assert.Equal("succeeded", (string?)Assert.Single(executions).Fields["status"]);
            Assert.Equal(Api($"/job/{Job}/executions?max=1&status=succeeded"), rundeck.Requests[^1].Url); // Rundeck's own spelling: it answers one it does not know with nothing
            Assert.Equal(758L, (long?)Assert.Single(await Run(rundeck, "job", new() { ["job"] = Job })).Fields["averageDuration"]);
            Assert.Equal("running", (string?)Assert.Single(await Run(rundeck, "execution", new() { ["execution"] = "4" })).Fields["status"]);
            var line = Assert.Single(await Run(rundeck, "execution_output", new() { ["execution"] = "4", ["lines"] = "5" })).Fields;
            Assert.Equal((4L, "meny tisdag fisk", "kassa1", "NORMAL", "succeeded"), ((long?)line["execution"], (string?)line["log"], (string?)line["node"], (string?)line["level"], (string?)line["state"]));
            Assert.Equal(Api("/execution/4/output?lastlines=5"), rundeck.Requests[^1].Url);
        }

        [Fact]
        public async Task An_abort_rundeck_refused_is_said_in_its_record()
        {
            var rundeck = new Rundeck(_ => (HttpStatusCode.OK, "{\"abort\":{\"status\":\"failed\",\"reason\":\"Job is not running\"},\"execution\":{\"id\":\"5\",\"status\":\"failed\"}}"));
            var abort = Assert.Single(await Run(rundeck, "abort", new() { ["execution"] = "5" })).Fields;
            Assert.Equal((5L, "failed", "Job is not running", "failed"), ((long?)abort["execution"], (string?)abort["abort"], (string?)abort["reason"], (string?)abort["status"]));
            Assert.Equal(("POST", Api("/execution/5/abort")), (rundeck.Requests[0].Method, rundeck.Requests[0].Url));
        }

        [Fact]
        public async Task Rundecks_message_is_said_and_an_answer_that_is_not_rundecks_refused()
        {
            async Task<string> Failed(HttpStatusCode status, string body, string operation = "execution") =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Rundeck(_ => (status, body)), operation, new() { ["execution"] = "4", ["job"] = Job, ["project"] = "kafe" }))).Message;

            Assert.Equal("Rundeck answered HTTP 404: Execution does not exist: 4.", await Failed(HttpStatusCode.NotFound, "{\"errorCode\":\"api.error.item.doesnotexist\",\"apiversion\":59,\"error\":true,\"message\":\"Execution does not exist: 4\"}"));
            Assert.Equal("Rundeck answered HTTP 409: Execution had a conflict: Job \"Dagens meny\": Limit of running executions has been reached.", await Failed(HttpStatusCode.Conflict, "{\"error\":true,\"apiversion\":59,\"errorCode\":\"api.error.execution.conflict\",\"message\":\"Execution had a conflict: Job \\\"Dagens meny\\\": Limit of running executions has been reached.\"}", "job"));
            Assert.Equal("Rundeck answered HTTP 403: (unauthenticated) is not authorized for: /api/44/execution/4. - check the API token and what its user may do.", await Failed(HttpStatusCode.Forbidden, "{\"error\":true,\"apiversion\":59,\"errorCode\":\"unauthorized\",\"message\":\"(unauthenticated) is not authorized for: /api/44/execution/4\"}"));
            Assert.Equal("Rundeck answered HTTP 403: (the token) is not authorized for: /api/44/projects. - check the API token and what its user may do.", await Failed(HttpStatusCode.Forbidden, "{\"error\":true,\"apiversion\":59,\"errorCode\":\"unauthorized\",\"message\":\"(Token:ortPF****) is not authorized for: /api/44/projects\"}")); // the token's first characters, as Rundeck says them, never repeated
            Assert.Equal("Rundeck answered HTTP 400: Unsupported API Version \"44\". - this connector speaks Rundeck's API 44 (Rundeck 4.12 and later); this Rundeck answers up to 41.", await Failed(HttpStatusCode.BadRequest, "{\"error\":true,\"apiversion\":41,\"errorCode\":\"api.error.api-version.unsupported\",\"message\":\"Unsupported API Version \\\"44\\\"\"}"));
            Assert.Equal("Rundeck answered with what is not JSON - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "<html>Rundeck - Login</html>"));
            Assert.Equal("Rundeck answered with what is not a list - check the address: a sign-in page or a proxy answers so.", await Failed(HttpStatusCode.OK, "{\"id\":\"x\"}", "jobs"));
        }

        [Fact]
        public async Task A_test_counts_the_projects_the_token_may_see()
        {
            var connector = new RundeckConnector();
            var ok = await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Rundeck(_ => (HttpStatusCode.OK, "[{\"name\":\"kafe\"}]"))), Address, null, Credentials));
            Assert.Equal((true, "Rundeck answers: 1 project(s) the token may see."), (ok.Success, ok.Message));
            Assert.False((await connector.TestConnectionAsync(Contexts.Test(new StubEgress(new Rundeck(_ => (HttpStatusCode.OK, "<html>Rundeck - Login</html>"))), Address, null, Credentials))).Success);
        }
    }

    /// <summary>
    /// Rundeck LIVE, against this machine's throwaway Rundeck 6.2 (scripts/rundeck-test-server.ps1) - fail-not-skip without
    /// it: a test counting the projects and a wrong token refused in Rundeck's words; a job found by name, run with a value
    /// holding Rundeck's own dashes - which stays one value, where n8n's argString overrode another option - waited for and its
    /// output read; an option the job does not have refused before Rundeck runs it; a required option missing and a value outside
    /// an enforced list refused in Rundeck's words; a failing job's status and output; a long job aborted, and an abort of a
    /// finished one said.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RundeckConnectorLiveTests(ITestOutputHelper output)
    {
        private static Dictionary<string, string> Token => new() { ["api-key"] = RundeckTestEndpoint.Token };

        private static HostConnectorEgress Egress => new(new[] { new Uri(RundeckTestEndpoint.Endpoint).Host });

        private static Task<List<ConnectorRecord>> All(string operation, Dictionary<string, string> parameters) =>
            Contexts.DrainAsync(new RundeckConnector(), Contexts.Extract(Egress, RundeckTestEndpoint.Endpoint, operation, parameters, new Dictionary<string, string>(), Token, timeoutSeconds: 60));

        private static async Task<IReadOnlyDictionary<string, object?>> One(string operation, Dictionary<string, string> parameters) => Assert.Single(await All(operation, parameters)).Fields;

        private static async Task<string> JobId(string name) =>
            (string)Assert.Single(await All("jobs", new() { ["project"] = "kafe", ["name"] = name })).Fields["id"]!;

        [Fact]
        public async Task A_test_counts_the_projects_and_a_wrong_token_is_refused()
        {
            var tested = await new RundeckConnector().TestConnectionAsync(Contexts.Test(Egress, RundeckTestEndpoint.Endpoint, credentials: Token, timeoutSeconds: 60));
            Assert.Equal((true, "Rundeck answers: 1 project(s) the token may see."), (tested.Success, tested.Message));
            var wrong = await new RundeckConnector().TestConnectionAsync(Contexts.Test(Egress, RundeckTestEndpoint.Endpoint, credentials: new Dictionary<string, string> { ["api-key"] = new string('a', 32) }, timeoutSeconds: 60));
            Assert.Equal((false, "Rundeck answered HTTP 403: (the token) is not authorized for: /api/44/projects. - check the API token and what its user may do."), (wrong.Success, wrong.Message));
        }

        [Fact]
        public async Task A_job_runs_with_its_options_waited_for_and_read()
        {
            var menu = await JobId("Dagens meny");
            var run = await One("run", new() { ["job"] = menu, ["option.day"] = "tisdag", ["option.dish"] = "fisk -day söndag", ["wait"] = "60" });
            output.WriteLine($"execution {run["id"]}");
            Assert.Equal(("succeeded", true, "meny tisdag fisk -day söndag"), ((string?)run["status"], (bool?)run["finished"], (string?)run["output"])); // one value, as given
            Assert.Equal("{\"dish\":\"fisk -day söndag\",\"day\":\"tisdag\"}", (string?)run["options"]);
            var id = run["id"]!.ToString()!;
            Assert.Equal("succeeded", (string?)(await One("execution", new() { ["execution"] = id }))["status"]);
            Assert.Contains(await All("execution_output", new() { ["execution"] = id }), line => (string?)line.Fields["log"] == "meny tisdag fisk -day söndag");
            Assert.Contains(await All("executions", new() { ["job"] = menu, ["status"] = "succeeded", ["max"] = "50" }), execution => execution.Fields["id"]!.ToString() == id);
            Assert.Equal("Dagens meny", (string?)(await One("job", new() { ["job"] = menu }))["name"]);

            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => All("run", new() { ["job"] = menu, ["option.dagen"] = "tisdag" }));
            Assert.Equal("The job has no option 'dagen' - it takes day, dish (option.dagen).", unknown.Message);
            var demands = await JobId("Krav");
            Assert.Equal("Rundeck answered HTTP 400: Job options were not valid: Option 'day' is required.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("run", new() { ["job"] = demands }))).Message);
            Assert.Equal("Rundeck answered HTTP 400: Job options were not valid: Option size value: jättestor was not in the allowed values: [liten, stor].", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("run", new() { ["job"] = demands, ["option.day"] = "x", ["option.size"] = "jättestor" }))).Message);
        }

        [Fact]
        public async Task A_failing_job_is_said_and_a_long_one_aborted()
        {
            var broken = await One("run", new() { ["job"] = await JobId("Trasig"), ["wait"] = "60" });
            Assert.Equal(("failed", true), ((string?)broken["status"], (bool?)broken["finished"]));
            Assert.Contains("trasig", (string?)broken["output"], StringComparison.Ordinal);

            var slow = await One("run", new() { ["job"] = await JobId("Långsam"), ["option.seconds"] = "60" });
            Assert.Equal("running", (string?)slow["status"]);
            var id = slow["id"]!.ToString()!;
            var abort = await One("abort", new() { ["execution"] = id });
            Assert.Contains((string?)abort["abort"], new[] { "pending", "aborted" });
            var again = await One("abort", new() { ["execution"] = broken["id"]!.ToString()! });
            Assert.Equal(("failed", "Job is not running"), ((string?)again["abort"], (string?)again["reason"]));
            Assert.Equal("Rundeck answered HTTP 404: Execution does not exist: 999999.", (await Assert.ThrowsAsync<InvalidOperationException>(() => All("execution", new() { ["execution"] = "999999" }))).Message);
        }
    }
}
