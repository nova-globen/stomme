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
    /// BL-229 - Todoist DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Todoist's API: a Todoist connection made over the API, its token vaulted, tested (the user
    /// named); a published pipeline creates a task in a project with labels, reads the project's tasks over two pages by cursor,
    /// completes one, moves it to a section and comments on it - every call the token as the bearer; the token is in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TodoistDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_completes_moves_and_comments()
        {
            var token = "tdt" + Guid.NewGuid().ToString("N");
            using var todoist = new TodoistStandIn();
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
                    Name = "Errands", ConnectorType = "todoist", Endpoint = $"http://127.0.0.1:{todoist.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
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
                        Call("add", "Add the errand", "create_task", "project = 6Jf8VQXxpwv56VQ7\ncontent = Buy milk\nlabels = shop, errands\npriority = urgent\ndue_string = tomorrow at 5pm"),
                        Call("read", "The errands", "tasks", "project = 6Jf8VQXxpwv56VQ7\nmax = 230"),
                        Call("done", "Done", "close_task", "task = 6Jf8VQXxpwv59999"),
                        Call("move", "Move it", "move_task", "task = 6Jf8VQXxpwv59999\nsection = done_s"),
                        Call("note", "Note", "add_comment", "task = 6Jf8VQXxpwv59999\ncontent = Bought."),
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
                var received = todoist.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /api/v1/user",
                    "POST /api/v1/tasks {\"content\":\"Buy milk\",\"labels\":[\"shop\",\"errands\"],\"priority\":4,\"due_string\":\"tomorrow at 5pm\",\"project_id\":\"6Jf8VQXxpwv56VQ7\"}",
                    "GET /api/v1/tasks?project_id=6Jf8VQXxpwv56VQ7&limit=200",
                    "GET /api/v1/tasks?project_id=6Jf8VQXxpwv56VQ7&limit=30&cursor=c1",
                    "POST /api/v1/tasks/6Jf8VQXxpwv59999/close",
                    "POST /api/v1/tasks/6Jf8VQXxpwv59999/move {\"section_id\":\"done_s\"}",
                    "POST /api/v1/comments {\"task_id\":\"6Jf8VQXxpwv59999\",\"content\":\"Bought.\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 230", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Todoist's API: the user, a task made, a project's tasks over two pages, one completed, moved and commented on.</summary>
        private sealed class TodoistStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization)> _received = new();

            public TodoistStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string TaskJson(int n) => "{\"id\":\"6Jf8VQXxpwv5" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "\",\"content\":\"Errand " + n + "\",\"description\":\"\",\"project_id\":\"6Jf8VQXxpwv56VQ7\",\"labels\":[\"shop\"],\"priority\":1,\"due\":null,\"checked\":false,\"added_at\":\"2026-10-01T10:00:00Z\"}";

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
                        _received.Add(($"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/api/v1/user") => (200, "{\"id\":\"2671355\",\"full_name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}"),
                        ("POST", "/api/v1/tasks") => (200, TaskJson(9999)),
                        ("GET", "/api/v1/tasks") when url.Query.Contains("cursor=c1", StringComparison.Ordinal) => (200, "{\"results\":[" + string.Join(",", Enumerable.Range(201, 100).Select(TaskJson)) + "],\"next_cursor\":\"c2\"}"),
                        ("GET", "/api/v1/tasks") => (200, "{\"results\":[" + string.Join(",", Enumerable.Range(1, 200).Select(TaskJson)) + "],\"next_cursor\":\"c1\"}"),
                        ("POST", _) when path.EndsWith("/close", StringComparison.Ordinal) || path.EndsWith("/move", StringComparison.Ordinal) => (204, ""),
                        ("POST", "/api/v1/comments") => (200, "{\"id\":\"c1\",\"task_id\":\"6Jf8VQXxpwv59999\",\"content\":\"Bought.\",\"posted_at\":\"2026-10-12T08:00:00Z\"}"),
                        _ => (404, "{\"error\":\"Not found\",\"error_tag\":\"NOT_FOUND\",\"http_code\":404}"),
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
