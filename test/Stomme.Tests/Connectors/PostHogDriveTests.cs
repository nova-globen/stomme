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
    /// BL-229 - PostHog DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for PostHog's ingestion host that refuses a body without the project key it knows: a connection
    /// made over the API, tested; a published pipeline capturing an order for a person, identifying them and reading their feature
    /// flags; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PostHogDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for PostHog's ingestion host: its key in the body or a 401 as PostHog answers it, every call kept.</summary>
        private sealed class PostHogStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _key;

            public PostHogStandIn(string key)
            {
                _key = key;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Path, JsonObject Body)> Received { get; } = new();

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

                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? new JsonObject();
                    var path = context.Request.RawUrl ?? "/";
                    Received.Enqueue((path, body));
                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        _ when (string?)body["api_key"] != _key => (401, "{\"type\":\"authentication_error\",\"code\":\"invalid_api_key\",\"detail\":\"Project API key invalid.\"}"),
                        ("POST", "/batch/") => (200, "{\"status\":\"Ok\"}"),
                        ("POST", "/flags?v=2") => (200, "{\"flags\":{\"new-menu\":{\"key\":\"new-menu\",\"enabled\":true,\"variant\":\"b\",\"reason\":{\"code\":\"condition_match\"}}},\"errorsWhileComputingFlags\":false}"),
                        _ => (404, "{\"detail\":\"Not found.\"}"),
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
        public async Task A_pipeline_captures_identifies_and_reads_flags_through_posthog()
        {
            using var posthog = new PostHogStandIn("phc_drive_key");
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
                    Name = "Kafé analys", ConnectorType = "posthog", Endpoint = $"http://127.0.0.1:{posthog.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = "phc_drive_key" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans analys" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("order", "event", "event = order placed\ndistinct_id = user-42\nproperties = {\"total\": 129.5}"),
                        Call("who", "identify", "distinct_id = user-42\nproperties = {\"email\": \"ada@kafe.example\"}"),
                        Call("flags", "flags", "distinct_id = user-42"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.True(Outputs("order")["Result"]![0]!["captured"]!.GetValue<bool>());
                Assert.Equal("$identify", Outputs("who")["Result"]![0]!["event"]!.GetValue<string>());
                Assert.Equal(("new-menu", "b"), (Outputs("flags")["Result"]![0]!["flag"]!.GetValue<string>(), Outputs("flags")["Result"]![0]!["variant"]!.GetValue<string>()));
                var batches = posthog.Received.Where(request => request.Path == "/batch/").Select(request => request.Body["batch"]![0]!).ToList();
                Assert.Equal(new[] { "order placed", "$identify" }, batches.Select(item => item["event"]!.GetValue<string>()));
                Assert.Equal(129.5, batches[0]["properties"]!["total"]!.GetValue<double>());

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain("phc_drive_key", steps, StringComparison.Ordinal);
                Assert.DoesNotContain("phc_drive_key", graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
