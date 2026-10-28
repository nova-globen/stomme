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
    /// BL-229 - Sentry DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a self-hosted Sentry that refuses a call without the token: a Sentry connection made over
    /// the API, its token vaulted, tested; a published pipeline reads a project's unresolved issues over two pages by Sentry's
    /// cursor - no more than asked, never the host the Link header names - resolves one and assigns it, creates a release with
    /// a commit with an accent and deletes it, and reads the teams; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SentryDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_resolves_releases_and_reads_teams_with_its_token()
        {
            var token = "sntryu_" + Guid.NewGuid().ToString("N");
            using var sentry = new SentryStandIn(token);
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
                    Name = "Shop errors", ConnectorType = "sentry", Endpoint = $"http://127.0.0.1:{sentry.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Triage" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("issues", "Issues", "issues", "organization = kafe\nproject = kassa\nquery = is:unresolved\nmax = 120"),
                        Call("resolve", "Resolve", "update_issue", "issue = 4001\nstatus = resolved\nassigned_to = ada@kafe.example.test"),
                        Call("release", "Release", "create_release", "organization = kafe\nversion = kassa@1.0.0\nprojects = kassa\ncommits = [{\"id\": \"a1b2c3\", \"repository\": \"kafe/kassa\", \"message\": \"Fika för alla\"}]"),
                        Call("drop", "Drop", "delete_release", "organization = kafe\nversion = kassa@1.0.0"),
                        Call("teams", "Teams", "teams", "organization = kafe"),
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
                var received = sentry.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/0/organizations/?member=true",
                    "GET /api/0/projects/kafe/kassa/issues/?query=is%3Aunresolved&per_page=100",
                    "GET /api/0/projects/kafe/kassa/issues/?query=is%3Aunresolved&per_page=100&cursor=0%3A100%3A0",
                    "PUT /api/0/issues/4001/ {\"status\":\"resolved\",\"assignedTo\":\"ada@kafe.example.test\"}",
                    "POST /api/0/organizations/kafe/releases/ {\"version\":\"kassa@1.0.0\",\"projects\":[\"kassa\"],\"commits\":[{\"id\":\"a1b2c3\",\"repository\":\"kafe/kassa\",\"message\":\"Fika för alla\"}]}",
                    "DELETE /api/0/organizations/kafe/releases/kassa%401.0.0/",
                    "GET /api/0/organizations/kafe/teams/?per_page=100",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("issues"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("KAFE-1", JsonNode.Parse(Outputs("issues"))!["Result"]![0]!["shortId"]!.GetValue<string>());
                Assert.Equal("resolved", JsonNode.Parse(Outputs("resolve"))!["Result"]![0]!["status"]!.GetValue<string>());
                Assert.Equal("kassa@1.0.0", JsonNode.Parse(Outputs("release"))!["Result"]![0]!["version"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Equal("fika", JsonNode.Parse(Outputs("teams"))!["Result"]![0]!["slug"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a self-hosted Sentry that answers only a call carrying the token.</summary>
        private sealed class SentryStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public SentryStandIn(string token)
            {
                _token = token;
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Issues(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (4000 + i) + "\",\"shortId\":\"KAFE-" + i + "\",\"title\":\"Fika failed " + i + "\"}")) + "]";
                    string? link = null;
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"detail\":\"Invalid token\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/0/organizations/") => (200, "[{\"slug\":\"kafe\",\"name\":\"Kafé\"}]"),
                            ("GET", "/api/0/projects/kafe/kassa/issues/") when raw.Contains("cursor=0%3A100%3A0", StringComparison.Ordinal) => (200, Issues(101, 80)),
                            ("GET", "/api/0/projects/kafe/kassa/issues/") => (200, Issues(1, 100)),
                            ("PUT", "/api/0/issues/4001/") => (200, "{\"id\":\"4001\",\"status\":\"resolved\",\"assignedTo\":{\"email\":\"ada@kafe.example.test\"}}"),
                            ("POST", "/api/0/organizations/kafe/releases/") => (201, "{\"version\":\"kassa@1.0.0\",\"commitCount\":1}"),
                            ("DELETE", "/api/0/organizations/kafe/releases/kassa%401.0.0/") => (204, ""),
                            ("GET", "/api/0/organizations/kafe/teams/") => (200, "[{\"slug\":\"fika\",\"name\":\"Fika\"}]"),
                            _ => (404, "{\"detail\":\"The requested resource does not exist\"}"),
                        };
                    if (path == "/api/0/projects/kafe/kassa/issues/" && authorized && !raw.Contains("cursor=", StringComparison.Ordinal))
                    {
                        // The next page's address on another host: only its cursor may be followed.
                        link = "<http://127.0.0.2:9/api/0/projects/kafe/kassa/issues/?cursor=0:100:0>; rel=\"next\"; results=\"true\"; cursor=\"0:100:0\"";
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
