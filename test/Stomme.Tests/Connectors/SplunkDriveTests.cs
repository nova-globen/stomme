#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Splunk DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Splunk server's management port that refuses a call without the token: a Splunk
    /// connection made over the API, its token vaulted, tested; a published pipeline runs a search blocking, reads its results
    /// over two pages matched by a quoted value - no more than asked - makes a report with an accent from the job, gives a
    /// user roles and deletes the job; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SplunkDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_runs_a_search_reads_its_results_and_keeps_a_report_and_a_user_with_its_token()
        {
            var token = "eyJ" + Guid.NewGuid().ToString("N");
            using var splunk = new SplunkStandIn(token);
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé logs", ConnectorType = "splunk", Endpoint = $"http://127.0.0.1:{splunk.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Till errors" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("search", "Search", "create_search", "search = index=main error\nearliest_time = -24h@h"),
                        Call("results", "Results", "search_results", "job = 1697123456.42\nfield = host\nvalue = web-01\nmax = 120"),
                        Call("report", "Report", "create_report", "name = Fika & bröd\njob = 1697123456.42"),
                        Call("user", "User", "update_user", "user = ada\nroles = user, power"),
                        Call("drop", "Drop", "delete_search_job", "job = 1697123456.42"),
                    },
                };
                for (var i = 1; i < graph.Steps.Count; i++)
                {
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = graph.Steps[i - 1].Id, SourcePort = "Done", TargetId = graph.Steps[i].Id });
                }

                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);
                var received = splunk.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /services/authentication/current-context?output_mode=json",
                    "POST /services/search/jobs?output_mode=json search=search+index%3Dmain+error&exec_mode=blocking&earliest_time=-24h%40h",
                    "GET /services/search/jobs/1697123456.42?output_mode=json",
                    "GET /services/search/jobs/1697123456.42/results?output_mode=json&search=search%20host%3D%22web-01%22&count=100&offset=0",
                    "GET /services/search/jobs/1697123456.42/results?output_mode=json&search=search%20host%3D%22web-01%22&count=100&offset=100",
                    "GET /services/search/jobs/1697123456.42?output_mode=json",
                    "POST /services/saved/searches?output_mode=json name=Fika+%26+br%C3%B6d&search=search+index%3Dmain+error&dispatch.earliest_time=-24h%40h&dispatch.latest_time=now",
                    "POST /services/authentication/users/ada?output_mode=json roles=user&roles=power",
                    "DELETE /services/search/jobs/1697123456.42?output_mode=json",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("DONE", JsonNode.Parse(Outputs("search"))!["Result"]![0]!["dispatchState"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("results"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Fika & bröd", JsonNode.Parse(Outputs("report"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("[\"user\",\"power\"]", JsonNode.Parse(Outputs("user"))!["Result"]![0]!["roles"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Splunk server's management port that answers only a call carrying the token.</summary>
        private sealed class SplunkStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public SplunkStandIn(string token)
            {
                _token = token;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Authorized)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var raw = context.Request.RawUrl ?? "";
                    var method = context.Request.HttpMethod;
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Results(int first, int count) => "{\"preview\":false,\"results\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"host\":\"web-01\",\"_raw\":\"error " + i + "\"}")) + "]}";
                    const string Job = "{\"entry\":[{\"name\":\"search index=main error\",\"id\":\"https://splunk.kafe.example.test:8089/services/search/jobs/1697123456.42\",\"content\":{\"sid\":\"1697123456.42\",\"dispatchState\":\"DONE\",\"request\":{\"search\":\"search index=main error\",\"earliest_time\":\"-24h@h\",\"latest_time\":\"now\"}}}]}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"messages\":[{\"type\":\"WARN\",\"text\":\"call not properly authenticated\"}]}")
                        : (method, path) switch
                        {
                            ("GET", "/services/authentication/current-context") => (200, "{\"entry\":[{\"name\":\"tokens\",\"content\":{\"username\":\"admin\"}}]}"),
                            ("POST", "/services/search/jobs") => (201, "{\"sid\":\"1697123456.42\"}"),
                            ("GET", "/services/search/jobs/1697123456.42") => (200, Job),
                            ("GET", "/services/search/jobs/1697123456.42/results") => (200, raw.Contains("offset=100", StringComparison.Ordinal) ? Results(101, 50) : Results(1, 100)),
                            ("POST", "/services/saved/searches") => (201, "{\"entry\":[{\"name\":\"Fika & bröd\",\"id\":\"https://splunk.kafe.example.test:8089/servicesNS/nobody/search/saved/searches/Fika%20%26%20br%C3%B6d\",\"content\":{\"search\":\"search index=main error\"}}]}"),
                            ("POST", "/services/authentication/users/ada") => (200, "{\"entry\":[{\"name\":\"ada\",\"id\":\"https://splunk.kafe.example.test:8089/services/authentication/users/ada\",\"content\":{\"roles\":[\"user\",\"power\"]}}]}"),
                            ("DELETE", "/services/search/jobs/1697123456.42") => (200, "{\"messages\":[{\"type\":\"INFO\",\"text\":\"Search job cancelled.\"}]}"),
                            _ => (404, "{\"messages\":[{\"type\":\"ERROR\",\"text\":\"Not Found\"}]}"),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
