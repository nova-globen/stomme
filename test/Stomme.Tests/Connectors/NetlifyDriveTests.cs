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
    /// BL-229 - Netlify DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Netlify's API that refuses a call without the token: a Netlify connection made
    /// over the API, its token vaulted, tested; a published pipeline reads the sites over two pages as long as the stand-in
    /// links a next one - no more than asked - reads a site's ready deploys, builds the site, publishes an earlier deploy
    /// again, reads a form's submissions with their fields and deletes one; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class NetlifyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string DeployId = "5f1a2b3c4d5e6f7081920a3b";
        private const string FormId = "60bb1b2c3d4e5f6071829304";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_sites_and_deploys_builds_restores_and_keeps_submissions_with_its_token()
        {
            var token = "nfp" + Guid.NewGuid().ToString("N");
            using var netlify = new NetlifyStandIn(token);
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
                    Name = "Shop sites", ConnectorType = "netlify", Endpoint = $"http://127.0.0.1:{netlify.Port}/api/v1", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Sites" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("sites", "Sites", "sites", "max = 120"),
                        Call("deploys", "Deploys", "deploys", "site = kafe.netlify.app\nstate = ready"),
                        Call("build", "Build", "build", "site = kafe.netlify.app\nclear_cache = true"),
                        Call("restore", "Restore", "restore_deploy", $"site = kafe.netlify.app\ndeploy = {DeployId}"),
                        Call("submissions", "Submissions", "submissions", $"form = {FormId}"),
                        Call("delete", "Delete", "delete_submission", $"submission = {DeployId}"),
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
                var received = netlify.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/v1/user",
                    "GET /api/v1/sites?filter=all&page=1&per_page=100",
                    "GET /api/v1/sites?filter=all&page=2&per_page=100",
                    "GET /api/v1/sites/kafe.netlify.app/deploys?state=ready&page=1&per_page=100",
                    "POST /api/v1/sites/kafe.netlify.app/builds {\"clear_cache\":true}",
                    $"POST /api/v1/sites/kafe.netlify.app/deploys/{DeployId}/restore",
                    $"GET /api/v1/forms/{FormId}/submissions?page=1&per_page=100",
                    $"DELETE /api/v1/submissions/{DeployId}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("sites"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("ready", JsonNode.Parse(Outputs("deploys"))!["Result"]![0]!["state"]!.GetValue<string>());
                Assert.Equal("enqueued", JsonNode.Parse(Outputs("build"))!["Result"]![0]!["state"]!.GetValue<string>());
                Assert.Equal(DeployId, JsonNode.Parse(Outputs("restore"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("Kanelbulle", JsonNode.Parse(Outputs("submissions"))!["Result"]![0]!["bun"]!.GetValue<string>()); // a field it was sent with
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

        /// <summary>A loopback stand-in for Netlify's API that answers only a call carrying the token.</summary>
        private sealed class NetlifyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _bearer;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public NetlifyStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == _bearer;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Sites(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"s" + i + "\",\"name\":\"kafe-" + i + "\"}")) + "]";
                    var path = raw.Split('?')[0];
                    string? link = null;
                    var (status, answer) = !authorized ? (401, "{\"code\":401,\"message\":\"Access Denied\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v1/user") => (200, "{\"id\":\"u1\",\"full_name\":\"Ada Lovelace\",\"email\":\"ada@kafe.example.test\"}"),
                            ("GET", "/api/v1/sites") when raw.Contains("page=1&", StringComparison.Ordinal) => (200, Sites(1, 100)),
                            ("GET", "/api/v1/sites") => (200, Sites(101, 80)),
                            ("GET", "/api/v1/sites/kafe.netlify.app/deploys") => (200, "[{\"id\":\"" + DeployId + "\",\"state\":\"ready\",\"branch\":\"main\"}]"),
                            ("POST", "/api/v1/sites/kafe.netlify.app/builds") => (200, "{\"id\":\"b1\",\"deploy_id\":\"" + DeployId + "\",\"state\":\"enqueued\"}"),
                            ("POST", $"/api/v1/sites/kafe.netlify.app/deploys/{DeployId}/restore") => (200, "{\"id\":\"" + DeployId + "\",\"state\":\"ready\"}"),
                            ("GET", $"/api/v1/forms/{FormId}/submissions") => (200, "[{\"id\":\"" + DeployId + "\",\"number\":1,\"name\":\"Ada\",\"data\":{\"name\":\"Ada\",\"bun\":\"Kanelbulle\"}}]"),
                            ("DELETE", $"/api/v1/submissions/{DeployId}") => (204, ""),
                            _ => (404, "{\"code\":404,\"message\":\"Not Found\"}"),
                        };
                    if (authorized && method == "GET" && path == "/api/v1/sites" && raw.Contains("page=1&", StringComparison.Ordinal))
                    {
                        link = $"<http://127.0.0.1:{Port}/api/v1/sites?filter=all&page=2&per_page=100>; rel=\"next\"";
                    }

                    context.Response.StatusCode = status;
                    if (link is not null)
                    {
                        context.Response.Headers["Link"] = link;
                    }

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
