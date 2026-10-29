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
    /// Urlscan.io DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for urlscan.io's API v1 that refuses a call without the key in its API-Key header and answers
    /// 404 for a scan's result until it has been asked a few times: a urlscan.io connection made over the API, its key vaulted,
    /// tested; a published pipeline scanning a URL and waiting for its verdict, then searching the scans of the page's domain - an
    /// expression over the verdict's result; the key in neither the graph, nor the run, nor any address asked.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class UrlScanIoDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Key = "urlscan-drive-key";
        private const string Uuid = "0196c4c4-9a2b-7000-8000-0123456789ab";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for urlscan.io: its key in the header or a 401, a result pending for its first two asks.</summary>
        private sealed class UrlScanStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private int _asked;

            public UrlScanStandIn()
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

                    var (status, answer) = (context.Request.HttpMethod, path.Split('?')[0]) switch
                    {
                        _ when context.Request.Headers["API-Key"] != Key => (401, "{\"message\":\"No API key supplied\",\"status\":401}"),
                        ("GET", "/api/v1/user/quotas/") => (200, "{\"source\":\"api-key\",\"limits\":{\"public\":{\"day\":{\"limit\":5000,\"used\":1,\"remaining\":4999}}}}"),
                        ("POST", "/api/v1/scan/") => (200, $"{{\"message\":\"Submission successful\",\"uuid\":\"{Uuid}\",\"visibility\":\"private\",\"url\":\"https://kassa.example/login\"}}"),
                        ("GET", $"/api/v1/result/{Uuid}/") when Interlocked.Increment(ref _asked) <= 2 => (404, "{\"message\":\"Scan is not finished yet\",\"status\":404}"),
                        ("GET", $"/api/v1/result/{Uuid}/") => (200, $"{{\"task\":{{\"uuid\":\"{Uuid}\",\"screenshotURL\":\"https://urlscan.io/screenshots/{Uuid}.png\"}},\"page\":{{\"url\":\"https://kassa.example/login\",\"domain\":\"kassa.example\",\"ip\":\"203.0.113.7\",\"country\":\"SE\"}},\"verdicts\":{{\"overall\":{{\"score\":100,\"malicious\":true}}}}}}"),
                        ("GET", "/api/v1/search/") => (200, $"{{\"results\":[{{\"_id\":\"{Uuid}\",\"page\":{{\"domain\":\"kassa.example\"}},\"sort\":[1792000000000,\"{Uuid}\"]}}],\"total\":1,\"has_more\":false}}"),
                        _ => (404, "{\"message\":\"Not found\",\"status\":404}"),
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
        public async Task A_pipeline_scans_a_url_waits_for_its_verdict_and_searches_its_domain()
        {
            using var urlscan = new UrlScanStandIn();
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
                    Name = "Kafé länkkoll", ConnectorType = "urlscanio", Endpoint = $"http://127.0.0.1:{urlscan.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = Key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans länkar" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var scan = Call("scan", "scan");
                scan.Inputs["Parameters"] = "url = https://kassa.example/login\ntags = phishing\nwait = true";
                scan.OutputVariables["Result"] = "v-verdict";
                var seen = Call("seen", "search");
                seen.InputExpressions["Parameters"] = "query = domain:{{ verdict[0].page_domain }}\nmax = 10";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-verdict", Name = "verdict" } }, Steps = { scan, seen } };
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "scan", SourcePort = "Done", TargetId = "seen" });

                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 600 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var verdict = Outputs("scan")["Result"]![0]!;
                Assert.Equal((Uuid, true, 100L, "kassa.example"), (verdict["id"]!.GetValue<string>(), verdict["malicious"]!.GetValue<bool>(), verdict["score"]!.GetValue<long>(), verdict["page_domain"]!.GetValue<string>()));
                Assert.Equal(Uuid, Outputs("seen")["Result"]![0]!["id"]!.GetValue<string>());

                Assert.Equal(new[] { "GET /api/v1/user/quotas/", "POST /api/v1/scan/", $"GET /api/v1/result/{Uuid}/", $"GET /api/v1/result/{Uuid}/", $"GET /api/v1/result/{Uuid}/", "GET /api/v1/search/?size=10&q=domain%3Akassa.example" }, urlscan.Asked);
                Assert.Equal("{\"url\":\"https://kassa.example/login\",\"visibility\":\"private\",\"tags\":[\"phishing\"]}", Assert.Single(urlscan.Bodies));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Key, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain(urlscan.Asked, path => path.Contains(Key, StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
