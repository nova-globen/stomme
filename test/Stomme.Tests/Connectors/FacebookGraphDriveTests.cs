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
    /// The Facebook Graph API DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for the Graph that refuses a call without the token in its header - or with
    /// one in its address: a Graph connection made over the API at a version, its token vaulted, tested; a published pipeline
    /// reads a Page, posts to its feed with an accent, reads the feed over two pages - no more than asked - and deletes the
    /// post; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class FacebookGraphDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_a_page_posts_reads_its_feed_and_deletes_the_post_with_its_token()
        {
            var token = "fb" + Guid.NewGuid().ToString("N");
            using var meta = new GraphStandIn(token);
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
                    Name = "Shop Page", ConnectorType = "facebookgraph", Endpoint = $"http://127.0.0.1:{meta.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["version"] = "v21.0" },
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Page" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("page", "Page", "get", "node = 123\nfields = id,name,fan_count"),
                        Call("post", "Post", "post", "node = 123\nedge = feed\nfields = {\"message\": \"Fika på café\"}"),
                        Call("feed", "Feed", "get", "node = 123\nedge = feed\nfields = message\nmax = 120"),
                        Call("delete", "Delete", "delete", "node = 123_9"),
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
                var received = meta.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token, or one in the address: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v21.0/me?fields=id,name",
                    "GET /v21.0/123?fields=id%2Cname%2Cfan_count",
                    "POST /v21.0/123/feed {\"message\":\"Fika på café\"}",
                    "GET /v21.0/123/feed?fields=message&limit=100",
                    "GET /v21.0/123/feed?fields=message&limit=100&after=c2",
                    "DELETE /v21.0/123_9",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Kafé", JsonNode.Parse(Outputs("page"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal("123_9", JsonNode.Parse(Outputs("post"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("feed"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.True(JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["success"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the Graph that answers only a call carrying the token in its header, and none in its address.</summary>
        private sealed class GraphStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GraphStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token && !raw.Contains("access_token", StringComparison.Ordinal);
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Posts(int from, int count, string after, string next) => "{\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"123_" + i + "\",\"message\":\"Fika " + i + "\"}")) + "],\"paging\":{\"cursors\":{\"after\":\"" + after + "\"},\"next\":\"" + next + "\"}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (400, "{\"error\":{\"message\":\"Invalid OAuth access token.\",\"type\":\"OAuthException\",\"code\":190}}")
                        : (method, path) switch
                        {
                            ("GET", "/v21.0/me") => (200, "{\"id\":\"123\",\"name\":\"Kafé\"}"),
                            ("GET", "/v21.0/123") => (200, "{\"id\":\"123\",\"name\":\"Kafé\",\"fan_count\":42}"),
                            ("POST", "/v21.0/123/feed") => (200, "{\"id\":\"123_9\"}"),
                            ("GET", "/v21.0/123/feed") => (200, raw.Contains("after=c2", StringComparison.Ordinal) ? Posts(101, 80, "c3", "") : Posts(1, 100, "c2", "http://127.0.0.1/next")),
                            ("DELETE", "/v21.0/123_9") => (200, "{\"success\":true}"),
                            _ => (400, "{\"error\":{\"message\":\"Unsupported request.\",\"type\":\"GraphMethodException\",\"code\":100}}"),
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
