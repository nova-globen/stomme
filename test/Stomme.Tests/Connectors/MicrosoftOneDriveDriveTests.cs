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
    /// Microsoft OneDrive and a SharePoint site's documents DRIVEN on an installed SQLite host, with the REAL connectors,
    /// the host's egress (which follows no redirect of its own), the extraction port and the run's pipeline folder, against a
    /// loopback stand-in for the tenant's token endpoint and Graph's drives: a token for the app's client credentials alone, Graph
    /// answering that token alone, its download address answering only WITHOUT it. Both connections made over the API and tested;
    /// a published pipeline searches a user's OneDrive over two pages for files, downloads one by its path INTO the run's folder
    /// through Graph's redirect, reads it there with Read text file, makes a path of folders, uploads the file into the last - the
    /// stand-in receiving the same bytes - shares that folder, moves the upload into a folder found by its path and renames it,
    /// copies it (Graph's monitor said, not fetched) and deletes it; then downloads a file of the site's documents and uploads the
    /// CSV there; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftOneDriveDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/kafe-tenant/oauth2/v2.0/token";
        private const string OneDrive = "/v1.0/users/ada%40kafe.example.com/drive";
        private const string Site = "/v1.0/sites/kafe.sharepoint.com:/sites/Kassa:";
        private const string Csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
        private static readonly byte[] Pdf = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x00, 0xFF };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_moves_files_between_onedrive_sharepoint_and_its_folder()
        {
            var secret = "od-" + Guid.NewGuid().ToString("N");
            using var graphStandIn = new GraphStandIn("app-kafe-1", secret);
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
                var credentials = new Dictionary<string, string> { ["client-id"] = "app-kafe-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{graphStandIn.Port}{TokenPath}" };
                async Task<string> Connect(string name, string type, Dictionary<string, string> settings)
                {
                    var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                    {
                        Name = name, ConnectorType = type, Endpoint = $"http://127.0.0.1:{graphStandIn.Port}", TimeoutSeconds = 10, Settings = settings, Credentials = new(credentials),
                    }, "the connection " + name);
                    Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                    var id = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                    var tested = await Send(HttpMethod.Post, $"/api/Connections/{id}/test", new { }, "test the connection " + name);
                    Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                    return id;
                }

                var od = await Connect("Ada's OneDrive", "microsoftonedrive", new() { ["user"] = "ada@kafe.example.com" });
                var sp = await Connect("Kafé intranet", "microsoftsharepoint", new() { ["site"] = "kafe.sharepoint.com:/sites/Kassa" });

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé files" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string connection, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("find", od, "search", "query = kvitto\nwhat = files"),
                        Call("get", od, "download", "item = /Kvitton/kvitto 1042.csv\nfile = downloads/"),
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/kvitto 1042.csv" } },
                        Call("mkdir", od, "create_folder", "name = Arkiv/2026"),
                        Call("put", od, "upload", "file = downloads/kvitto 1042.csv\nname = arkiv-1042.csv\nfolder = 01Y2026"),
                        Call("share", od, "share", "item = 01Y2026\ntype = view\nscope = organization"),
                        Call("move", od, "move", "item = 01NEW\nfolder = /Arkiv\nname = flyttad.csv"),
                        Call("copy", od, "copy", "item = 01NEW\nname = kopia.csv"),
                        Call("drop", od, "delete", "item = 01NEW"),
                        Call("spget", sp, "download_file", "item = /Rapporter/juni.pdf\nfile = sharepoint/"),
                        Call("spput", sp, "upload_file", "file = downloads/kvitto 1042.csv\nfolder = /Rapporter"),
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

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                var received = graphStandIn.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the app's credentials for a token - the rest with it
                var origin = $"http://127.0.0.1:{graphStandIn.Port}";
                Assert.Equal(new[]
                {
                    $"GET {OneDrive}?$select=id,name,driveType,owner",
                    $"GET {Site}?$select=displayName,webUrl",
                    $"GET {OneDrive}/root/search(q='kvitto')",
                    $"GET {OneDrive}/root/search(q='kvitto')?$skiptoken=p2",
                    $"GET {OneDrive}/root:/Kvitton/kvitto%201042.csv:",
                    $"GET {OneDrive}/root:/Kvitton/kvitto%201042.csv:/content",
                    "GET /download/01CSV?tempauth=t1 (no token)", // Graph's redirect, followed once, without the token
                    $"POST {OneDrive}/root/children {{\"name\":\"Arkiv\",\"folder\":{{}},\"@microsoft.graph.conflictBehavior\":\"fail\"}}",
                    $"POST {OneDrive}/items/01ARKIV/children {{\"name\":\"2026\",\"folder\":{{}},\"@microsoft.graph.conflictBehavior\":\"fail\"}}",
                    $"PUT {OneDrive}/items/01Y2026:/arkiv-1042.csv:/content?@microsoft.graph.conflictBehavior=replace ({Encoding.UTF8.GetByteCount(Csv)} bytes, text/csv)",
                    $"POST {OneDrive}/items/01Y2026/createLink {{\"type\":\"view\",\"scope\":\"organization\"}}",
                    $"GET {OneDrive}/root:/Arkiv:?$select=id",
                    $"PATCH {OneDrive}/items/01NEW {{\"parentReference\":{{\"id\":\"01ARKIV\"}},\"name\":\"flyttad.csv\"}}",
                    $"POST {OneDrive}/items/01NEW/copy {{\"name\":\"kopia.csv\"}}",
                    $"DELETE {OneDrive}/items/01NEW",
                    $"GET {Site}/drive/root:/Rapporter/juni.pdf:",
                    $"GET {Site}/drive/root:/Rapporter/juni.pdf:/content",
                    "GET /download/01PDF?tempauth=t2 (no token)",
                    $"PUT {Site}/drive/root:/Rapporter/kvitto%201042.csv:/content?@microsoft.graph.conflictBehavior=replace ({Encoding.UTF8.GetByteCount(Csv)} bytes, text/csv)",
                }, received.Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));
                Assert.Equal(new[] { Csv, Csv }, graphStandIn.Uploaded.Select(bytes => Encoding.UTF8.GetString(bytes))); // the downloaded file's very bytes, to each drive

                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(3, Outputs("find")["RecordCount"]!.GetValue<int>()); // both pages, files alone
                Assert.Equal(("01CSV", "downloads/kvitto 1042.csv"), (Outputs("get")["Result"]![0]!["id"]!.GetValue<string>(), Outputs("get")["Result"]![0]!["file"]!.GetValue<string>())); // its own name
                Assert.Equal(Csv, Outputs("read")["Result"]!.GetValue<string>()); // the file steps read what was downloaded, in the run's folder
                Assert.Equal("01Y2026", Outputs("mkdir")["Result"]![0]!["id"]!.GetValue<string>()); // the last folder of the path
                Assert.Equal("01NEW", Outputs("put")["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("https://kafe-my.sharepoint.com/:f:/g/arkiv", Outputs("share")["Result"]![0]!["link"]!.GetValue<string>());
                Assert.Equal("flyttad.csv", Outputs("move")["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal((202, origin + "/monitor/1"), (Outputs("copy")["Result"]![0]!["status"]!.GetValue<int>(), Outputs("copy")["Result"]![0]!["location"]!.GetValue<string>()));
                Assert.DoesNotContain(received, request => request.Line.Contains("/monitor/", StringComparison.Ordinal)); // said, never fetched
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Equal(("sharepoint/juni.pdf", Pdf.Length), (Outputs("spget")["Result"]![0]!["file"]!.GetValue<string>(), Outputs("spget")["Result"]![0]!["size"]!.GetValue<int>()));
                Assert.Equal("01SPCSV", Outputs("spput")["Result"]![0]!["id"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain("tempauth", steps, StringComparison.Ordinal); // the pre-authenticated address in no record
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A loopback stand-in for a tenant's token endpoint and Graph's drives: a token for the app's client credentials alone,
        /// Graph for that token alone, its pre-authenticated download address only without it; a user's OneDrive and a site's
        /// documents - two pages of a search, a file's metadata and its redirect, folders made, uploads received whole, a link, a
        /// move, a copy accepted for later and a delete.
        /// </summary>
        private sealed class GraphStandIn : IDisposable
        {
            private const string Token = "od-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();
            private readonly List<byte[]> _uploaded = new();

            public GraphStandIn(string clientId, string secret)
            {
                _form = $"grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}&scope={Uri.EscapeDataString("https://graph.microsoft.com/.default")}";
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

                    var raw = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var bearer = context.Request.Headers["Authorization"];
                    var issuing = method == "POST" && path == TokenPath;
                    var downloading = path.StartsWith("/download/", StringComparison.Ordinal);
                    var authorized = issuing ? Encoding.UTF8.GetString(body) == _form
                        : downloading ? bearer is null && raw.Contains("tempauth=", StringComparison.Ordinal)
                        : bearer == "Bearer " + Token;
                    var content = method == "PUT";
                    lock (_gate)
                    {
                        // The token request's form carries the secret, and an upload is checked by its bytes: logged by their line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : content ? $" ({body.Length} bytes, {context.Request.ContentType})" : " " + Encoding.UTF8.GetString(body))}{(downloading ? (bearer is null ? " (no token)" : " (with a token)") : "")}", authorized));
                        if (content && authorized)
                        {
                            _uploaded.Add(body);
                        }
                    }

                    var origin = $"http://127.0.0.1:{Port}";
                    string Page(string? next, params string[] names) =>
                        "{" + (next is null ? "" : "\"@odata.nextLink\":\"" + next + "\",") + "\"value\":[" + string.Join(",", names.Select((name, i) => "{\"id\":\"01F" + i + "\",\"name\":\"" + name.TrimEnd('/') + "\"," + (name.EndsWith('/') ? "\"folder\":{\"childCount\":1}" : "\"file\":{\"mimeType\":\"text/csv\"},\"size\":35") + "}")) + "]}";
                    var search = $"{OneDrive}/root/search(q='kvitto')";
                    var location = (string?)null;
                    byte[]? bytes = null;
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token is empty.\"}}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", OneDrive) => (200, "{\"id\":\"b!ada\",\"name\":\"OneDrive\",\"driveType\":\"business\",\"owner\":{\"user\":{\"displayName\":\"Ada\"}}}"),
                            ("GET", Site) => (200, "{\"displayName\":\"Kassa\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa\"}"),
                            ("GET", _) when path == search => (200, raw.Contains("skiptoken=p2", StringComparison.Ordinal) ? Page(null, "kvitto 1044.csv", "Kvitton/") : Page(origin + search + "?$skiptoken=p2", "kvitto 1042.csv", "kvitto 1043.csv")),
                            ("GET", OneDrive + "/root:/Kvitton/kvitto%201042.csv:") => (200, "{\"id\":\"01CSV\",\"name\":\"kvitto 1042.csv\",\"size\":" + Encoding.UTF8.GetByteCount(Csv) + ",\"file\":{\"mimeType\":\"text/csv\"},\"@microsoft.graph.downloadUrl\":\"" + origin + "/download/01CSV?tempauth=t1\"}"),
                            ("GET", OneDrive + "/root:/Kvitton/kvitto%201042.csv:/content") => (302, Redirect(out location, origin + "/download/01CSV?tempauth=t1")),
                            ("GET", "/download/01CSV") => (200, Bytes(out bytes, Encoding.UTF8.GetBytes(Csv))),
                            ("POST", OneDrive + "/root/children") => (201, "{\"id\":\"01ARKIV\",\"name\":\"Arkiv\",\"folder\":{\"childCount\":0}}"),
                            ("POST", OneDrive + "/items/01ARKIV/children") => (201, "{\"id\":\"01Y2026\",\"name\":\"2026\",\"folder\":{\"childCount\":0}}"),
                            ("PUT", OneDrive + "/items/01Y2026:/arkiv-1042.csv:/content") => (201, "{\"id\":\"01NEW\",\"name\":\"arkiv-1042.csv\",\"size\":" + body.Length + "}"),
                            ("POST", OneDrive + "/items/01Y2026/createLink") => (201, "{\"id\":\"L1\",\"roles\":[\"read\"],\"link\":{\"type\":\"view\",\"scope\":\"organization\",\"webUrl\":\"https://kafe-my.sharepoint.com/:f:/g/arkiv\"}}"),
                            ("GET", OneDrive + "/root:/Arkiv:") => (200, "{\"id\":\"01ARKIV\"}"),
                            ("PATCH", OneDrive + "/items/01NEW") => (200, "{\"id\":\"01NEW\",\"name\":\"flyttad.csv\",\"parentReference\":{\"id\":\"01ARKIV\"}}"),
                            ("POST", OneDrive + "/items/01NEW/copy") => (202, Redirect(out location, origin + "/monitor/1")),
                            ("DELETE", OneDrive + "/items/01NEW") => (204, ""),
                            ("GET", Site + "/drive/root:/Rapporter/juni.pdf:") => (200, "{\"id\":\"01PDF\",\"name\":\"juni.pdf\",\"size\":" + Pdf.Length + ",\"file\":{\"mimeType\":\"application/pdf\"}}"),
                            ("GET", Site + "/drive/root:/Rapporter/juni.pdf:/content") => (302, Redirect(out location, origin + "/download/01PDF?tempauth=t2")),
                            ("GET", "/download/01PDF") => (200, Bytes(out bytes, Pdf)),
                            ("PUT", Site + "/drive/root:/Rapporter/kvitto%201042.csv:/content") => (201, "{\"id\":\"01SPCSV\",\"name\":\"kvitto 1042.csv\"}"),
                            _ => (404, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The resource could not be found.\"}}"),
                        };
                    context.Response.StatusCode = status;
                    if (location is not null)
                    {
                        context.Response.AddHeader("Location", location);
                    }

                    if (bytes is not null)
                    {
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else if (answer.Length > 0)
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer));
                    }

                    context.Response.Close();
                }
            }

            private static string Redirect(out string? location, string to)
            {
                location = to;
                return "";
            }

            private static string Bytes(out byte[]? bytes, byte[] given)
            {
                bytes = given;
                return "";
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
