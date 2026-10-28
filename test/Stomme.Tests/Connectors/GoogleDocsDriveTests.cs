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
    /// BL-229 - Google Docs DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the shared OAuth2 token
    /// provider and the extraction port, against a loopback stand-in for Google's token endpoint, the Drive API and the Docs API: a
    /// Google Docs connection made over the API, its secret and refresh token vaulted, tested (its document read); a published
    /// pipeline makes a document in a folder, inserts text, replaces a placeholder, sends a page break as a request and reads the
    /// document back - the refresh token exchanged, every call its access token; the secrets in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleDocsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Doc = "1AbCdEfGhIjKlMnOp";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_writes_and_reads_a_document()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var google = new DocsStandIn();
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
                    Name = "Plans", ConnectorType = "googledocs", Endpoint = $"http://127.0.0.1:{google.Port}/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token", ["drive_endpoint"] = $"http://127.0.0.1:{google.Port}/drive/v3", ["document"] = Doc },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Plans" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "New plan", "create_document", "title = Plan & notes\nfolder = 0BxFolder"),
                        Call("insert", "Insert", "insert_text", $"document = {Doc}\ntext = Dear {{{{name}}}}, café <b>news</b>"),
                        Call("replace", "Replace", "replace_text", $"document = {Doc}\nfind = {{{{name}}}}\nreplace = Ada"),
                        Call("break", "Break", "update_document", $"document = {Doc}\nrequests = [{{\"insertPageBreak\":{{\"endOfSegmentLocation\":{{\"segmentId\":\"\"}}}}}}]"),
                        Call("read", "Read", "get_document", ""),
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
                var received = google.Received;
                Assert.Contains("refresh_token=" + refresh, received.First(request => request.Path == "/token").Body);
                Assert.All(received.Where(request => request.Path != "/token"), request => Assert.Equal("Bearer access-drive", request.Authorization));
                Assert.Equal(new[]
                {
                    $"GET /v1/documents/{Doc}?fields=title",
                    "POST /drive/v3/files?fields=id,name,webViewLink&supportsAllDrives=true {\"name\":\"Plan & notes\",\"mimeType\":\"application/vnd.google-apps.document\",\"parents\":[\"0BxFolder\"]}",
                    $"POST /v1/documents/{Doc}:batchUpdate {{\"requests\":[{{\"insertText\":{{\"text\":\"Dear {{{{name}}}}, café <b>news</b>\",\"endOfSegmentLocation\":{{\"segmentId\":\"\"}}}}}}]}}",
                    $"POST /v1/documents/{Doc}:batchUpdate {{\"requests\":[{{\"replaceAllText\":{{\"replaceText\":\"Ada\",\"containsText\":{{\"text\":\"{{{{name}}}}\",\"matchCase\":false}}}}}}]}}",
                    $"POST /v1/documents/{Doc}:batchUpdate {{\"requests\":[{{\"insertPageBreak\":{{\"endOfSegmentLocation\":{{\"segmentId\":\"\"}}}}}}]}}",
                    $"GET /v1/documents/{Doc}",
                }, received.Where(request => request.Path != "/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("Dear Ada, caf", read2["outputsJson"]!.GetValue<string>()); // the text read back
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

        /// <summary>A loopback stand-in for Google's token endpoint, the Drive API and the Docs API: a document made, written and read.</summary>
        private sealed class DocsStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public DocsStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string Body, string? Authorization)> Received
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

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add((path, $"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 || path == "/token" ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("POST", "/drive/v3/files") => (200, "{\"id\":\"" + Doc + "\",\"name\":\"Plan & notes\"}"),
                        ("POST", "/v1/documents/" + Doc + ":batchUpdate") => (200, "{\"documentId\":\"" + Doc + "\",\"replies\":[{}],\"writeControl\":{\"requiredRevisionId\":\"rev-2\"}}"),
                        ("GET", "/v1/documents/" + Doc) => (200, "{\"documentId\":\"" + Doc + "\",\"title\":\"Plan & notes\",\"revisionId\":\"rev-3\",\"body\":{\"content\":[{\"paragraph\":{\"elements\":[{\"textRun\":{\"content\":\"Dear Ada, café <b>news</b>\\n\"}}]}}]}}"),
                        _ => (404, """{"error":{"code":404,"message":"Not found.","status":"NOT_FOUND"}}"""),
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
