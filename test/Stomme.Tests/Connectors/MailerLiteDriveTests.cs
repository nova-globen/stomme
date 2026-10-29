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
    /// MailerLite DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for MailerLite's API that refuses a call without the token: a MailerLite connection
    /// made over the API, its token vaulted, tested; a published pipeline creates a subscriber with an accent in a field,
    /// reads the active subscribers over two pages - no more than asked - updates the subscriber given by e-mail (looked up
    /// to its id), adds them to a group, reads the groups and the fields and deletes the subscriber; the token in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MailerLiteDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_updates_groups_and_deletes_subscribers_with_its_token()
        {
            var token = "ml" + Guid.NewGuid().ToString("N");
            using var mailerLite = new MailerLiteStandIn(token);
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
                    Name = "Shop newsletter", ConnectorType = "mailerlite", Endpoint = $"http://127.0.0.1:{mailerLite.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Newsletter" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Create", "create_subscriber", "email = ada@kafe.example.test\nfields = {\"name\": \"Ada\", \"company\": \"Kafé\"}\ngroups = 7"),
                        Call("read", "Read", "subscribers", "status = active\nmax = 120"),
                        Call("update", "Update", "update_subscriber", "subscriber = ada@kafe.example.test\nstatus = unsubscribed"),
                        Call("add", "Add", "add_to_group", "subscriber = 31\ngroup = 8"),
                        Call("groups", "Groups", "groups", ""),
                        Call("fields", "Fields", "fields", ""),
                        Call("delete", "Delete", "delete_subscriber", "subscriber = 31"),
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
                var received = mailerLite.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token or not asking for JSON: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/groups?limit=1",
                    "POST /api/subscribers {\"email\":\"ada@kafe.example.test\",\"fields\":{\"name\":\"Ada\",\"company\":\"Kafé\"},\"groups\":[\"7\"]}",
                    "GET /api/subscribers?filter%5Bstatus%5D=active&limit=100",
                    "GET /api/subscribers?filter%5Bstatus%5D=active&limit=100&cursor=c2",
                    "GET /api/subscribers/ada%40kafe.example.test",
                    "PUT /api/subscribers/31 {\"status\":\"unsubscribed\"}",
                    "POST /api/subscribers/31/groups/8",
                    "GET /api/groups?limit=100&page=1",
                    "GET /api/fields?limit=100&page=1",
                    "DELETE /api/subscribers/31",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("31", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("unsubscribed", JsonNode.Parse(Outputs("update"))!["Result"]![0]!["status"]!.GetValue<string>());
                Assert.Equal("31", JsonNode.Parse(Outputs("add"))!["Result"]![0]!["subscriber"]!.GetValue<string>());
                Assert.Equal("Kafé", JsonNode.Parse(Outputs("groups"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(2, JsonNode.Parse(Outputs("fields"))!["RecordCount"]!.GetValue<int>());
                Assert.True(JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for MailerLite's API that answers only a call carrying the token and asking for JSON.</summary>
        private sealed class MailerLiteStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _bearer;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public MailerLiteStandIn(string token)
            {
                _bearer = "Bearer " + token;
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
                    var authorized = context.Request.Headers["Authorization"] == _bearer && context.Request.Headers["Accept"] == "application/json";
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Subscribers(int first, int count, string next) => "{\"data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (1000 + i) + "\",\"email\":\"s" + i + "@kafe.example.test\",\"status\":\"active\"}")) + "],\"meta\":{\"next_cursor\":\"" + next + "\"}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"message\":\"Unauthenticated.\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/groups") when raw.Contains("limit=1&", StringComparison.Ordinal) || raw.EndsWith("limit=1", StringComparison.Ordinal) => (200, "{\"data\":[{\"id\":\"7\",\"name\":\"Kafé\"}],\"meta\":{\"total\":2}}"),
                            ("GET", "/api/groups") => (200, "{\"data\":[{\"id\":\"7\",\"name\":\"Kafé\"},{\"id\":\"8\",\"name\":\"Bageri\"}],\"meta\":{\"last_page\":1,\"total\":2}}"),
                            ("POST", "/api/subscribers") => (201, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\",\"status\":\"active\",\"fields\":{\"name\":\"Ada\",\"company\":\"Kafé\"}}}"),
                            ("GET", "/api/subscribers") => (200, raw.Contains("cursor=c2", StringComparison.Ordinal) ? Subscribers(101, 80, "c3") : Subscribers(1, 100, "c2")),
                            ("GET", "/api/subscribers/ada%40kafe.example.test") => (200, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\",\"status\":\"active\"}}"),
                            ("PUT", "/api/subscribers/31") => (200, "{\"data\":{\"id\":\"31\",\"email\":\"ada@kafe.example.test\",\"status\":\"unsubscribed\"}}"),
                            ("POST", "/api/subscribers/31/groups/8") => (200, "{\"data\":{\"id\":\"8\",\"name\":\"Bageri\"}}"),
                            ("GET", "/api/fields") => (200, "{\"data\":[{\"id\":\"1\",\"key\":\"name\",\"type\":\"text\"},{\"id\":\"2\",\"key\":\"company\",\"type\":\"text\"}],\"meta\":{\"last_page\":1}}"),
                            ("DELETE", "/api/subscribers/31") => (204, ""),
                            _ => (404, "{\"message\":\"Resource not found.\"}"),
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
