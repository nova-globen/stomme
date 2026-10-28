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
    /// BL-229 - Discourse DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a forum that refuses a call without the key and its user: a Discourse connection made over
    /// the API, its key vaulted, tested; a published pipeline posts a topic with an accent, replies in it, reads the latest posts
    /// over three pages by the last one's id - no more than asked - creates a group, adds users to it and reads the staff; the
    /// key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DiscourseDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_posts_replies_reads_and_keeps_a_group_with_its_key()
        {
            var key = "dk" + Guid.NewGuid().ToString("N");
            using var forum = new ForumStandIn(key);
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
                    Name = "Shop forum", ConnectorType = "discourse", Endpoint = $"http://127.0.0.1:{forum.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "system", ["password"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika forum" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("topic", "Topic", "create_post", "title = Fika at 3\ncontent = Who is in? Kafé!\ncategory = 4"),
                        Call("reply", "Reply", "create_post", "content = I am\ntopic = 7\nreply_to = 1"),
                        Call("posts", "Posts", "posts", "max = 120"),
                        Call("group", "Group", "create_group", "name = fika"),
                        Call("join", "Join", "add_to_group", "group = 41\nusernames = ada,bo"),
                        Call("staff", "Staff", "users", "flag = staff"),
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
                var received = forum.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /session/current.json",
                    "POST /posts.json {\"raw\":\"Who is in? Kafé!\",\"title\":\"Fika at 3\",\"category\":4}",
                    "POST /posts.json {\"raw\":\"I am\",\"topic_id\":7,\"reply_to_post_number\":1}",
                    "GET /posts.json",
                    "GET /posts.json?before=201",
                    "GET /posts.json?before=151",
                    "POST /admin/groups.json {\"group\":{\"name\":\"fika\"}}",
                    "PUT /groups/41/members.json {\"usernames\":\"ada,bo\"}",
                    "GET /admin/users/list/staff.json?page=1",
                    "GET /admin/users/list/staff.json?page=2",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(7, JsonNode.Parse(Outputs("topic"))!["Result"]![0]!["topic_id"]!.GetValue<long>());
                Assert.Equal(2, JsonNode.Parse(Outputs("reply"))!["Result"]![0]!["post_number"]!.GetValue<long>());
                Assert.Equal(120, JsonNode.Parse(Outputs("posts"))!["RecordCount"]!.GetValue<int>()); // three pages, no more than asked
                Assert.Equal(41, JsonNode.Parse(Outputs("group"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.True(JsonNode.Parse(Outputs("join"))!["Result"]![0]!["added"]!.GetValue<bool>());
                Assert.Equal("ada", JsonNode.Parse(Outputs("staff"))!["Result"]![0]!["username"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Discourse forum that answers only a call carrying the key and its user.</summary>
        private sealed class ForumStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ForumStandIn(string key)
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
                    var authorized = context.Request.Headers["Api-Key"] == _key && context.Request.Headers["Api-Username"] == "system";
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Posts(int newest, int count) => "{\"latest_posts\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => "{\"id\":" + (newest - i) + ",\"username\":\"ada\",\"raw\":\"Fika " + (newest - i) + "\"}")) + "]}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (403, "{\"errors\":[\"You are not permitted to view the requested resource. The API username or key is invalid.\"],\"error_type\":\"invalid_access\"}")
                        : (method, path) switch
                        {
                            ("GET", "/session/current.json") => (200, "{\"current_user\":{\"id\":-1,\"username\":\"system\",\"admin\":true}}"),
                            ("POST", "/posts.json") => (200, body.Contains("topic_id", StringComparison.Ordinal) ? "{\"id\":202,\"topic_id\":7,\"post_number\":2}" : "{\"id\":201,\"topic_id\":7,\"post_number\":1}"),
                            ("GET", "/posts.json") => (200, raw.EndsWith("before=151", StringComparison.Ordinal) ? Posts(150, 50) : raw.EndsWith("before=201", StringComparison.Ordinal) ? Posts(200, 50) : Posts(250, 50)),
                            ("POST", "/admin/groups.json") => (200, "{\"basic_group\":{\"id\":41,\"name\":\"fika\"}}"),
                            ("PUT", "/groups/41/members.json") => (200, "{\"success\":\"OK\",\"usernames\":[\"ada\",\"bo\"]}"),
                            ("GET", "/admin/users/list/staff.json") => (200, raw.EndsWith("page=1", StringComparison.Ordinal) ? "[{\"id\":3,\"username\":\"ada\",\"admin\":true}]" : "[]"),
                            _ => (404, "{\"errors\":[\"The requested URL or resource could not be found.\"],\"error_type\":\"not_found\"}"),
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
