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
    /// BL-229 - WordPress DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a site's REST API: a WordPress connection made over the API from an admin page's address,
    /// its application password vaulted, tested (the user named); a published pipeline writes a post with HTML, reads the posts
    /// over two pages as the site says it has, publishes the post and trashes it - every call the username and application
    /// password by Basic; the password is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class WordPressDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_writes_reads_publishes_and_trashes_a_post()
        {
            var password = "wp" + Guid.NewGuid().ToString("N")[..20];
            using var site = new WordPressStandIn();
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
                    Name = "News site", ConnectorType = "wordpress", Endpoint = $"http://127.0.0.1:{site.Port}/news/wp-admin/edit.php", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "editor", ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "News" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("write", "Write it", "create_post", "title = Café & tea\ncontent = <p>Hello <b>world</b></p>\ncategories = 3, 4"),
                        Call("read", "The posts", "posts", "status = draft, publish\nmax = 130"),
                        Call("publish", "Publish", "update_post", "post = 9\nstatus = publish"),
                        Call("trash", "Trash", "delete_post", "post = 9"),
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
                var received = site.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("editor:" + password));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /news/wp-json/wp/v2/users/me?context=edit",
                    "POST /news/wp-json/wp/v2/posts {\"title\":\"Café & tea\",\"content\":\"<p>Hello <b>world</b></p>\",\"categories\":[3,4]}",
                    "GET /news/wp-json/wp/v2/posts?status=draft,publish&per_page=100&page=1",
                    "GET /news/wp-json/wp/v2/posts?status=draft,publish&per_page=30&page=2",
                    "POST /news/wp-json/wp/v2/posts/9 {\"status\":\"publish\"}",
                    "DELETE /news/wp-json/wp/v2/posts/9?force=false",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a WordPress site in /news: the user, a post written, the posts over the two pages it says it has, one published and trashed.</summary>
        private sealed class WordPressStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization)> _received = new();

            public WordPressStandIn()
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

            private static string Post(int id, string status = "draft") => "{\"id\":" + id + ",\"date_gmt\":\"2026-10-01T10:00:00\",\"slug\":\"post-" + id + "\",\"status\":\"" + status + "\",\"type\":\"post\",\"link\":\"http://127.0.0.1/news/post-" + id + "/\",\"title\":{\"rendered\":\"Post " + id + "\"},\"content\":{\"rendered\":\"<p>Hello</p>\"},\"author\":2,\"categories\":[3],\"tags\":[]}";

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

                    var pages = (string?)null;
                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/news/wp-json/wp/v2/users/me") => (200, "{\"id\":2,\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}"),
                        ("POST", "/news/wp-json/wp/v2/posts") => (201, Post(9)),
                        ("GET", "/news/wp-json/wp/v2/posts") when url.Query.Contains("page=2", StringComparison.Ordinal) => (200, "[" + string.Join(",", Enumerable.Range(101, 30).Select(id => Post(id, "publish"))) + "]"),
                        ("GET", "/news/wp-json/wp/v2/posts") => (200, "[" + string.Join(",", Enumerable.Range(1, 100).Select(id => Post(id, "publish"))) + "]"),
                        ("POST", "/news/wp-json/wp/v2/posts/9") => (200, Post(9, "publish")),
                        ("DELETE", "/news/wp-json/wp/v2/posts/9") => (200, Post(9, "trash")),
                        _ => (404, "{\"code\":\"rest_no_route\",\"message\":\"No route was found matching the URL and request method.\",\"data\":{\"status\":404}}"),
                    };
                    if (context.Request.HttpMethod == "GET" && path == "/news/wp-json/wp/v2/posts")
                    {
                        pages = "2";
                    }

                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    if (pages is not null)
                    {
                        context.Response.AddHeader("X-WP-TotalPages", pages);
                    }

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
