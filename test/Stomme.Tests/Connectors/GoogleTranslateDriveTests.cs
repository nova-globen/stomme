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
    /// BL-229 - Google Translate DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Google's token endpoint and Cloud Translation that refuses a call without the token it
    /// issued: a Google Translate connection made over the API, the client's secret and refresh token vaulted, tested; a published
    /// pipeline translating a note over two lines from the Query box, detecting two texts' languages and listing the languages
    /// named in Swedish; the secret and the refresh token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleTranslateDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for Google's token endpoint and Cloud Translation: its token for one refresh grant, every call kept.</summary>
        private sealed class GoogleTranslateStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _form;

            public GoogleTranslateStandIn(string clientId, string secret, string refresh)
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
                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/token") => body == _form ? (200, "{\"access_token\":\"translate-drive-token\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_grant\"}"),
                        _ when context.Request.Headers["Authorization"] != "Bearer translate-drive-token" => (401, "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}"),
                        ("POST", "/language/translate/v2") => (200, "{\"data\":{\"translations\":[{\"translatedText\":" + JsonSerializer.Serialize(((string?)JsonNode.Parse(body)!["q"]![0])!.Replace("Kassan är stängd.", "The till is closed.", StringComparison.Ordinal).Replace("Åter klockan åtta.", "Back at eight.", StringComparison.Ordinal)) + ",\"detectedSourceLanguage\":\"sv\"}]}}"),
                        ("POST", "/language/translate/v2/detect") => (200, "{\"data\":{\"detections\":[[{\"language\":\"sv\",\"isReliable\":false,\"confidence\":0.97}],[{\"language\":\"de\",\"isReliable\":false,\"confidence\":0.88}]]}}"),
                        ("GET", "/language/translate/v2/languages") => (200, "{\"data\":{\"languages\":[{\"language\":\"de\"},{\"language\":\"en\"},{\"language\":\"sv\"}]}}"),
                        ("GET", "/language/translate/v2/languages?target=sv") => (200, "{\"data\":{\"languages\":[{\"language\":\"de\",\"name\":\"tyska\"},{\"language\":\"en\",\"name\":\"engelska\"},{\"language\":\"sv\",\"name\":\"svenska\"}]}}"),
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
        public async Task A_pipeline_translates_detects_and_lists_through_google_translate()
        {
            using var google = new GoogleTranslateStandIn("kafe-client", "translate-secret-drive", "translate-refresh-drive");
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
                    Name = "Kafé översättning", ConnectorType = "googletranslate", Endpoint = $"http://127.0.0.1:{google.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "translate-secret-drive", ["refresh-token"] = "translate-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans skylt" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters, string? query = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                    if (query is not null)
                    {
                        step.Inputs["Query"] = query;
                    }

                    return step;
                }

                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("sign", "translate", "to = en", "Kassan är stängd.\nÅter klockan åtta."),
                        Call("which", "detect", "texts = [\"Kassan är stängd.\", \"Geöffnet\"]"),
                        Call("names", "languages", "names_in = sv"),
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
                Assert.Equal("The till is closed.\nBack at eight.", Outputs("sign")["Result"]![0]!["translatedText"]!.GetValue<string>()); // the Query box's two lines, one text
                Assert.Equal(new[] { "sv", "de" }, Outputs("which")["Result"]!.AsArray().Select(found => found!["language"]!.GetValue<string>()));
                Assert.Equal(new[] { "tyska", "engelska", "svenska" }, Outputs("names")["Result"]!.AsArray().Select(language => language!["name"]!.GetValue<string>()));
                var translated = google.Received.Single(request => request.Path == "/language/translate/v2");
                Assert.Equal("{\"q\":[\"Kassan är stängd.\\nÅter klockan åtta.\"],\"target\":\"en\",\"format\":\"text\"}", translated.Body);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "translate-secret-drive", "translate-refresh-drive" })
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
