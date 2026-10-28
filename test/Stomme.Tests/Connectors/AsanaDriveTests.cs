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
    /// BL-229 - Asana DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Asana's API: an Asana connection made over the API, its token vaulted, tested (the user
    /// named); a published pipeline adds a task to a project, comments on it, moves it to a section, completes it and reads the
    /// project's open tasks over two pages - each call a bearer; the token is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AsanaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_a_task_comments_moves_and_completes_it_and_reads_the_open_ones()
        {
            var token = "asana-" + Guid.NewGuid().ToString("N");
            using var asana = new AsanaStandIn();
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
                    Name = "Orders project", ConnectorType = "asana", Endpoint = $"http://127.0.0.1:{asana.Port}/api/1.0", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Order task" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        Call("add", "Add a task", "create_task", "project = 100\nname = Order 1001\nassignee = me\ndue = 2026-10-12"),
                        Call("comment", "Say so", "comment", "task = 201\ntext = Packed."),
                        Call("move", "Doing", "move_task", "task = 201\nsection = 301"),
                        Call("done", "Done", "update_task", "task = 201\ncompleted = true"),
                        Call("open", "Open tasks", "tasks", "project = 100"),
                    },
                };
                graph.Steps[4].OutputVariables["Result"] = "v-open";
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
                var received = asana.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                Assert.Equal("{\"data\":{\"name\":\"Order 1001\",\"assignee\":\"me\",\"due_on\":\"2026-10-12\",\"projects\":[\"100\"]}}", received.Single(request => request.Method == "POST" && request.Path == "/api/1.0/tasks").Body);
                Assert.Equal("{\"data\":{\"text\":\"Packed.\"}}", received.Single(request => request.Path == "/api/1.0/tasks/201/stories").Body);
                Assert.Equal("{\"data\":{\"task\":\"201\"}}", received.Single(request => request.Path == "/api/1.0/sections/301/addTask").Body);
                Assert.Equal("{\"data\":{\"completed\":true}}", received.Single(request => request.Method == "PUT").Body);
                Assert.Equal(2, received.Count(request => request.Method == "GET" && request.Path == "/api/1.0/projects/100/tasks")); // both pages

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Order 1007", steps);
                Assert.Contains("Order 1008", steps); // the second page's task too
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Asana's API: the user, a task added as 201, its comment, its move, its completion, the project's open tasks over two pages.</summary>
        private sealed class AsanaStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public AsanaStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string Query, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string TaskJson(string gid, string name) =>
                "{\"gid\":\"" + gid + "\",\"name\":\"" + name + "\",\"notes\":\"\",\"completed\":false,\"due_on\":null,\"assignee\":null,\"projects\":[{\"name\":\"Orders\"}],\"tags\":[],\"memberships\":[],\"permalink_url\":\"https://app.asana.example/0/100/" + gid + "\"}";

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

                    var path = context.Request.Url!.AbsolutePath;
                    var query = context.Request.Url.Query;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, query, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/api/1.0/users/me") => (200, "{\"data\":{\"gid\":\"9\",\"name\":\"Ops Bot\"}}"),
                        ("POST", "/api/1.0/tasks") => (201, "{\"data\":" + TaskJson("201", "Order 1001") + "}"),
                        ("POST", "/api/1.0/tasks/201/stories") => (201, "{\"data\":{\"gid\":\"555\"}}"),
                        ("POST", "/api/1.0/sections/301/addTask") => (200, "{\"data\":{}}"),
                        ("PUT", "/api/1.0/tasks/201") => (200, "{\"data\":" + TaskJson("201", "Order 1001") + "}"),
                        ("GET", "/api/1.0/projects/100/tasks") when query.Contains("offset=o2", StringComparison.Ordinal) => (200, "{\"data\":[" + TaskJson("208", "Order 1008") + "],\"next_page\":null}"),
                        ("GET", "/api/1.0/projects/100/tasks") => (200, "{\"data\":[" + TaskJson("207", "Order 1007") + "],\"next_page\":{\"offset\":\"o2\"}}"),
                        _ => (404, "{\"errors\":[{\"message\":\"Not Found\"}]}"),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
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
