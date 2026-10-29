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
    /// Box DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Box (its token endpoint, its API, its upload host and its download host on one listener):
    /// a Box connection made over the API - the app's secret vaulted, the enterprise it acts as its setting - refused while loopback
    /// is not allowlisted, then tested; a published pipeline uploads a note over several lines from the step's Query box, lists the
    /// folder, downloads the note - Box sending it to its download host, reached without the token - into the site's pipeline
    /// folder, shares it with a colleague and deletes it; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BoxDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_a_note_in_box_and_fetches_it()
        {
            using var box = new BoxStandIn("kafe-app", "kafe-secret-drive", "1042");
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

                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé Box", ConnectorType = "box", Endpoint = $"http://127.0.0.1:{box.Port}", TimeoutSeconds = 30,
                    Settings = new() { ["subject_id"] = "1042" },
                    Credentials = new() { ["client-id"] = "kafe-app", ["client-secret"] = "kafe-secret-drive", ["token-url"] = $"http://127.0.0.1:{box.Port}/oauth2/token" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows the stand-in")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa Box" }, "create");
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
                        Call("note", "upload", "name = stängt.txt\nparent = 12", "Kassan stängd\nÅter 08:00"),
                        Call("list", "list", "folder_id = 12"),
                        Call("fetch", "download", "file_id = 501\nfile = box/"),
                        Call("share", "share", "item_type = file\nitem_id = 501\nemail = bo@kafe.example\nrole = viewer"),
                        Call("clean", "delete", "file_id = 501"),
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
                Assert.Equal("501", Outputs("note")["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("Kassan stängd\nÅter 08:00", Encoding.UTF8.GetString(box.Uploaded.Single()));
                Assert.Equal("stängt.txt", Outputs("list")["Result"]![0]!["name"]!.GetValue<string>());
                var fetched = Outputs("fetch")["Result"]![0]!;
                Assert.Equal(("box/stängt.txt", 26L), (fetched["file"]!.GetValue<string>(), fetched["size"]!.GetValue<long>())); // written into the site's pipeline folder
                Assert.Equal("viewer", Outputs("share")["Result"]![0]!["role"]!.GetValue<string>());
                Assert.True(Outputs("clean")["Result"]![0]!["deleted"]!.GetValue<bool>());

                var lines = box.Received;
                Assert.Contains(lines, line => line.Line == "GET /download/501" && !line.Authorized); // the download host reached without the token
                Assert.All(lines.Where(line => line.Line.StartsWith("GET /2.0", StringComparison.Ordinal) || line.Line.StartsWith("POST /2.0", StringComparison.Ordinal) || line.Line.StartsWith("DELETE /2.0", StringComparison.Ordinal) || line.Line.StartsWith("POST /api/2.0", StringComparison.Ordinal)), line => Assert.True(line.Authorized, line.Line));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain("kafe-secret-drive", steps, StringComparison.Ordinal);
                Assert.DoesNotContain("kafe-secret-drive", graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A loopback stand-in for Box: a token for the app's client-credentials grant acting as one enterprise alone; the API for
        /// that token alone - the signed-in user, a folder's items, a file's metadata, its content as a redirect to the download
        /// path (which wants no token), a collaboration and a delete; uploads at /api/2.0/files/content kept whole.
        /// </summary>
        private sealed class BoxStandIn : IDisposable
        {
            private const string Token = "box-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();
            private readonly List<byte[]> _uploaded = new();

            public BoxStandIn(string clientId, string secret, string enterprise)
            {
                _form = $"grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}&box_subject_type=enterprise&box_subject_id={enterprise}";
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

            public IReadOnlyList<byte[]> Uploaded
            {
                get
                {
                    lock (_gate)
                    {
                        return _uploaded.ToArray();
                    }
                }
            }

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

                    byte[] body;
                    using (var buffer = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(buffer);
                        body = buffer.ToArray();
                    }

                    var path = (context.Request.RawUrl ?? "/").Split('?')[0];
                    var method = context.Request.HttpMethod;
                    var issuing = path == "/oauth2/token";
                    var authorized = issuing ? Encoding.UTF8.GetString(body) == _form : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {path}", authorized));
                    }

                    var status = 200;
                    string? json = null;
                    byte[]? bytes = null;
                    string? location = null;
                    if (issuing)
                    {
                        (status, json) = authorized ? (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_client\"}");
                    }
                    else if (path == "/download/501")
                    {
                        bytes = Encoding.UTF8.GetBytes("Kassan stängd\nÅter 08:00"); // the download host: a signed address, no token
                    }
                    else if (!authorized)
                    {
                        (status, json) = (401, "{\"type\":\"error\",\"status\":401,\"code\":\"unauthorized\",\"message\":\"Unauthorized\"}");
                    }
                    else
                    {
                        switch (method, path)
                        {
                            case ("GET", "/2.0/users/me"):
                                json = "{\"type\":\"user\",\"id\":\"9\",\"name\":\"Kafé Automation\",\"login\":\"AutomationUser_1@boxdevedition.com\"}";
                                break;
                            case ("POST", "/api/2.0/files/content"):
                                var text = Encoding.UTF8.GetString(body);
                                var marker = "Kassan";
                                var at = text.IndexOf(marker, StringComparison.Ordinal);
                                var end = text.IndexOf("\r\n--", at, StringComparison.Ordinal);
                                lock (_gate)
                                {
                                    _uploaded.Add(Encoding.UTF8.GetBytes(text[at..end]));
                                }

                                json = "{\"total_count\":1,\"entries\":[{\"type\":\"file\",\"id\":\"501\",\"name\":\"stängt.txt\",\"size\":26}]}";
                                break;
                            case ("GET", "/2.0/folders/12/items"):
                                json = "{\"entries\":[{\"type\":\"file\",\"id\":\"501\",\"name\":\"stängt.txt\",\"size\":26}]}";
                                break;
                            case ("GET", "/2.0/files/501"):
                                json = "{\"type\":\"file\",\"id\":\"501\",\"name\":\"stängt.txt\",\"size\":26}";
                                break;
                            case ("GET", "/2.0/files/501/content"):
                                status = 302;
                                location = $"http://127.0.0.1:{Port}/download/501";
                                break;
                            case ("POST", "/2.0/collaborations"):
                                json = "{\"type\":\"collaboration\",\"id\":\"7\",\"role\":\"viewer\",\"status\":\"pending\"}";
                                break;
                            case ("DELETE", "/2.0/files/501"):
                                status = 204;
                                break;
                            default:
                                (status, json) = (404, "{\"type\":\"error\",\"status\":404,\"code\":\"not_found\",\"message\":\"Not Found\"}");
                                break;
                        }
                    }

                    context.Response.StatusCode = status;
                    if (location is not null)
                    {
                        context.Response.RedirectLocation = location;
                    }

                    var payload = bytes ?? (json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json));
                    context.Response.ContentType = bytes is null ? "application/json" : "application/octet-stream";
                    if (payload.Length > 0)
                    {
                        await context.Response.OutputStream.WriteAsync(payload);
                    }

                    context.Response.Close();
                }
            }
        }
    }
}
