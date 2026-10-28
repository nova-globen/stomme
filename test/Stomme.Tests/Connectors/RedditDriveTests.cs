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
    /// BL-229 - Reddit DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Reddit's token endpoint and OAuth API: a Reddit connection made over the API, its client
    /// and refresh token vaulted, tested; a published pipeline submits a text post with an accent, reads a subreddit's new
    /// posts over two pages - no more than asked - comments on the post and deletes it - the token exchanged with the client as
    /// HTTP Basic, every call the access token and the user agent; the secret and the refresh token in neither the graph nor the
    /// run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RedditDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Agent = "web:fika-bot:1.0 (by /u/ada)";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_submits_reads_comments_and_deletes()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var reddit = new RedditStandIn();
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
                    Name = "Fika bot", ConnectorType = "reddit", Endpoint = $"http://127.0.0.1:{reddit.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{reddit.Port}/api/v1/access_token", ["user_agent"] = Agent },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("submit", "Submit", "submit", "subreddit = fika\ntitle = Fika & kanelbulle\ntext = Var dricker ni kaffe?"),
                        Call("posts", "New posts", "posts", "subreddit = r/fika\nsort = new\nmax = 150"),
                        Call("comment", "Comment", "comment", "on = t3_p9\ntext = Tack!"),
                        Call("delete", "Delete", "delete", "thing = t3_p9"),
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
                var received = reddit.Received;
                var token = received.First(request => request.Path == "/api/v1/access_token");
                Assert.Equal(("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:" + secret)), Agent), (token.Authorization, token.Agent));
                Assert.Equal("grant_type=refresh_token&refresh_token=" + refresh, token.Body); // the client never in the form
                Assert.All(received.Where(request => request.Path != "/api/v1/access_token"), request => Assert.Equal(("Bearer access-drive", Agent), (request.Authorization, request.Agent)));
                Assert.Equal(new[]
                {
                    "GET /api/v1/me",
                    "POST /api/submit api_type=json&sr=fika&title=Fika+%26+kanelbulle&kind=self&text=Var+dricker+ni+kaffe%3F&sendreplies=true",
                    "GET /r/fika/new?limit=100&raw_json=1",
                    "GET /r/fika/new?limit=50&after=t3_p100&raw_json=1",
                    "POST /api/comment api_type=json&thing_id=t3_p9&text=Tack%21",
                    "POST /api/del id=t3_p9",
                }, received.Where(request => request.Path != "/api/v1/access_token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var posts = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "posts")!;
                Assert.Contains("\"RecordCount\": 150", posts["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
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

        /// <summary>A loopback stand-in for Reddit's token endpoint and OAuth API: the account, a post submitted, a subreddit's new posts over two pages, a comment, a delete.</summary>
        private sealed class RedditStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization, string? Agent)> _received = new();

            public RedditStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string Body, string? Authorization, string? Agent)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Listing(int from, int count, string? after) =>
                "{\"kind\":\"Listing\",\"data\":{\"after\":" + (after is null ? "null" : "\"" + after + "\"") + ",\"children\":[" + string.Join(",", Enumerable.Range(from, count).Select(n =>
                    "{\"kind\":\"t3\",\"data\":{\"id\":\"p" + n + "\",\"name\":\"t3_p" + n + "\",\"subreddit\":\"fika\",\"title\":\"Fika " + n + "\",\"author\":\"lin\",\"created_utc\":1760259600.0}}")) + "]}}";

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
                        _received.Add((path, $"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 || path == "/api/v1/access_token" ? "" : " " + body)}", body, context.Request.Headers["Authorization"], context.Request.Headers["User-Agent"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/api/v1/access_token") => (200, """{"access_token":"access-drive","expires_in":86400,"token_type":"bearer","scope":"*"}"""),
                        ("GET", "/api/v1/me") => (200, """{"name":"ada","total_karma":42}"""),
                        ("POST", "/api/submit") => (200, """{"json":{"errors":[],"data":{"url":"https://www.reddit.com/r/fika/comments/p9/fika/","id":"p9","name":"t3_p9"}}}"""),
                        ("GET", "/r/fika/new") when url.Query.Contains("after=t3_p100", StringComparison.Ordinal) => (200, Listing(101, 100, "t3_p200")),
                        ("GET", "/r/fika/new") => (200, Listing(1, 100, "t3_p100")),
                        ("POST", "/api/comment") => (200, """{"json":{"errors":[],"data":{"things":[{"kind":"t1","data":{"id":"c1","name":"t1_c1","link_id":"t3_p9","parent_id":"t3_p9","body":"Tack!","author":"ada"}}]}}}"""),
                        ("POST", "/api/del") => (200, "{}"),
                        _ => (404, """{"message":"Not Found","error":404}"""),
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
