#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    /// Ghost DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Ghost site's Admin API that refuses a call whose token its key's secret did not sign:
    /// a Ghost connection made over the API, its key vaulted, tested; a published pipeline creates a post with an accent as HTML,
    /// reads the published posts over two pages - no more than asked - publishes the post with Ghost's last change read first,
    /// gets it by its slug and deletes it; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GhostDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string PostId = "64f0c0ffee0123456789abcd";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_publishes_gets_and_deletes_a_post_with_its_key()
        {
            var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            using var ghost = new GhostStandIn(keyId, secret);
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
                    Name = "Shop blog", ConnectorType = "ghost", Endpoint = $"http://127.0.0.1:{ghost.Port}/ghost/#/dashboard", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = keyId + ":" + secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Blog" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "New post", "create_post", "title = Café news\nhtml = <p>Hej</p>\nfields = {\"tags\": [{\"name\": \"News\"}]}"),
                        Call("read", "Published", "posts", "filter = status:published\ninclude = tags\nmax = 120"),
                        Call("publish", "Publish", "update_post", $"id = {PostId}\nstatus = published"),
                        Call("get", "By slug", "get_post", "slug = café-news\nformats = html"),
                        Call("delete", "Drop", "delete_post", $"id = {PostId}"),
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
                var received = ghost.Received;
                Assert.All(received, request => Assert.True(request.Signed, "not signed with the key: " + request.Line));
                const string Api = "/ghost/api/admin";
                Assert.Equal(new[]
                {
                    $"GET {Api}/posts/?limit=1&fields=id",
                    $"POST {Api}/posts/?source=html {{\"posts\":[{{\"tags\":[{{\"name\":\"News\"}}],\"title\":\"Café news\",\"html\":\"<p>Hej</p>\"}}]}}",
                    $"GET {Api}/posts/?filter=status%3Apublished&include=tags&limit=100&page=1",
                    $"GET {Api}/posts/?filter=status%3Apublished&include=tags&limit=100&page=2",
                    $"GET {Api}/posts/{PostId}/?fields=id,updated_at",
                    $"PUT {Api}/posts/{PostId}/ {{\"posts\":[{{\"status\":\"published\",\"updated_at\":\"2026-10-13T08:00:00.000Z\"}}]}}",
                    $"GET {Api}/posts/slug/caf%C3%A9-news/?formats=html",
                    $"DELETE {Api}/posts/{PostId}/",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(PostId, JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<string>()); // the id Ghost gave
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("published", JsonNode.Parse(Outputs("publish"))!["Result"]![0]!["status"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Ghost site's Admin API that answers only a call whose token the key signed.</summary>
        private sealed class GhostStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _keyId;
            private readonly byte[] _secret;
            private readonly List<(string Line, bool Signed)> _received = new();

            public GhostStandIn(string keyId, string secret)
            {
                _keyId = keyId;
                _secret = Convert.FromHexString(secret);
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Signed)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            // Ghost's check: a token for this key, signed with its secret, not expired.
            private bool Signed(string? authorization)
            {
                if (authorization is null || !authorization.StartsWith("Ghost ", StringComparison.Ordinal) || authorization["Ghost ".Length..].Split('.') is not { Length: 3 } parts)
                {
                    return false;
                }

                var header = parts[0].Replace('-', '+').Replace('_', '/');
                var claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header.PadRight(header.Length + ((4 - (header.Length % 4)) % 4), '='))));
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                var body = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '='))));
                return claims?["kid"]?.GetValue<string>() == _keyId
                    && body?["aud"]?.GetValue<string>() == "/admin/"
                    && body["exp"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    && parts[2] == Base64Url(HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])));
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
                    var signed = Signed(context.Request.Headers["Authorization"]) && context.Request.Headers["Accept-Version"] == "v5.0";
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", signed));
                    }

                    string Posts(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"" + i.ToString("x24") + "\",\"title\":\"Café " + i + "\",\"status\":\"published\",\"tags\":[{\"name\":\"News\"}]}")) + "]";
                    string Post(string status) => "{\"posts\":[{\"id\":\"" + PostId + "\",\"slug\":\"café-news\",\"title\":\"Café news\",\"status\":\"" + status + "\",\"html\":\"<p>Hej</p>\"}]}";
                    var (status, answer) = !signed ? (401, "{\"errors\":[{\"message\":\"Invalid token: invalid signature\",\"type\":\"UnauthorizedError\"}]}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", _) when raw.Contains("fields=id,updated_at", StringComparison.Ordinal) => (200, "{\"posts\":[{\"id\":\"" + PostId + "\",\"updated_at\":\"2026-10-13T08:00:00.000Z\"}]}"),
                            ("GET", _) when raw.Contains("limit=1&fields=id", StringComparison.Ordinal) => (200, "{\"posts\":[{\"id\":\"" + PostId + "\"}],\"meta\":{\"pagination\":{\"total\":150}}}"),
                            ("GET", _) when raw.Contains("page=2", StringComparison.Ordinal) => (200, "{\"posts\":" + Posts(101, 50) + ",\"meta\":{\"pagination\":{\"page\":2,\"next\":null}}}"),
                            ("GET", _) when raw.Contains("page=1", StringComparison.Ordinal) => (200, "{\"posts\":" + Posts(1, 100) + ",\"meta\":{\"pagination\":{\"page\":1,\"next\":2}}}"),
                            ("GET", _) when raw.Contains("/posts/slug/", StringComparison.Ordinal) => (200, Post("published")),
                            ("POST", _) => (201, Post("draft")),
                            ("PUT", _) => (200, Post("published")),
                            ("DELETE", _) => (204, ""),
                            _ => (404, "{\"errors\":[{\"message\":\"Resource not found\",\"type\":\"NotFoundError\"}]}"),
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
