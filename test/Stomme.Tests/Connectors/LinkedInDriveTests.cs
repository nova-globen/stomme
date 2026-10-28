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
    /// BL-229 - LinkedIn DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for LinkedIn's API: a LinkedIn connection made over the API, its token vaulted, tested; a
    /// published pipeline posts as the member (its URN read first) with an accent and LinkedIn's reserved characters, posts an
    /// organization's article, changes the post's text, reads the organization's posts over two pages and deletes the post -
    /// every call the token as a bearer, every versioned call LinkedIn's version and Rest.li's protocol; the token in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LinkedInDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Post = "urn:li:share:6844785523593134080";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_posts_as_the_member_and_an_organization_changes_reads_and_deletes()
        {
            var token = "AQV" + Guid.NewGuid().ToString("N");
            using var linkedIn = new LinkedInStandIn();
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
                    Name = "Company page", ConnectorType = "linkedin", Endpoint = $"http://127.0.0.1:{linkedIn.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Posts" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("post", "Post", "create_post", "text = Fika (daily) at the café #fika\nvisibility = CONNECTIONS"),
                        Call("article", "Article", "create_post", "author = organization:2414183\ntext = Read this\narticle_url = https://example.com/fika\narticle_title = Fika & bullar"),
                        Call("change", "Change", "update_post", $"post = {Post}\ntext = Fika *now*"),
                        Call("posts", "Posts", "posts", "author = organization:2414183\nmax = 150"),
                        Call("drop", "Drop", "delete_post", $"post = {Post}"),
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
                var received = linkedIn.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                Assert.All(received.Where(request => request.Line.Contains(" /rest/", StringComparison.Ordinal)), request => Assert.Equal(("202604", "2.0.0"), (request.Version, request.Protocol)));
                const string Distribution = "\"distribution\":{\"feedDistribution\":\"MAIN_FEED\",\"targetEntities\":[],\"thirdPartyDistributionChannels\":[]},\"lifecycleState\":\"PUBLISHED\",\"isReshareDisabledByAuthor\":false";
                Assert.Equal(new[]
                {
                    "GET /v2/userinfo",
                    "GET /v2/userinfo",
                    "POST /rest/posts {\"author\":\"urn:li:person:782bbtaQ\",\"commentary\":\"Fika \\\\(daily\\\\) at the café #fika\",\"visibility\":\"CONNECTIONS\"," + Distribution + "}",
                    "POST /rest/posts {\"author\":\"urn:li:organization:2414183\",\"commentary\":\"Read this\",\"visibility\":\"PUBLIC\"," + Distribution + ",\"content\":{\"article\":{\"source\":\"https://example.com/fika\",\"title\":\"Fika & bullar\"}}}",
                    "POST /rest/posts/urn%3Ali%3Ashare%3A6844785523593134080 [PARTIAL_UPDATE] {\"patch\":{\"$set\":{\"commentary\":\"Fika \\\\*now\\\\*\"}}}",
                    "GET /rest/posts?author=urn%3Ali%3Aorganization%3A2414183&q=author&count=100&start=0&sortBy=LAST_MODIFIED [FINDER]",
                    "GET /rest/posts?author=urn%3Ali%3Aorganization%3A2414183&q=author&count=50&start=100&sortBy=LAST_MODIFIED [FINDER]",
                    "DELETE /rest/posts/urn%3Ali%3Ashare%3A6844785523593134080 [DELETE]",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var posts = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "posts")!;
                Assert.Contains("\"RecordCount\": 150", posts["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var post = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "post")!;
                Assert.Contains(Post, post["outputsJson"]!.GetValue<string>(), StringComparison.Ordinal); // the post's URN, from LinkedIn's header
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for LinkedIn's API: the member, posts made (their URN in x-restli-id), a post changed and deleted, an organization's posts over two pages.</summary>
        private sealed class LinkedInStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization, string? Version, string? Protocol)> _received = new();

            public LinkedInStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization, string? Version, string? Protocol)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Page(int from, int count, bool next) =>
                "{\"elements\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"urn:li:share:" + (7000000000000000000L + n) + "\",\"author\":\"urn:li:organization:2414183\",\"commentary\":\"Post " + n + "\",\"visibility\":\"PUBLIC\",\"lifecycleState\":\"PUBLISHED\",\"createdAt\":1634817394721}"))
                + "],\"paging\":{\"start\":0,\"count\":100,\"links\":" + (next ? "[{\"rel\":\"next\",\"href\":\"/rest/posts?start=100\"}]" : "[]") + "}}";

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
                    var path = Uri.UnescapeDataString(url.AbsolutePath);
                    var method = context.Request.Headers["X-RestLi-Method"];
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(method is null || method == "CREATE" ? "" : " [" + method + "]")}{(body.Length == 0 ? "" : " " + body)}",
                            context.Request.Headers["Authorization"], context.Request.Headers["LinkedIn-Version"], context.Request.Headers["X-Restli-Protocol-Version"]));
                    }

                    var (status, answer, id) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/v2/userinfo") => (200, """{"sub":"782bbtaQ","name":"Ada Lovelace","email":"ada@example.com"}""", (string?)null),
                        ("POST", "/rest/posts") => (201, "", body.Contains("organization", StringComparison.Ordinal) ? "urn:li:share:6844785523593134081" : Post),
                        ("GET", "/rest/posts") when url.Query.Contains("start=100", StringComparison.Ordinal) => (200, Page(101, 100, true), null),
                        ("GET", "/rest/posts") => (200, Page(1, 100, true), null),
                        ("POST", _) when path == "/rest/posts/urn:li:share:6844785523593134080" => (204, "", null),
                        ("DELETE", _) when path == "/rest/posts/urn:li:share:6844785523593134080" => (204, "", null),
                        _ => (404, """{"status":404,"code":"NOT_FOUND","message":"Not found"}""", null),
                    };
                    context.Response.StatusCode = status;
                    if (id is not null)
                    {
                        context.Response.Headers["x-restli-id"] = id;
                    }

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
