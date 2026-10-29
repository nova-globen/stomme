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
    /// Google Tasks DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the shared OAuth2 token
    /// provider and the extraction port, against a loopback stand-in for Google's token endpoint and the Tasks API: a Google Tasks
    /// connection made over the API, its secret and refresh token vaulted, tested (the lists named); a published pipeline makes a
    /// task under a parent, reads the list's open tasks over two pages by Google's token, finishes one, moves it and clears the
    /// finished ones - the refresh token exchanged once, every Tasks call its access token; the secrets in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleTasksDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_finishes_moves_and_clears_tasks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var google = new TasksStandIn();
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
                    Name = "My tasks", ConnectorType = "googletasks", Endpoint = $"http://127.0.0.1:{google.Port}/tasks/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Errands" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "Buy milk", "create_task", "list = QWx0\ntitle = Buy milk\nnotes = Oat\ndue = 2026-10-31\nparent = p1"),
                        Call("read", "Open ones", "tasks", "list = QWx0\nshow_completed = false\nmax = 130"),
                        Call("done", "Done", "update_task", "list = QWx0\ntask = t7\ncompleted = true"),
                        Call("move", "To the top", "move_task", "list = QWx0\ntask = t7"),
                        Call("clear", "Clear", "clear_completed", "list = QWx0"),
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
                var received = google.Received;
                Assert.Contains("refresh_token=" + refresh, received.First(request => request.Path == "/token").Body);
                Assert.All(received.Where(request => request.Path != "/token"), request => Assert.Equal("Bearer access-drive", request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /tasks/v1/users/@me/lists?maxResults=100",
                    "POST /tasks/v1/lists/QWx0/tasks?parent=p1 {\"title\":\"Buy milk\",\"notes\":\"Oat\",\"due\":\"2026-10-31T00:00:00.000Z\"}",
                    "GET /tasks/v1/lists/QWx0/tasks?showCompleted=false&showDeleted=false&showHidden=false&maxResults=100",
                    "GET /tasks/v1/lists/QWx0/tasks?showCompleted=false&showDeleted=false&showHidden=false&maxResults=30&pageToken=next",
                    "PATCH /tasks/v1/lists/QWx0/tasks/t7 {\"status\":\"completed\"}",
                    "POST /tasks/v1/lists/QWx0/tasks/t7/move",
                    "POST /tasks/v1/lists/QWx0/clear",
                }, received.Where(request => request.Path != "/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the Tasks API: the lists, a task made, a list's tasks over two pages, one finished, moved, the list cleared.</summary>
        private sealed class TasksStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public TasksStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string Body, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string TaskJson(int n, string status = "needsAction") => "{\"id\":\"t" + n + "\",\"title\":\"Errand " + n + "\",\"status\":\"" + status + "\",\"due\":\"2026-10-31T00:00:00.000Z\",\"position\":\"0001\"}";

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

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add((path, $"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 || path == "/token" ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/tasks/v1/users/@me/lists") => (200, """{"items":[{"id":"QWx0","title":"Errands"}]}"""),
                        ("POST", "/tasks/v1/lists/QWx0/tasks") => (200, TaskJson(7)),
                        ("GET", "/tasks/v1/lists/QWx0/tasks") when url.Query.Contains("pageToken=next", StringComparison.Ordinal) => (200, "{\"items\":[" + string.Join(",", Enumerable.Range(101, 100).Select(n => TaskJson(n))) + "]}"),
                        ("GET", "/tasks/v1/lists/QWx0/tasks") => (200, "{\"items\":[" + string.Join(",", Enumerable.Range(1, 100).Select(n => TaskJson(n))) + "],\"nextPageToken\":\"next\"}"),
                        ("PATCH", "/tasks/v1/lists/QWx0/tasks/t7") => (200, TaskJson(7, "completed")),
                        ("POST", "/tasks/v1/lists/QWx0/tasks/t7/move") => (200, TaskJson(7, "completed")),
                        ("POST", "/tasks/v1/lists/QWx0/clear") => (204, ""),
                        _ => (404, """{"error":{"code":404,"message":"Not found.","status":"NOT_FOUND"}}"""),
                    };
                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }

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
