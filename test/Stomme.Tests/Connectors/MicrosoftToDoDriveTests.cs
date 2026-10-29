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
    /// Microsoft To Do DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the shared OAuth2
    /// token provider and the extraction port, against a loopback stand-in for the tenant's token endpoint and Graph: a Microsoft To
    /// Do connection made over the API, its secret vaulted, tested (the user's lists named); a published pipeline makes a task with
    /// an HTML body, a due time and an importance, reads the list's open tasks over two pages through Graph's next link, finishes
    /// one and deletes it - an app-only token, every Graph call its bearer; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftToDoDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_finishes_and_deletes_tasks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var microsoft = new GraphStandIn();
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
                    Name = "Ada's To Do", ConnectorType = "microsofttodo", Endpoint = $"http://127.0.0.1:{microsoft.Port}/v1.0", TimeoutSeconds = 10,
                    Settings = new() { ["user"] = "ada@contoso.com" },
                    Credentials = new() { ["token-url"] = $"http://127.0.0.1:{microsoft.Port}/tenant/oauth2/v2.0/token", ["client-id"] = "app-1", ["client-secret"] = secret },
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
                        Call("make", "Buy milk", "create_task", "list = QWx0\ntitle = Buy milk\nbody = Oat <b>milk</b> & bread\nhtml = true\ndue = 2026-10-31T17:00:00+02:00\nimportance = high"),
                        Call("read", "Open ones", "tasks", "list = QWx0\nfilter = status ne 'completed'\nmax = 130"),
                        Call("done", "Done", "update_task", "list = QWx0\ntask = t7\nstatus = completed"),
                        Call("drop", "Drop", "delete_task", "list = QWx0\ntask = t7"),
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
                var received = microsoft.Received;
                var token = received.First(request => request.Path == "/tenant/oauth2/v2.0/token").Body;
                Assert.Contains("grant_type=client_credentials", token);
                Assert.Contains("client_secret=" + secret, token);
                Assert.All(received.Where(request => request.Path.StartsWith("/v1.0/", StringComparison.Ordinal)), request => Assert.Equal("Bearer access-drive", request.Authorization));
                const string tasks = "/v1.0/users/ada%40contoso.com/todo/lists/QWx0/tasks";
                Assert.Equal(new[]
                {
                    "GET /v1.0/users/ada%40contoso.com/todo/lists?$top=100",
                    "POST " + tasks + " {\"title\":\"Buy milk\",\"body\":{\"content\":\"Oat <b>milk</b> & bread\",\"contentType\":\"html\"},\"dueDateTime\":{\"dateTime\":\"2026-10-31T15:00:00\",\"timeZone\":\"UTC\"},\"importance\":\"high\"}",
                    "GET " + tasks + "?$top=100&$filter=status%20ne%20%27completed%27",
                    "GET " + tasks + "?$top=100&$skiptoken=next",
                    "PATCH " + tasks + "/t7 {\"status\":\"completed\"}",
                    "DELETE " + tasks + "/t7",
                }, received.Where(request => request.Path.StartsWith("/v1.0/", StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the tenant's token endpoint and Graph: the user's lists, a task made, a list's tasks over two pages, one finished and deleted.</summary>
        private sealed class GraphStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public GraphStandIn()
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

            private static string TaskJson(int n, string status = "notStarted") =>
                "{\"id\":\"t" + n + "\",\"title\":\"Errand " + n + "\",\"status\":\"" + status + "\",\"importance\":\"normal\",\"dueDateTime\":{\"dateTime\":\"2026-10-31T00:00:00.0000000\",\"timeZone\":\"UTC\"}}";

            private async Task ServeAsync()
            {
                const string tasks = "/v1.0/users/ada%40contoso.com/todo/lists/QWx0/tasks";
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
                        _received.Add((path, $"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 || path.EndsWith("/token", StringComparison.Ordinal) ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/tenant/oauth2/v2.0/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/v1.0/users/ada%40contoso.com/todo/lists") => (200, """{"value":[{"id":"QWx0","displayName":"Errands"}]}"""),
                        ("POST", tasks) => (201, TaskJson(7)),
                        ("GET", tasks) when url.Query.Contains("skiptoken=next", StringComparison.Ordinal) => (200, "{\"value\":[" + string.Join(",", Enumerable.Range(101, 100).Select(n => TaskJson(n))) + "]}"),
                        ("GET", tasks) => (200, "{\"value\":[" + string.Join(",", Enumerable.Range(1, 100).Select(n => TaskJson(n))) + "],\"@odata.nextLink\":\"http://127.0.0.1:" + Port + tasks + "?$top=100&$skiptoken=next\"}"),
                        ("PATCH", tasks + "/t7") => (200, TaskJson(7, "completed")),
                        ("DELETE", tasks + "/t7") => (204, ""),
                        _ => (404, """{"error":{"code":"ErrorItemNotFound","message":"Not found."}}"""),
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
