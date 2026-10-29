#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Hunter DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Hunter's API v2 that refuses a call without the key in its X-API-KEY header: a Hunter
    /// connection made over the API, its key vaulted, tested; a published pipeline searching a domain's executives, finding a
    /// person's address and verifying it - Hunter still verifying at the first ask - and the address found read by the verifier
    /// through an expression over the finder's result; the key in neither the graph, nor the run, nor any address asked.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HunterDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Key = "hunter-drive-key";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for Hunter's API v2: its key in the header or a 401, every address asked kept.</summary>
        private sealed class HunterStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private int _verifying;

            public HunterStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<string> Asked { get; } = new();

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
                    Asked.Enqueue(path);
                    var route = path.Split('?')[0];
                    var (status, answer) = route switch
                    {
                        _ when context.Request.Headers["X-API-KEY"] != Key => (401, "{\"errors\":[{\"id\":\"authentication_failed\",\"code\":401,\"details\":\"No user found for the API key supplied\"}]}"),
                        "/v2/account" => (200, "{\"data\":{\"plan_name\":\"Starter\",\"requests\":{\"searches\":{\"used\":3,\"available\":500}}}}"),
                        "/v2/domain-search" => (200, "{\"data\":{\"domain\":\"kafe.example\",\"organization\":\"Kafé Kassan\",\"emails\":[{\"value\":\"ada@kafe.example\",\"type\":\"personal\",\"confidence\":97,\"seniority\":\"executive\"},{\"value\":\"grace@kafe.example\",\"type\":\"personal\",\"confidence\":91,\"seniority\":\"executive\"}]},\"meta\":{\"results\":2}}"),
                        "/v2/email-finder" => (200, "{\"data\":{\"first_name\":\"Ada\",\"last_name\":\"Lovelace\",\"email\":\"ada@kafe.example\",\"score\":97}}"),
                        "/v2/email-verifier" when Interlocked.Increment(ref _verifying) == 1 => (202, "{\"data\":{\"status\":\"unknown\"}}"),
                        "/v2/email-verifier" => (200, "{\"data\":{\"status\":\"valid\",\"score\":100,\"email\":\"ada@kafe.example\"}}"),
                        _ => (404, "{\"errors\":[{\"id\":\"not_found\",\"code\":404,\"details\":\"Not found\"}]}"),
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
        public async Task A_pipeline_searches_finds_and_verifies_through_hunter()
        {
            using var hunter = new HunterStandIn();
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
                    Name = "Kafé kontakter", ConnectorType = "hunter", Endpoint = $"http://127.0.0.1:{hunter.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = Key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans kontakter" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var search = Call("search", "domain_search");
                search.Inputs["Parameters"] = "domain = kafe.example\nseniority = executive\nmax = 5";
                var find = Call("find", "email_finder");
                find.Inputs["Parameters"] = "domain = kafe.example\nfirst_name = Ada\nlast_name = Lovelace";
                find.OutputVariables["Result"] = "v-found";
                var verify = Call("verify", "email_verifier");
                verify.InputExpressions["Parameters"] = "email = {{ found[0].email }}";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-found", Name = "found" } }, Steps = { search, find, verify } };
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
                Assert.Equal(new[] { "ada@kafe.example", "grace@kafe.example" }, Outputs("search")["Result"]!.AsArray().Select(found => found!["value"]!.GetValue<string>()));
                Assert.Equal(97, Outputs("find")["Result"]![0]!["score"]!.GetValue<long>());
                Assert.Equal("valid", Outputs("verify")["Result"]![0]!["status"]!.GetValue<string>());
                Assert.Contains("/v2/domain-search?domain=kafe.example&seniority=executive&limit=5&offset=0", hunter.Asked);
                Assert.Equal(2, hunter.Asked.Count(path => path == "/v2/email-verifier?email=ada%40kafe.example")); // asked again while Hunter was still verifying
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Key, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain(hunter.Asked, path => path.Contains(Key, StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
