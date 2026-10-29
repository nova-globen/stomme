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
    /// Google Drive DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the extraction port
    /// and the run's pipeline folder, against a loopback stand-in for Google's token endpoint and the Drive API: a token for the
    /// OAuth client's refresh token alone, the API answering that token alone. A connection made over the API, tested (whom it
    /// signs in as); a published pipeline searches two pages of files, downloads one INTO the run's folder under its own name,
    /// reads it there with Read text file, uploads that file into a folder - the stand-in receiving the same bytes, multipart -
    /// exports a Google Doc as text, makes a folder, shares it with anyone, moves the upload into it, copies it and trashes the
    /// original; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleDriveDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
        private const string Pdf = "1PdFfIlEiDaBcDeFgHiJkLmNo";
        private const string Doc = "1DoCfIlEiDaBcDeFgHiJkLmNo";
        private const string Made = "1NeWfIlEiDaBcDeFgHiJkLmNo";
        private const string Folder = "0NeWfOlDeRiDaBcDeFgHi";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_moves_files_between_google_drive_and_its_folder()
        {
            var secret = "gd-" + Guid.NewGuid().ToString("N");
            using var google = new GoogleStandIn("kafe-client", secret, "kafe-refresh-" + Guid.NewGuid().ToString("N")[..8]);
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
                    Name = "Kafé drive", ConnectorType = "googledrive", Endpoint = $"http://127.0.0.1:{google.Port}/drive/v3", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = secret, ["refresh-token"] = google.RefreshToken },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé drive" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("find", "search", "name = kvitto\nwhat = files"),
                        Call("get", "download", $"file_id = {Pdf}\nfile = downloads/"),
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/kvitto 1042.csv" } },
                        Call("doc", "download", $"file_id = https://docs.google.com/document/d/{Doc}/edit\nfile = docs/\ndocs_as = text/plain"),
                        Call("mkdir", "create_folder", "name = Arkiv 2026"),
                        Call("put", "upload", $"file = downloads/kvitto 1042.csv\nname = arkiv-1042.csv\nfolder = {Folder}"),
                        Call("share", "share", $"file_id = {Folder}\nrole = reader\ntype = anyone"),
                        Call("move", "move", $"file_id = {Made}\nfolder = root"),
                        Call("copy", "copy", $"file_id = {Made}"),
                        Call("drop", "delete", $"file_id = {Pdf}"),
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
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST /token", received[0].Line); // the refresh token for a token - the rest with it
                var calls = received.Where(request => request.Line != "POST /token").Select(request => request.Line).ToList();
                Assert.Equal("GET /drive/v3/about?fields=user%28displayName%2CemailAddress%29", calls[0]); // the test
                Assert.Equal(2, calls.Count(line => line.StartsWith("GET /drive/v3/files?corpora=user", StringComparison.Ordinal))); // two pages
                Assert.Contains($"GET /drive/v3/files/{Pdf}?alt=media&supportsAllDrives=true", calls);
                Assert.Contains($"GET /drive/v3/files/{Doc}/export?mimeType=text%2Fplain", calls);
                Assert.Contains($"PATCH /drive/v3/files/{Made}?supportsAllDrives=true&addParents=root&removeParents={Folder}&fields=" + Uri.EscapeDataString("id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description") + " {}", calls); // its parents read first
                Assert.Contains($"POST /drive/v3/files/{Made}/copy?supportsAllDrives=true&fields=" + Uri.EscapeDataString("id,name,mimeType,parents,size,createdTime,modifiedTime,webViewLink,trashed,driveId,md5Checksum,description") + " {\"name\":\"Copy of arkiv-1042.csv\"}", calls);
                Assert.Contains($"PATCH /drive/v3/files/{Pdf}?supportsAllDrives=true&fields=id {{\"trashed\":true}}", calls); // to the trash
                Assert.Contains($"POST /drive/v3/files/{Folder}/permissions?supportsAllDrives=true&sendNotificationEmail=false {{\"role\":\"reader\",\"type\":\"anyone\",\"allowFileDiscovery\":false}}", calls);

                // The upload: multipart, its metadata naming the folder, its media the downloaded file's very bytes.
                var (metadata, type, bytes) = google.Uploaded!.Value;
                Assert.Equal("{\"name\":\"arkiv-1042.csv\",\"parents\":[\"" + Folder + "\"]}", metadata);
                Assert.Equal(("text/csv", Csv), (type, Encoding.UTF8.GetString(bytes)));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(3, Outputs("find")["RecordCount"]!.GetValue<int>()); // both pages
                Assert.Equal("downloads/kvitto 1042.csv", Outputs("get")["Result"]![0]!["file"]!.GetValue<string>()); // its own name
                Assert.Equal(Csv, Outputs("read")["Result"]!.GetValue<string>()); // the file steps read what was downloaded, in the run's folder
                Assert.Equal(("docs/Kassa rutiner.txt", "application/vnd.google-apps.document"), (Outputs("doc")["Result"]![0]!["file"]!.GetValue<string>(), Outputs("doc")["Result"]![0]!["exported_from"]!.GetValue<string>()));
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain(google.RefreshToken, steps, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A loopback stand-in for Google's token endpoint and the Drive API: a token for the client's refresh token alone, the API
        /// for that token alone; two pages of files, a CSV's bytes, a Google Doc's export, a folder made, an upload parsed from its
        /// multipart body, a permission, a move, a copy and a trash.
        /// </summary>
        private sealed class GoogleStandIn : IDisposable
        {
            private const string Token = "gd-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GoogleStandIn(string clientId, string secret, string refreshToken)
            {
                RefreshToken = refreshToken;
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public string RefreshToken { get; }

            public (string Metadata, string Type, byte[] Bytes)? Uploaded { get; private set; }

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

            // A multipart/related body's two parts: the metadata's text, the media's type and bytes.
            private static (string, string, byte[]) Parts(byte[] body, string contentType)
            {
                var boundary = "--" + contentType.Split("boundary=")[1].Trim('"');
                var text = Encoding.Latin1.GetString(body);
                var parts = text.Split(boundary).Where(part => part.Length > 4 && !part.StartsWith("--", StringComparison.Ordinal)).ToList();
                (string Headers, string Body) Split(string part)
                {
                    var at = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return (part[..at], part[(at + 4)..^2]);
                }

                var (_, metadata) = Split(parts[0]);
                var (headers, media) = Split(parts[1]);
                var type = headers.Split("\r\n").Single(line => line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))["Content-Type:".Length..].Trim();
                return (Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(metadata)), type, Encoding.Latin1.GetBytes(media));
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
                    var issuing = method == "POST" && path == "/token";
                    var authorized = issuing ? Encoding.UTF8.GetString(body) == _form : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    var multipart = context.Request.ContentType?.StartsWith("multipart/related", StringComparison.Ordinal) == true;
                    lock (_gate)
                    {
                        // The token request's form carries the secret, and an upload is checked by its parts: logged by their line alone.
                        _received.Add(($"{method} {raw}{(issuing || multipart || body.Length == 0 ? "" : " " + Encoding.UTF8.GetString(body))}", authorized));
                    }

                    string Files(int first, int count, bool more) =>
                        "{" + (more ? "\"nextPageToken\":\"p2\"," : "") + "\"files\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + (i == 1 ? Pdf : "1OtHeR" + i + "aBcDeFgHiJkLmNo") + "\",\"name\":\"kvitto " + (1041 + i) + ".csv\",\"mimeType\":\"text/csv\"}")) + "]}";
                    string File(string id, string name, string type, string? parents = null) => "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"mimeType\":\"" + type + "\"" + (parents is null ? "" : ",\"parents\":[\"" + parents + "\"]") + "}";
                    byte[]? answer = null;
                    var status = 200;
                    var json = true;
                    if (!authorized)
                    {
                        (status, answer) = (401, Encoding.UTF8.GetBytes(issuing ? "{\"error\":\"invalid_grant\"}" : "{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\",\"status\":\"UNAUTHENTICATED\"}}"));
                    }
                    else if (issuing)
                    {
                        answer = Encoding.UTF8.GetBytes("{\"access_token\":\"" + Token + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
                    }
                    else
                    {
                        var reply = (method, path) switch
                        {
                            ("GET", "/drive/v3/about") => "{\"user\":{\"displayName\":\"Ada\",\"emailAddress\":\"ada@kafe.example.com\"}}",
                            ("GET", "/drive/v3/files") => raw.Contains("pageToken=p2", StringComparison.Ordinal) ? Files(3, 1, false) : Files(1, 2, true),
                            ("GET", $"/drive/v3/files/{Pdf}") when raw.Contains("alt=media", StringComparison.Ordinal) => null,
                            ("GET", $"/drive/v3/files/{Pdf}") => "{\"id\":\"" + Pdf + "\",\"name\":\"kvitto 1042.csv\",\"mimeType\":\"text/csv\",\"size\":\"" + Encoding.UTF8.GetByteCount(Csv) + "\"}",
                            ("GET", $"/drive/v3/files/{Doc}") => File(Doc, "Kassa rutiner", "application/vnd.google-apps.document"),
                            ("GET", $"/drive/v3/files/{Doc}/export") => null,
                            ("POST", "/drive/v3/files") => File(Folder, "Arkiv 2026", "application/vnd.google-apps.folder"),
                            ("POST", "/upload/drive/v3/files") => File(Made, "arkiv-1042.csv", "text/csv", Folder),
                            ("POST", $"/drive/v3/files/{Folder}/permissions") => "{\"id\":\"anyoneWithLink\",\"type\":\"anyone\",\"role\":\"reader\"}",
                            ("GET", $"/drive/v3/files/{Made}") when raw.Contains("fields=parents", StringComparison.Ordinal) => "{\"parents\":[\"" + Folder + "\"]}",
                            ("GET", $"/drive/v3/files/{Made}") => "{\"name\":\"arkiv-1042.csv\"}",
                            ("PATCH", $"/drive/v3/files/{Made}") => File(Made, "arkiv-1042.csv", "text/csv", "root"),
                            ("POST", $"/drive/v3/files/{Made}/copy") => File("1CoPyfIlEiDaBcDeFgHiJkLmNo", "Copy of arkiv-1042.csv", "text/csv", "root"),
                            ("PATCH", $"/drive/v3/files/{Pdf}") => "{\"id\":\"" + Pdf + "\"}",
                            _ => "",
                        };
                        if (method == "POST" && path == "/upload/drive/v3/files")
                        {
                            Uploaded = Parts(body, context.Request.ContentType!);
                        }

                        if (reply is null)
                        {
                            json = false;
                            answer = path.EndsWith("/export", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("Kassan öppnar 07.00") : Encoding.UTF8.GetBytes(Csv);
                        }
                        else if (reply.Length == 0)
                        {
                            (status, answer) = (404, Encoding.UTF8.GetBytes("{\"error\":{\"code\":404,\"message\":\"Not found\",\"errors\":[{\"reason\":\"notFound\"}]}}"));
                        }
                        else
                        {
                            answer = Encoding.UTF8.GetBytes(reply);
                        }
                    }

                    context.Response.StatusCode = status;
                    context.Response.ContentType = json ? "application/json" : "application/octet-stream";
                    await context.Response.OutputStream.WriteAsync(answer!);
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
