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
    /// Jenkins DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Jenkins server that refuses a call without the user's name and token and answers
    /// its actions with a redirect, as Jenkins does: a Jenkins connection made over the API, its token vaulted, tested; a
    /// published pipeline reads the jobs, builds a job with a parameter with an accent, reads its builds and the last one's
    /// log, copies the job - read back - and quiets the instance down; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class JenkinsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_builds_logs_copies_and_quiets_with_its_token()
        {
            var token = "jt" + Guid.NewGuid().ToString("N");
            using var jenkins = new JenkinsStandIn("kafe-ci:" + token);
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
                    Name = "Shop CI", ConnectorType = "jenkins", Endpoint = $"http://127.0.0.1:{jenkins.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "kafe-ci", ["password"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "CI" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("jobs", "Jobs", "jobs", "folder = shop"),
                        Call("build", "Build", "build", "job = shop/build\nparameters = {\"NOTE\": \"Kaffe på café\"}"),
                        Call("builds", "Builds", "builds", "job = shop/build\nmax = 2"),
                        Call("log", "Log", "console", "job = shop/build\nnumber = lastBuild"),
                        Call("copy", "Copy", "copy_job", "job = shop/build\nnew_job = build-copy"),
                        Call("quiet", "Quiet", "quiet_down", "reason = Fika"),
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
                var received = jenkins.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /me/api/json?tree=fullName,id",
                    "GET /job/shop/api/json?tree=jobs%5Bname%2CfullName%2Curl%2Ccolor%2Cbuildable%2Cdescription%5D",
                    "POST /job/shop/job/build/buildWithParameters NOTE=Kaffe+p%C3%A5+caf%C3%A9",
                    "GET /job/shop/job/build/api/json?tree=allBuilds%5Bnumber%2Curl%2CdisplayName%2Cdescription%2Cresult%2Cbuilding%2Ctimestamp%2Cduration%5D%7B0%2C2%7D",
                    "GET /job/shop/job/build/lastBuild/consoleText",
                    "POST /job/shop/createItem?name=build-copy&mode=copy&from=shop%2Fbuild",
                    "GET /job/shop/job/build-copy/api/json?tree=name%2CfullName%2Curl%2Cdescription%2Ccolor%2Cbuildable%2CinQueue%2CnextBuildNumber%2ClastBuild%5Bnumber%2Curl%2Cresult%2Ctimestamp%2Cduration%5D%2ClastCompletedBuild%5Bnumber%2Cresult%5D%2ClastSuccessfulBuild%5Bnumber%5D%2ClastFailedBuild%5Bnumber%5D%2ChealthReport%5Bscore%2Cdescription%5D",
                    "POST /quietDown?reason=Fika",
                    "GET /api/json?tree=mode%2CnodeName%2CquietingDown%2CuseSecurity",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(2, JsonNode.Parse(Outputs("jobs"))!["RecordCount"]!.GetValue<int>());
                Assert.Equal(7, JsonNode.Parse(Outputs("build"))!["Result"]![0]!["queue_item"]!.GetValue<long>());
                Assert.Equal(2, JsonNode.Parse(Outputs("builds"))!["RecordCount"]!.GetValue<int>());
                Assert.Contains("Finished: SUCCESS", JsonNode.Parse(Outputs("log"))!["Result"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.Equal("shop/build-copy", JsonNode.Parse(Outputs("copy"))!["Result"]![0]!["fullName"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("quiet"))!["Result"]![0]!["quietingDown"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Jenkins server that answers only a call carrying the user's name and token, every answer with Jenkins's own header.</summary>
        private sealed class JenkinsStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _basic;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public JenkinsStandIn(string userAndToken)
            {
                _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userAndToken));
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
                    var authorized = context.Request.Headers["Authorization"] == _basic;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    var path = raw.Split('?')[0];
                    string? location = null;
                    var (status, answer) = !authorized ? (401, "<html>Unauthorized</html>")
                        : (method, path) switch
                        {
                            ("GET", "/me/api/json") => (200, "{\"fullName\":\"Kafé CI\",\"id\":\"kafe-ci\"}"),
                            ("GET", "/job/shop/api/json") => (200, "{\"jobs\":[{\"name\":\"build\",\"fullName\":\"shop/build\",\"color\":\"blue\"},{\"name\":\"deploy\",\"fullName\":\"shop/deploy\",\"color\":\"red\"}]}"),
                            ("POST", "/job/shop/job/build/buildWithParameters") => (201, ""),
                            ("GET", "/job/shop/job/build/api/json") => (200, "{\"allBuilds\":[{\"number\":42,\"result\":\"SUCCESS\"},{\"number\":41,\"result\":\"FAILURE\"}]}"),
                            ("GET", "/job/shop/job/build/lastBuild/consoleText") => (200, "Started by user Ada\nKaffe på café\nFinished: SUCCESS\n"),
                            ("POST", "/job/shop/createItem") or ("POST", "/quietDown") => (302, ""),
                            ("GET", "/job/shop/job/build-copy/api/json") => (200, "{\"name\":\"build-copy\",\"fullName\":\"shop/build-copy\",\"color\":\"notbuilt\"}"),
                            ("GET", "/api/json") => (200, "{\"mode\":\"NORMAL\",\"quietingDown\":true,\"useSecurity\":true}"),
                            _ => (404, "<html>Not Found</html>"),
                        };
                    if (authorized && status == 201)
                    {
                        location = $"http://127.0.0.1:{Port}/queue/item/7/";
                    }
                    else if (authorized && status == 302)
                    {
                        location = $"http://127.0.0.1:{Port}/";
                    }

                    context.Response.StatusCode = status;
                    context.Response.Headers["X-Jenkins"] = "2.479.1";
                    if (location is not null)
                    {
                        context.Response.RedirectLocation = location;
                    }

                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = answer.StartsWith('{') ? "application/json" : "text/plain";
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
