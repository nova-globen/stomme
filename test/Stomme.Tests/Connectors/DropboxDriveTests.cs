#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    /// BL-229 - Dropbox DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the extraction port and
    /// the run's pipeline folder, against a loopback stand-in for Dropbox's token endpoint and API v2: a token for the app's
    /// refresh grant alone, the API answering that token alone. The connection made over the API and tested; a published
    /// pipeline lists a folder over two pages, searches it, downloads a file INTO the run's folder - its path in the content
    /// call's header as ASCII JSON - reads it there with Read text file, makes a folder, uploads the file into it beside
    /// what is there and a line of text to another, copies, moves, deletes and reads a file's metadata; the stand-in receiving
    /// the same bytes; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DropboxDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/oauth2/token";
        private const string Csv = "id;kund;summa\n1042;Kafé Ada;99,50\n";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_moves_files_between_dropbox_and_its_folder()
        {
            var secret = "dbx-" + Guid.NewGuid().ToString("N");
            using var dropbox = new DropboxStandIn("kafe-key", secret, "kafe-refresh-1");
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(System.Net.Http.HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(System.Net.Http.HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(System.Net.Http.HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé Dropbox", ConnectorType = "dropbox", Endpoint = $"http://127.0.0.1:{dropbox.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{dropbox.Port}{TokenPath}" },
                    Credentials = new() { ["client-id"] = "kafe-key", ["client-secret"] = secret, ["refresh-token"] = "kafe-refresh-1" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(System.Net.Http.HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(System.Net.Http.HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé Dropbox" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("list", "list", "path = /Kvitton"),
                        Call("find", "search", "query = kvitto\npath = /Kvitton\nextensions = csv"),
                        Call("get", "download", "path = /Kvitton/kvitto 1042.csv\nfile = downloads/"),
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/kvitto 1042.csv" } },
                        Call("mkdir", "create_folder", "path = /Arkiv/2026"),
                        Call("put", "upload", "file = downloads/kvitto 1042.csv\npath = /Arkiv/2026/\nmode = add"),
                        Call("note", "upload", "text = Kassan stängd\npath = /Anteckningar/stängt.txt"),
                        Call("copy", "copy", "from = /Arkiv/2026/kvitto 1042.csv\nto = /Arkiv/kopia.csv"),
                        Call("move", "move", "from = /Arkiv/kopia.csv\nto = /Arkiv/flyttad.csv\nautorename = true"),
                        Call("drop", "delete", "path = /Arkiv/flyttad.csv"),
                        Call("meta", "get", "path = /Arkiv/2026/kvitto 1042.csv"),
                    },
                };
                for (var i = 1; i < graph.Steps.Count; i++)
                {
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = graph.Steps[i - 1].Id, SourcePort = "Done", TargetId = graph.Steps[i].Id });
                }

                var saved = await Send(System.Net.Http.HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(System.Net.Http.HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(System.Net.Http.HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
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
                var received = dropbox.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the app's refresh grant for a token - the rest with it
                var csvBytes = Encoding.UTF8.GetByteCount(Csv);
                Assert.Equal(new[]
                {
                    "POST /2/users/get_current_account",
                    "POST /2/files/list_folder {\"path\":\"/Kvitton\",\"recursive\":false,\"include_deleted\":false,\"include_mounted_folders\":true,\"include_non_downloadable_files\":true,\"include_has_explicit_shared_members\":false,\"limit\":1000}",
                    "POST /2/files/list_folder/continue {\"cursor\":\"page-2\"}",
                    "POST /2/files/search_v2 {\"query\":\"kvitto\",\"options\":{\"max_results\":1000,\"file_status\":\"active\",\"filename_only\":true,\"path\":\"/Kvitton\",\"file_extensions\":[\"csv\"]}}",
                    "POST /2/files/download arg={\"path\":\"/Kvitton/kvitto 1042.csv\"}",
                    "POST /2/files/create_folder_v2 {\"path\":\"/Arkiv/2026\",\"autorename\":false}",
                    $"POST /2/files/upload arg={{\"path\":\"/Arkiv/2026/kvitto 1042.csv\",\"mode\":\"add\",\"autorename\":false,\"mute\":false}} ({csvBytes} bytes, application/octet-stream)",
                    "POST /2/files/upload arg={\"path\":\"/Anteckningar/st\\u00E4ngt.txt\",\"mode\":\"overwrite\",\"autorename\":false,\"mute\":false} (14 bytes, application/octet-stream)",
                    "POST /2/files/copy_v2 {\"from_path\":\"/Arkiv/2026/kvitto 1042.csv\",\"to_path\":\"/Arkiv/kopia.csv\",\"autorename\":false}",
                    "POST /2/files/move_v2 {\"from_path\":\"/Arkiv/kopia.csv\",\"to_path\":\"/Arkiv/flyttad.csv\",\"autorename\":true}",
                    "POST /2/files/delete_v2 {\"path\":\"/Arkiv/flyttad.csv\"}",
                    "POST /2/files/get_metadata {\"path\":\"/Arkiv/2026/kvitto 1042.csv\"}",
                }, received.Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));
                Assert.Equal(new[] { Csv, "Kassan stängd" }, dropbox.Uploaded.Select(bytes => Encoding.UTF8.GetString(bytes))); // the downloaded file's very bytes; the text as given

                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(3, Outputs("list")["RecordCount"]!.GetValue<int>()); // both pages
                Assert.Equal(("/Kvitton/kvitto 1042.csv", "file"), (Outputs("list")["Result"]![0]!["pathDisplay"]!.GetValue<string>(), Outputs("list")["Result"]![0]!["type"]!.GetValue<string>())); // n8n's names
                Assert.Equal("/Kvitton/kvitto 1042.csv", Outputs("find")["Result"]![0]!["pathDisplay"]!.GetValue<string>());
                Assert.Equal(("downloads/kvitto 1042.csv", csvBytes), (Outputs("get")["Result"]![0]!["file"]!.GetValue<string>(), Outputs("get")["Result"]![0]!["size"]!.GetValue<int>())); // its own name
                Assert.Equal(Csv, Outputs("read")["Result"]!.GetValue<string>()); // the file steps read what was downloaded, in the run's folder
                Assert.Equal(("folder", "/Arkiv/2026"), (Outputs("mkdir")["Result"]![0]!["type"]!.GetValue<string>(), Outputs("mkdir")["Result"]![0]!["path_display"]!.GetValue<string>()));
                Assert.Equal("/Arkiv/2026/kvitto 1042.csv", Outputs("put")["Result"]![0]!["path_display"]!.GetValue<string>());
                Assert.Equal("/Arkiv/flyttad.csv", Outputs("move")["Result"]![0]!["path_display"]!.GetValue<string>());
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Equal(csvBytes, Outputs("meta")["Result"]![0]!["size"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain("kafe-refresh-1", steps, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// A loopback stand-in for Dropbox's token endpoint and API v2: a token for the app's refresh grant alone, the API for that
        /// token alone; /Kvitton over two pages, a search, a file's bytes with its metadata in Dropbox-API-Result, uploads kept
        /// whole, a folder made, a copy, a move, a delete and a file's metadata.
        /// </summary>
        private sealed class DropboxStandIn : IDisposable
        {
            private const string Token = "dbx-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();
            private readonly List<byte[]> _uploaded = new();

            public DropboxStandIn(string clientId, string secret, string refresh)
            {
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
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

            private static string Entry(string path, bool folder = false, int size = 35) =>
                "{\".tag\":\"" + (folder ? "folder" : "file") + "\",\"name\":\"" + path.Split('/')[^1] + "\",\"path_lower\":\"" + path.ToLowerInvariant() + "\",\"path_display\":\"" + path + "\",\"id\":\"id:" + path.Length + "\""
                + (folder ? "" : ",\"rev\":\"015f\",\"size\":" + size + ",\"server_modified\":\"2026-10-17T08:00:05Z\"") + "}";

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
                    var arg = context.Request.Headers["Dropbox-API-Arg"];
                    var issuing = path == TokenPath;
                    var authorized = context.Request.HttpMethod == "POST" && (issuing ? Encoding.UTF8.GetString(body) == _form : context.Request.Headers["Authorization"] == "Bearer " + Token);
                    var uploading = path == "/2/files/upload";
                    lock (_gate)
                    {
                        // The token request's form carries the secret, and an upload is checked by its bytes: logged by their line alone.
                        _received.Add(($"{context.Request.HttpMethod} {path}{(arg is null ? "" : " arg=" + arg)}{(issuing || body.Length == 0 ? "" : uploading ? $" ({body.Length} bytes, {context.Request.ContentType})" : " " + Encoding.UTF8.GetString(body))}", authorized));
                        if (uploading && authorized)
                        {
                            _uploaded.Add(body);
                        }
                    }

                    byte[]? bytes = null;
                    string? result = null;
                    var csv = Encoding.UTF8.GetBytes(Csv);
                    var target = arg is null ? null : JsonNode.Parse(arg)!["path"]!.GetValue<string>();
                    var (status, answer) = !authorized ? (401, "{\"error_summary\":\"invalid_access_token/\"}")
                        : path switch
                        {
                            TokenPath => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":14400,\"token_type\":\"bearer\"}"),
                            "/2/users/get_current_account" => (200, "{\"account_id\":\"dbid:ada\",\"name\":{\"display_name\":\"Ada\"},\"root_info\":{\".tag\":\"user\",\"root_namespace_id\":\"11\"}}"),
                            "/2/files/list_folder" => (200, "{\"entries\":[" + Entry("/Kvitton/kvitto 1042.csv", size: csv.Length) + "," + Entry("/Kvitton/Arkiv", folder: true) + "],\"cursor\":\"page-2\",\"has_more\":true}"),
                            "/2/files/list_folder/continue" => (200, "{\"entries\":[" + Entry("/Kvitton/kvitto 1043.csv") + "],\"cursor\":\"end\",\"has_more\":false}"),
                            "/2/files/search_v2" => (200, "{\"matches\":[{\"match_type\":{\".tag\":\"filename\"},\"metadata\":{\".tag\":\"metadata\",\"metadata\":" + Entry("/Kvitton/kvitto 1042.csv", size: csv.Length) + "}}],\"has_more\":false}"),
                            "/2/files/download" when target == "/Kvitton/kvitto 1042.csv" => (200, Content(out bytes, csv, out result, "{\"name\":\"kvitto 1042.csv\",\"path_display\":\"/Kvitton/kvitto 1042.csv\",\"id\":\"id:K1\",\"size\":" + csv.Length + "}")),
                            "/2/files/create_folder_v2" => (200, "{\"metadata\":{\"name\":\"2026\",\"path_lower\":\"/arkiv/2026\",\"path_display\":\"/Arkiv/2026\",\"id\":\"id:Y2026\"}}"),
                            "/2/files/upload" => (200, "{\"name\":\"" + target!.Split('/')[^1] + "\",\"path_display\":\"" + target + "\",\"id\":\"id:U" + body.Length + "\",\"size\":" + body.Length + "}"),
                            "/2/files/copy_v2" => (200, "{\"metadata\":" + Entry("/Arkiv/kopia.csv") + "}"),
                            "/2/files/move_v2" => (200, "{\"metadata\":" + Entry("/Arkiv/flyttad.csv") + "}"),
                            "/2/files/delete_v2" => (200, "{\"metadata\":" + Entry("/Arkiv/flyttad.csv") + "}"),
                            "/2/files/get_metadata" => (200, Entry("/Arkiv/2026/kvitto 1042.csv", size: csv.Length)),
                            _ => (409, "{\"error_summary\":\"path/not_found/..\"}"),
                        };
                    context.Response.StatusCode = status;
                    if (result is not null)
                    {
                        context.Response.AddHeader("Dropbox-API-Result", result);
                    }

                    if (bytes is not null)
                    {
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer));
                    }

                    context.Response.Close();
                }
            }

            private static string Content(out byte[]? bytes, byte[] given, out string? result, string metadata)
            {
                bytes = given;
                result = metadata;
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
