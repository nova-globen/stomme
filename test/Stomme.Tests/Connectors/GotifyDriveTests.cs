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
    /// BL-229 - Gotify DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Gotify server that takes only the application's token to send and the client's to
    /// read and delete: a Gotify connection made over the API, its tokens vaulted, tested; a published pipeline sends a
    /// Markdown message with an accent, reads the messages over two pages by Gotify's since - no more than asked - deletes one
    /// and lists the applications; the tokens in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GotifyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_reads_and_deletes_messages_with_its_tokens()
        {
            var application = "A" + Guid.NewGuid().ToString("N")[..14];
            var clientToken = "C" + Guid.NewGuid().ToString("N")[..14];
            using var gotify = new GotifyStandIn(application, clientToken);
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
                    Name = "Shop pings", ConnectorType = "gotify", Endpoint = $"http://127.0.0.1:{gotify.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = application, ["password"] = clientToken },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika pings" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("send", "Send", "create_message", "message = **Fika** at 3 - Kafé\ntitle = Fika\npriority = 8\ncontent_type = text/markdown"),
                        Call("read", "Read", "messages", "max = 120"),
                        Call("delete", "Delete", "delete_message", "id = 301"),
                        Call("apps", "Apps", "applications", ""),
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
                var received = gotify.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "the wrong token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /current/user",
                    "POST /message {\"message\":\"**Fika** at 3 - Kafé\",\"title\":\"Fika\",\"priority\":8,\"extras\":{\"client::display\":{\"contentType\":\"text/markdown\"}}}",
                    "GET /message?limit=100",
                    "GET /message?limit=100&since=201",
                    "DELETE /message/301",
                    "GET /application",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(301, JsonNode.Parse(Outputs("send"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.True(JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Equal("Kassa", JsonNode.Parse(Outputs("apps"))!["Result"]![0]!["name"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var token in new[] { application, clientToken })
                {
                    Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Gotify server: the application's token to send, the client's for all else.</summary>
        private sealed class GotifyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _application;
            private readonly string _client;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GotifyStandIn(string application, string client)
            {
                _application = application;
                _client = client;
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
                    var authorized = context.Request.Headers["X-Gotify-Key"] == (method == "POST" ? _application : _client);
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Page(int newest, int count, bool next) =>
                        "{\"messages\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => "{\"id\":" + (newest - i) + ",\"appid\":1,\"message\":\"Fika " + (newest - i) + "\"}")) + "],\"paging\":{\"size\":" + count + ",\"limit\":100,\"since\":" + (newest - count + 1)
                        + (next ? ",\"next\":\"http://127.0.0.2:9/message?limit=100&since=" + (newest - count + 1) + "\"" : "") + "}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"error\":\"Unauthorized\",\"errorCode\":401}")
                        : (method, path) switch
                        {
                            ("GET", "/current/user") => (200, "{\"id\":1,\"name\":\"ada\",\"admin\":true}"),
                            ("POST", "/message") => (200, "{\"id\":301,\"appid\":1,\"message\":\"**Fika** at 3 - Kafé\"}"),
                            ("GET", "/message") => (200, raw.Contains("since=201", StringComparison.Ordinal) ? Page(200, 80, false) : Page(300, 100, true)),
                            ("DELETE", "/message/301") => (200, ""),
                            ("GET", "/application") => (200, "[{\"id\":1,\"name\":\"Kassa\",\"internal\":false}]"),
                            _ => (404, "{\"error\":\"Not Found\",\"errorCode\":404}"),
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
