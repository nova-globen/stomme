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
    /// BL-229 - Kit DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Kit's API that refuses a call without the key: a Kit connection made over the API, its
    /// key vaulted, tested; a published pipeline creates a subscriber with an accent in a field, adds them to a form and tags
    /// them, reads the subscribers over two pages as long as Kit says there is a next - no more than asked - and the tags,
    /// and untags the subscriber; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class KitDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_adds_tags_reads_and_untags_subscribers_with_its_key()
        {
            var key = "kk" + Guid.NewGuid().ToString("N");
            using var kit = new KitStandIn(key);
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
                    Name = "Shop creators", ConnectorType = "kit", Endpoint = $"http://127.0.0.1:{kit.Port}/v4", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Creators" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Create", "create_subscriber", "email = ada@kafe.example.test\nfirst_name = Ada\nfields = {\"cafe\": \"Kafé\"}"),
                        Call("form", "Form", "add_to_form", "form = 5\nemail = ada@kafe.example.test"),
                        Call("tag", "Tag", "tag_subscriber", "tag = 9\nemail = ada@kafe.example.test"),
                        Call("read", "Read", "subscribers", "max = 120"),
                        Call("tags", "Tags", "tags", ""),
                        Call("untag", "Untag", "untag_subscriber", "tag = 9\nemail = ada@kafe.example.test"),
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
                var received = kit.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v4/account",
                    "POST /v4/subscribers {\"email_address\":\"ada@kafe.example.test\",\"first_name\":\"Ada\",\"fields\":{\"cafe\":\"Kafé\"}}",
                    "POST /v4/forms/5/subscribers {\"email_address\":\"ada@kafe.example.test\"}",
                    "POST /v4/tags/9/subscribers {\"email_address\":\"ada@kafe.example.test\"}",
                    "GET /v4/subscribers?per_page=120",
                    "GET /v4/subscribers?per_page=120&after=WzEwMDBd",
                    "GET /v4/tags?per_page=1000",
                    "DELETE /v4/tags/9/subscribers?email_address=ada%40kafe.example.test",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(31, JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal("https://kafe.example.test", JsonNode.Parse(Outputs("form"))!["Result"]![0]!["referrer"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("fika", JsonNode.Parse(Outputs("tags"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("untag"))!["Result"]![0]!["untagged"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Kit's API that answers only a call carrying the key.</summary>
        private sealed class KitStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public KitStandIn(string key)
            {
                _key = key;
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
                    var authorized = context.Request.Headers["X-Kit-Api-Key"] == _key;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Subscribers(int first, int count, bool next) => "{\"subscribers\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + (1000 + i) + ",\"email_address\":\"s" + i + "@kafe.example.test\"}")) + "],\"pagination\":{\"has_next_page\":" + (next ? "true" : "false") + ",\"end_cursor\":\"WzEwMDBd\"}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"errors\":[\"The access token is invalid\"]}")
                        : (method, path) switch
                        {
                            ("GET", "/v4/account") => (200, "{\"user\":{\"email\":\"ada@kafe.example.test\"},\"account\":{\"name\":\"Kafé\"}}"),
                            ("POST", "/v4/subscribers") => (201, "{\"subscriber\":{\"id\":31,\"first_name\":\"Ada\",\"email_address\":\"ada@kafe.example.test\",\"state\":\"active\",\"fields\":{\"cafe\":\"Kafé\"}}}"),
                            ("POST", "/v4/forms/5/subscribers") => (201, "{\"subscriber\":{\"id\":31,\"email_address\":\"ada@kafe.example.test\",\"referrer\":\"https://kafe.example.test\"}}"),
                            ("POST", "/v4/tags/9/subscribers") => (201, "{\"subscriber\":{\"id\":31,\"email_address\":\"ada@kafe.example.test\",\"tagged_at\":\"2026-10-13T10:00:00Z\"}}"),
                            ("GET", "/v4/subscribers") => (200, raw.Contains("after=", StringComparison.Ordinal) ? Subscribers(101, 80, false) : Subscribers(1, 100, true)),
                            ("GET", "/v4/tags") => (200, "{\"tags\":[{\"id\":9,\"name\":\"fika\"}],\"pagination\":{\"has_next_page\":false}}"),
                            ("DELETE", "/v4/tags/9/subscribers") => (204, ""),
                            _ => (404, "{\"errors\":[\"Not Found\"]}"),
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
