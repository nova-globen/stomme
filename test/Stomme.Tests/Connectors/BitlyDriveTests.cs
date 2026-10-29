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
    /// Bitly DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Bitly's API v4 that refuses a call without the token: a Bitly connection made over the
    /// API, its token vaulted, tested; a published pipeline shortens an address with an accent, gets and updates the link,
    /// reads its clicks' total and the default group's links over two pages - no more than asked; the token in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BitlyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_shortens_gets_updates_and_counts_a_link_and_reads_the_groups_links_with_its_token()
        {
            var token = "bt" + Guid.NewGuid().ToString("N");
            using var bitly = new BitlyStandIn(token);
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
                    Name = "Shop links", ConnectorType = "bitly", Endpoint = $"http://127.0.0.1:{bitly.Port}/v4", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Links" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Shorten", "create", "long_url = https://example.test/café\ntitle = Café\ntags = fika"),
                        Call("get", "Get", "link", "link = bit.ly/3xYz9"),
                        Call("update", "Archive", "update", "link = https://bit.ly/3xYz9\narchived = true"),
                        Call("clicks", "Clicks", "clicks", "link = bit.ly/3xYz9\ntotal = true"),
                        Call("links", "Links", "links", "max = 120"),
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
                var received = bitly.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v4/user",
                    "POST /v4/bitlinks {\"long_url\":\"https://example.test/café\",\"title\":\"Café\",\"tags\":[\"fika\"]}",
                    "GET /v4/bitlinks/bit.ly/3xYz9",
                    "PATCH /v4/bitlinks/bit.ly/3xYz9 {\"archived\":true}",
                    "GET /v4/bitlinks/bit.ly/3xYz9/clicks/summary?unit=day&units=-1",
                    "GET /v4/user",
                    "GET /v4/groups/Bdef1/bitlinks?size=100",
                    "GET /v4/groups/Bdef1/bitlinks?size=100&search_after=c2",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("https://bit.ly/3xYz9", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["link"]!.GetValue<string>());
                Assert.Equal("Café", JsonNode.Parse(Outputs("get"))!["Result"]![0]!["title"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("update"))!["Result"]![0]!["archived"]!.GetValue<bool>());
                Assert.Equal(42, JsonNode.Parse(Outputs("clicks"))!["Result"]![0]!["clicks"]!.GetValue<long>());
                Assert.Equal(120, JsonNode.Parse(Outputs("links"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Bitly's API v4 that answers only a call carrying the token.</summary>
        private sealed class BitlyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public BitlyStandIn(string token)
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

                    string Links(int from, int count, string after, string next) => "{\"links\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"bit.ly/l" + i + "\",\"title\":\"Fika " + i + "\"}")) + "],\"pagination\":{\"search_after\":\"" + after + "\",\"next\":\"" + next + "\",\"size\":100}}";
                    const string Link = "{\"id\":\"bit.ly/3xYz9\",\"link\":\"https://bit.ly/3xYz9\",\"long_url\":\"https://example.test/café\",\"title\":\"Café\",\"archived\":false,\"tags\":[\"fika\"]}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (403, "{\"message\":\"FORBIDDEN\"}")
                        : (method, path) switch
                        {
                            ("GET", "/v4/user") => (200, "{\"login\":\"ada\",\"name\":\"Ada\",\"default_group_guid\":\"Bdef1\"}"),
                            ("POST", "/v4/bitlinks") => (200, Link),
                            ("GET", "/v4/bitlinks/bit.ly/3xYz9") => (200, Link),
                            ("PATCH", "/v4/bitlinks/bit.ly/3xYz9") => (200, Link.Replace("\"archived\":false", "\"archived\":true", StringComparison.Ordinal)),
                            ("GET", "/v4/bitlinks/bit.ly/3xYz9/clicks/summary") => (200, "{\"total_clicks\":42,\"units\":-1,\"unit\":\"day\"}"),
                            ("GET", "/v4/groups/Bdef1/bitlinks") => (200, raw.Contains("search_after=c2", StringComparison.Ordinal) ? Links(101, 80, "", "") : Links(1, 100, "c2", "http://127.0.0.1/next")),
                            _ => (404, "{\"message\":\"NOT_FOUND\"}"),
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
