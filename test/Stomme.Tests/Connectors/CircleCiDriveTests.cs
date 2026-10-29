#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// CircleCI DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for CircleCI's API v2 that refuses a call without the token in its Circle-Token header: a
    /// CircleCI connection made over the API, its project a setting, tested; a published pipeline triggering a build on a branch
    /// with its parameters from the step's Query box, reading the workflows of the pipeline it started - an expression over the
    /// trigger's result - and listing the branch's pipelines; the token in neither the graph, nor the run, nor any address asked.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CircleCiDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Token = "circle-drive-token";
        private const string Started = "5034460f-c7c4-4c43-9457-de07e2029e7b";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for CircleCI's API v2: the token in its header or a 401, every call and body kept.</summary>
        private sealed class CircleStandIn : IDisposable
        {
            private readonly HttpListener _listener;

            public CircleStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<string> Asked { get; } = new();

            public ConcurrentQueue<string> Bodies { get; } = new();

            public void Dispose() => _listener.Close();

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

                    var path = context.Request.RawUrl ?? "/";
                    Asked.Enqueue($"{context.Request.HttpMethod} {path}");
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    if (body.Length > 0)
                    {
                        Bodies.Enqueue(body);
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        _ when context.Request.Headers["Circle-Token"] != Token => (401, "{\"message\":\"You must log in first.\"}"),
                        ("GET", "/api/v2/me") => (200, "{\"name\":\"Ada Lovelace\",\"login\":\"ada\",\"id\":\"8a7e3c7d-0000-4000-8000-000000000001\"}"),
                        ("POST", "/api/v2/project/gh/kafe/kassa/pipeline") => (201, $"{{\"id\":\"{Started}\",\"state\":\"pending\",\"number\":143,\"created_at\":\"2026-10-18T09:00:00Z\"}}"),
                        ("GET", $"/api/v2/pipeline/{Started}/workflow") => (200, "{\"items\":[{\"id\":\"w1\",\"name\":\"build\",\"status\":\"success\",\"pipeline_number\":143},{\"id\":\"w2\",\"name\":\"deploy\",\"status\":\"on_hold\",\"pipeline_number\":143}],\"next_page_token\":null}"),
                        ("GET", "/api/v2/project/gh/kafe/kassa/pipeline?branch=release%2F2026.10") => (200, $"{{\"items\":[{{\"id\":\"{Started}\",\"number\":143,\"state\":\"created\",\"trigger\":{{\"type\":\"api\",\"actor\":{{\"login\":\"ada\"}}}},\"vcs\":{{\"branch\":\"release/2026.10\",\"revision\":\"a1b2c3d\"}}}}],\"next_page_token\":null}}"),
                        _ => (404, "{\"message\":\"Not found\"}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        [Fact]
        public async Task A_pipeline_triggers_a_build_and_reads_how_it_went_through_circleci()
        {
            using var circle = new CircleStandIn();
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
                    Name = "Kassans bygge", ConnectorType = "circleci", Endpoint = $"http://127.0.0.1:{circle.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["project"] = "kafe/kassa" },
                    Credentials = new() { ["api-key"] = Token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans release" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var trigger = Call("trigger", "pipeline_trigger");
                trigger.Inputs["Parameters"] = "branch = release/2026.10";
                trigger.Inputs["Query"] = "{\n  \"deploy\": true,\n  \"target\": \"uat\"\n}";
                trigger.OutputVariables["Result"] = "v-started";
                var workflows = Call("workflows", "pipeline_workflows");
                workflows.InputExpressions["Parameters"] = "pipeline = {{ started[0].id }}";
                var recent = Call("recent", "pipelines");
                recent.Inputs["Parameters"] = "branch = release/2026.10\nmax = 10";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-started", Name = "started" } }, Steps = { trigger, workflows, recent } };
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(143, Outputs("trigger")["Result"]![0]!["number"]!.GetValue<long>());
                Assert.Equal(new[] { "build:success", "deploy:on_hold" }, Outputs("workflows")["Result"]!.AsArray().Select(workflow => $"{workflow!["name"]!.GetValue<string>()}:{workflow["status"]!.GetValue<string>()}"));
                var listed = Outputs("recent")["Result"]![0]!;
                Assert.Equal(("release/2026.10", "a1b2c3d", "ada"), (listed["branch"]!.GetValue<string>(), listed["revision"]!.GetValue<string>(), listed["actor"]!.GetValue<string>()));

                Assert.Equal(new[] { "GET /api/v2/me", "POST /api/v2/project/gh/kafe/kassa/pipeline", $"GET /api/v2/pipeline/{Started}/workflow", "GET /api/v2/project/gh/kafe/kassa/pipeline?branch=release%2F2026.10" }, circle.Asked);
                Assert.Equal("{\"branch\":\"release/2026.10\",\"parameters\":{\"deploy\":true,\"target\":\"uat\"}}", Assert.Single(circle.Bodies));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain(circle.Asked, path => path.Contains(Token, StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
