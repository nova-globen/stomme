#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    /// BL-229 - FTP, SFTP and SSH DRIVEN on an installed SQLite host, with the REAL connectors, the host's egress and the extraction
    /// port, against real servers (<see cref="FileServerTestEndpoint"/>): the connection made over the API, its password vaulted,
    /// refused while loopback is not allowlisted, then tested; a published pipeline uploads text where its folders do not exist
    /// yet, lists them, downloads the file into the run's folder for Read text file to read, uploads that file back under
    /// another name, moves the first where its folders are made and deletes everything it made - the same pipeline over FTP and
    /// over SFTP; over SSH, commands run around a file's download and upload; the password in neither the graph nor the run. Fails, never skips, without a server (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class FileServerDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private static string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(FileServerTestEndpoint.HostKeyLine.Split(' ')[1]))).TrimEnd('=');

        [Fact]
        public Task A_pipeline_keeps_files_on_an_ftp_server() => FilesAsync(new ConnectionRequest
        {
            Name = "Kafé FTP", ConnectorType = "ftp", Endpoint = $"ftp://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.FtpPort}", TimeoutSeconds = 15,
            Settings = new() { ["encryption_mode"] = "none" },
            Credentials = new() { ["username"] = FileServerTestEndpoint.User, ["password"] = FileServerTestEndpoint.FtpPassword },
        }, FileServerTestEndpoint.FtpPassword);

        [Fact]
        public Task A_pipeline_keeps_files_on_an_sftp_server() => FilesAsync(new ConnectionRequest
        {
            Name = "Kafé SFTP", ConnectorType = "sftp", Endpoint = $"sftp://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.SftpPort}", TimeoutSeconds = 15,
            Settings = new() { ["host_key"] = Fingerprint },
            Credentials = new() { ["username"] = FileServerTestEndpoint.User, ["password"] = FileServerTestEndpoint.SftpPassword },
        }, FileServerTestEndpoint.SftpPassword);

        // BL-229 - n8n's SSH node: a command makes a file, the file is downloaded into the run's folder for Read text file to read
        // and uploaded back under another name, a command of several lines from the step's Query box reads both in its folder,
        // and a last one removes what the run made.
        [Fact]
        public Task A_pipeline_runs_commands_and_moves_files_over_ssh()
        {
            var folder = "/home/ops/drive-" + Guid.NewGuid().ToString("N")[..8];
            return DriveAsync(new ConnectionRequest
            {
                Name = "Kafé SSH", ConnectorType = "ssh", Endpoint = $"ssh://{FileServerTestEndpoint.Host}:{FileServerTestEndpoint.SftpPort}", TimeoutSeconds = 15,
                Settings = new() { ["host_key"] = Fingerprint },
                Credentials = new() { ["username"] = FileServerTestEndpoint.ShellUser, ["password"] = FileServerTestEndpoint.SftpPassword },
            }, FileServerTestEndpoint.SftpPassword, call =>
            {
                var check = call("check", "execute", "cwd = " + folder);
                check.Inputs["Query"] = "cat kopia.txt\necho\nls"; // printf wrote no line end
                return new()
                {
                    call("make", "execute", $"command = mkdir -p {folder} && printf 'Kassan är öppen' > {folder}/hej.txt"),
                    call("get", "download_file", $"path = {folder}/hej.txt\nfile = downloads/hej.txt"),
                    new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/hej.txt" } },
                    call("back", "upload", $"path = {folder}/kopia.txt\nfile = downloads/hej.txt"),
                    check,
                    call("clean", "execute", $"command = rm -r {folder}"),
                };
            }, outputs =>
            {
                Assert.Equal((0, ""), (outputs("make")["Result"]![0]!["code"]!.GetValue<int>(), outputs("make")["Result"]![0]!["stdout"]!.GetValue<string>()));
                Assert.Equal(("downloads/hej.txt", 17L), (outputs("get")["Result"]![0]!["file"]!.GetValue<string>(), outputs("get")["Result"]![0]!["size"]!.GetValue<long>()));
                Assert.Equal("Kassan är öppen", outputs("read")["Result"]!.GetValue<string>());
                Assert.Equal("Kassan är öppen\nhej.txt\nkopia.txt", outputs("check")["Result"]![0]!["stdout"]!.GetValue<string>());
                Assert.Equal(0, outputs("clean")["Result"]![0]!["code"]!.GetValue<int>());
            });
        }

        // The file servers' pipeline: text uploaded where its folders do not exist yet, listed, downloaded for Read text file to read,
        // sent back under another name, the first moved where its folders are made, everything deleted.
        private Task FilesAsync(ConnectionRequest request, string password)
        {
            var run = "/files/drive-" + Guid.NewGuid().ToString("N")[..8];
            return DriveAsync(request, password, call => new()
            {
                call("note", "upload", $"path = {run}/kvitton/hej.txt\ntext = Kassan är öppen"),
                call("list", "list", $"path = {run}\nrecursive = true\nfolders = true"),
                call("get", "download_file", $"path = {run}/kvitton/hej.txt\nfile = downloads/hej.txt"),
                new PipelineGraphStep { Id = "read", Type = "Stomme.ReadTextFile", Name = "read", Inputs = { ["Path"] = "downloads/hej.txt" } },
                call("back", "upload", $"path = {run}/arkiv/kopia.txt\nfile = downloads/hej.txt"),
                call("move", "rename", $"from = {run}/kvitton/hej.txt\nto = {run}/arkiv/2026/hej.txt\ncreate_folders = true"),
                call("after", "list", $"path = {run}\nrecursive = true"),
                call("clear", "delete", $"path = {run}\nfolder = true\nrecursive = true"),
            }, outputs =>
            {
                IEnumerable<string> Paths(string id) => outputs(id)["Result"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>()[(entry["path"]!.GetValue<string>().IndexOf(run, StringComparison.Ordinal) + run.Length)..]);
                Assert.Equal(new[] { "/kvitton", "/kvitton/hej.txt" }, Paths("list"));
                Assert.Equal(("downloads/hej.txt", 17L), (outputs("get")["Result"]![0]!["file"]!.GetValue<string>(), outputs("get")["Result"]![0]!["size"]!.GetValue<long>()));
                Assert.Equal("Kassan är öppen", outputs("read")["Result"]!.GetValue<string>()); // the file steps read what was downloaded, in the run's folder
                Assert.Equal(new[] { "/arkiv/2026/hej.txt", "/arkiv/kopia.txt" }, Paths("after")); // files only, by default
                Assert.Equal(5, outputs("clear")["Result"]![0]!["entries_deleted"]!.GetValue<int>()); // arkiv, 2026, both files, kvitton
            });
        }

        // The connection made over the API and tested - refused while loopback is not allowlisted - the steps published as one
        // pipeline and run, each step's outputs handed to the test's assertions; the password in neither the graph nor the run.
        private async Task DriveAsync(ConnectionRequest request, string password, Func<Func<string, string, string, PipelineGraphStep>, List<PipelineGraphStep>> build, Action<Func<string, JsonNode>> check)
        {
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

                var made = await Send(HttpMethod.Post, "/api/Connections", request, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = FileServerTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = request.Name }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph();
                graph.Steps.AddRange(build(Call));
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
                PipelineRunView? view = null;
                for (var attempt = 0; attempt < 150 && view?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    view = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(view?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                check(Outputs);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
