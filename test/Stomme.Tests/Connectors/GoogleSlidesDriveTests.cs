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
    /// BL-229 - Google Slides DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Google's token endpoint and the Slides API that refuses a call without the token it issued and
    /// serves a slide's thumbnail at the address it answers with: a connection made over the API, its presentation a setting, tested;
    /// a published pipeline creating a presentation, listing the connection's slides, replacing a placeholder on them and saving a
    /// slide's thumbnail into the run's folder - fetched with no token; the secret and the refresh token in neither the graph nor the
    /// run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleSlidesDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 42 };

        /// <summary>A loopback stand-in for Google's token endpoint and the Slides API: its token for one refresh grant, every call kept.</summary>
        private sealed class GoogleSlidesStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _form;

            public GoogleSlidesStandIn(string clientId, string secret, string refresh)
            {
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Method, string Path, string Body, string? Authorization)> Received { get; } = new();

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
                    var authorization = context.Request.Headers["Authorization"];
                    Received.Enqueue((method, path, body, authorization));
                    if (method == "GET" && path == "/thumbs/p1.png")
                    {
                        context.Response.StatusCode = authorization is null ? 200 : 400; // its own access in its address: a bearer is not Google's to receive here
                        context.Response.ContentType = "image/png";
                        await context.Response.OutputStream.WriteAsync(Png);
                        context.Response.Close();
                        continue;
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/token") => body == _form ? (200, "{\"access_token\":\"slides-drive-token\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_grant\"}"),
                        _ when authorization != "Bearer slides-drive-token" => (401, "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}"),
                        ("GET", "/v1/presentations/kassaDeck01?fields=title") => (200, "{\"title\":\"Kassa\"}"),
                        ("POST", "/v1/presentations") => (200, "{\"presentationId\":\"newDeck02\",\"title\":" + JsonSerializer.Serialize((string?)JsonNode.Parse(body)!["title"]) + ",\"revisionId\":\"r1\"}"),
                        ("GET", "/v1/presentations/kassaDeck01?fields=slides") => (200, "{\"slides\":[{\"objectId\":\"p1\"},{\"objectId\":\"p2\"}]}"),
                        ("POST", "/v1/presentations/kassaDeck01:batchUpdate") => (200, "{\"presentationId\":\"kassaDeck01\",\"replies\":[{\"replaceAllText\":{\"occurrencesChanged\":2}}],\"writeControl\":{\"requiredRevisionId\":\"r9\"}}"),
                        ("GET", "/v1/presentations/kassaDeck01/pages/p1/thumbnail?thumbnailProperties.thumbnailSize=LARGE&thumbnailProperties.mimeType=PNG") => (200, "{\"width\":1600,\"height\":900,\"contentUrl\":\"http://127.0.0.1:" + Port + "/thumbs/p1.png\"}"),
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
        public async Task A_pipeline_creates_lists_replaces_and_saves_a_thumbnail_through_google_slides()
        {
            using var google = new GoogleSlidesStandIn("kafe-client", "slides-secret-drive", "slides-refresh-drive");
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
                    Name = "Kafé presentationer", ConnectorType = "googleslides", Endpoint = $"http://127.0.0.1:{google.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token", ["presentation"] = "kassaDeck01" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "slides-secret-drive", ["refresh-token"] = "slides-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans bildspel" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("new", "create", "title = Kassans veckomöte"),
                        Call("slides", "slides", "max = 10"),
                        Call("fill", "replace_text", "texts = {\"{{kassa}}\": \"Kassa 3\"}\npages = p1, p2"),
                        Call("thumb", "thumbnail", "page = p1\nfile = bilder/"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal("newDeck02", Outputs("new")["Result"]![0]!["presentationId"]!.GetValue<string>());
                Assert.Equal(new[] { "p1", "p2" }, Outputs("slides")["Result"]!.AsArray().Select(slide => slide!["objectId"]!.GetValue<string>()));
                Assert.Equal(2, Outputs("fill")["Result"]![0]!["occurrencesChanged"]!.GetValue<int>());
                Assert.Equal(("bilder/p1.png", Png.Length), (Outputs("thumb")["Result"]![0]!["file"]!.GetValue<string>(), Outputs("thumb")["Result"]![0]!["size"]!.GetValue<int>()));
                var fetched = google.Received.Single(request => request.Path == "/thumbs/p1.png");
                Assert.Null(fetched.Authorization); // the thumbnail's address carries its own access: our token stays with the API

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "slides-secret-drive", "slides-refresh-drive" })
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
