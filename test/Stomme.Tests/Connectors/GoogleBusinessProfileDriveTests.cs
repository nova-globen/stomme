#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// BL-229 - Google Business Profile DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for Google's token endpoint and its Business Profile APIs that refuses a call
    /// without the token it issued: a connection made over the API with its account and location, the client's secret and
    /// refresh token vaulted, tested; a published pipeline reading the location's reviews, replying to the first by the name the
    /// step before answered (an expression over its result) and posting an event; the secret and the refresh token in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleBusinessProfileDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for Google's token endpoint and its Business Profile APIs: its token for one refresh grant, every call kept.</summary>
        private sealed class BusinessStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _form;

            public BusinessStandIn(string clientId, string secret, string refresh)
            {
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

            public void Dispose() => _listener.Close();

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

                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var path = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    Received.Enqueue((method, path, body));
                    var (status, answer) = (method, path.Split('?')[0]) switch
                    {
                        ("POST", "/token") => body == _form ? (200, "{\"access_token\":\"business-drive-token\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_grant\"}"),
                        _ when context.Request.Headers["Authorization"] != "Bearer business-drive-token" => (401, "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}"),
                        ("GET", "/v1/accounts") => (200, "{\"accounts\":[{\"name\":\"accounts/111\",\"accountName\":\"Kafé Kassan\"}]}"),
                        ("GET", "/v4/accounts/111/locations/222/reviews") => (200, "{\"reviews\":[{\"reviewId\":\"AbC_1\",\"name\":\"accounts/111/locations/222/reviews/AbC_1\",\"starRating\":\"FOUR\",\"comment\":\"Gott kaffe\"}],\"totalReviewCount\":1}"),
                        ("PUT", "/v4/accounts/111/locations/222/reviews/AbC_1/reply") => (200, "{\"comment\":" + JsonSerializer.Serialize((string?)JsonNode.Parse(body)!["comment"]) + ",\"updateTime\":\"2026-10-18T10:00:00Z\"}"),
                        ("POST", "/v4/accounts/111/locations/222/localPosts") => (200, "{\"name\":\"accounts/111/locations/222/localPosts/333\",\"topicType\":\"EVENT\",\"state\":\"LIVE\"}"),
                        _ => (404, "{\"error\":{\"code\":404,\"message\":\"Not found.\",\"status\":\"NOT_FOUND\"}}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        [Fact]
        public async Task A_pipeline_reads_reviews_replies_and_posts_through_google_business_profile()
        {
            using var google = new BusinessStandIn("kafe-client", "business-secret-drive", "business-refresh-drive");
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
                    Name = "Kafé recensioner", ConnectorType = "googlebusinessprofile", Endpoint = $"http://127.0.0.1:{google.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token", ["account"] = "accounts/111", ["location"] = "locations/222" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "business-secret-drive", ["refresh-token"] = "business-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans recensioner" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var read = Call("reviews", "reviews");
                read.OutputVariables["Result"] = "v-reviews";
                var reply = Call("reply", "review_reply");
                reply.InputExpressions["Parameters"] = "review = {{ reviews[0].name }}\nreply = Tack för besöket!";
                var post = Call("event", "post_create");
                post.Inputs["Parameters"] = "type = EVENT\ntitle = Kassafest\nsummary = Välkommen\nstart = 2026-11-01T18:00:00+01:00\nend = 2026-11-01T22:00:00+01:00\ncall_to_action = CALL";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-reviews", Name = "reviews" } }, Steps = { read, reply, post } };
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = answer.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(answer.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(4, Outputs("reviews")["Result"]![0]!["stars"]!.GetValue<int>());
                Assert.Equal(("Tack för besöket!", "accounts/111/locations/222/reviews/AbC_1"), (Outputs("reply")["Result"]![0]!["comment"]!.GetValue<string>(), Outputs("reply")["Result"]![0]!["review"]!.GetValue<string>()));
                Assert.Equal("LIVE", Outputs("event")["Result"]![0]!["state"]!.GetValue<string>());
                var posted = JsonNode.Parse(google.Received.Single(request => request.Method == "POST" && request.Path == "/v4/accounts/111/locations/222/localPosts").Body)!;
                Assert.Equal(("EVENT", 17), (posted["topicType"]!.GetValue<string>(), posted["event"]!["schedule"]!["startTime"]!["hours"]!.GetValue<int>())); // in UTC
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "business-secret-drive", "business-refresh-drive" })
                {
                    Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
