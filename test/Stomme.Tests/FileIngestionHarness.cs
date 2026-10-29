using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// One installed SQLite host for the file-ingestion suites, composed with SMALL caps (so a bomb is a
    /// few hundred KiB, not gigabytes) and a switchable scanner registered before <c>AddStomme</c> - the framework
    /// registers <see cref="NullFileScanner"/> with <c>TryAdd</c>, so this one wins exactly the way a real scanner
    /// would. Every test works in a folder of its own, so the tests share the install but never a folder.
    /// </summary>
    public class FileIngestionHostFixture : IAsyncLifetime
    {
        public const string Host = "localhost";

        public const long MaxUploadBytes = 256 * 1024;
        public const int MaxEntries = 5;
        public const long MaxTotalBytes = 600 * 1024;
        public const int MaxEntryRatio = 20;

        /// <summary>A file name containing this is refused by <see cref="Scanner"/>.</summary>
        public const string ThreatMarker = "eicar";

        private ScratchGateInstall? _scratch;

        public WebApplication App { get; private set; } = null!;

        public SwitchableFileScanner Scanner { get; } = new();

        public string ContentRoot => _scratch!.ContentRoot;

        public async Task InitializeAsync()
        {
            _scratch = new ScratchGateInstall(NewStore(), Host);

            var overridePath = Path.Combine(_scratch.ContentRoot, "appsettings.override.json");
            var overrides = JsonNode.Parse(System.IO.File.ReadAllText(overridePath))!.AsObject();
            overrides["Files"] = new JsonObject
            {
                ["MaxUploadBytes"] = MaxUploadBytes,
                ["Unzip"] = new JsonObject
                {
                    ["MaxEntries"] = MaxEntries,
                    ["MaxTotalBytes"] = MaxTotalBytes,
                    ["MaxEntryRatio"] = MaxEntryRatio
                },
                ["Chunks"] = new JsonObject { ["OrphanMinutes"] = 60 },
                ["UploadByUrl"] = new JsonObject { ["AllowedHosts"] = new JsonArray("localhost") }
            };
            System.IO.File.WriteAllText(overridePath, overrides.ToJsonString());

            GateDrive.CapturedErrors.Clear();
            App = await Program.BuildAppAsync(
                new[] { "--contentRoot", _scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<IFileScanner>(Scanner);
                    builder.Logging.AddProvider(new CapturingLoggerProvider(GateDrive.CapturedErrors));
                });

            var installTask = Task.Run(() => Program.InstallAsync(App));
            if (await Task.WhenAny(installTask, Task.Delay(GateDrive.InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"The {_scratch.Store.Name} installer did not finish in time.");
            }

            var install = await installTask;
            Assert.True(install.Success, "installer failed: " + install.Message);
            await App.StartAsync();
        }

        /// <summary>The store the host installs on - SQLite here; the drive runs the same host on PostgreSQL and RavenDB.</summary>
        private protected virtual IGateStore NewStore() => new SqliteGateStore();

        public async Task DisposeAsync()
        {
            if (App != null)
            {
                await GateDrive.ShutdownAsync(App);
            }

            _scratch?.Dispose();
        }
    }

    /// <summary>Refuses any file whose name contains <see cref="FileIngestionHostFixture.ThreatMarker"/>, and counts calls.</summary>
    public sealed class SwitchableFileScanner : IFileScanner
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<FileScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            // read the content, the way a real scanner would, so a scanner that needs the bytes is not a special case
            var buffer = new byte[81920];
            while (await content.ReadAsync(buffer, cancellationToken) > 0)
            {
            }

            return fileName.Contains(FileIngestionHostFixture.ThreatMarker, StringComparison.OrdinalIgnoreCase)
                ? new FileScanResult(false, "EICAR.Test.File")
                : FileScanResult.Clean;
        }
    }

    /// <summary>The HTTP verbs and the folder/staging observations the file-ingestion suites share.</summary>
    internal sealed class FileIngestionDrive
    {
        private readonly FileIngestionHostFixture _fixture;
        private readonly ITestOutputHelper _output;

        private FileIngestionDrive(FileIngestionHostFixture fixture, ITestOutputHelper output, HttpClient client, CookieContainer cookies, int userId)
        {
            _fixture = fixture;
            _output = output;
            Client = client;
            Cookies = cookies;
            UserId = userId;
        }

        public HttpClient Client { get; }

        public CookieContainer Cookies { get; }

        public int UserId { get; }

        private WebApplication App => _fixture.App;

        public static async Task<FileIngestionDrive> LoginAsHostAsync(FileIngestionHostFixture fixture, ITestOutputHelper output)
        {
            var client = GateDrive.NewClient(fixture.App, FileIngestionHostFixture.Host);
            var cookies = await GateDrive.LoginAsync(fixture.App, client, FileIngestionHostFixture.Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
            var userId = await GateDrive.UserIdAsync(fixture.App, FileIngestionHostFixture.Host, GateDrive.HostUsername);
            return new FileIngestionDrive(fixture, output, client, cookies, userId);
        }

        // ---- folders ----------------------------------------------------------------------------------------

        public Task<Folder> NewFolderAsync(int capacityMegabytes = 0) =>
            GateDrive.InTenantAsync(App, FileIngestionHostFixture.Host, async (sp, alias) =>
            {
                var folders = sp.GetRequiredService<IFolderRepository>();
                var rootFolder = (await folders.GetFoldersAsync(alias.SiteId)).Single(folder => folder.ParentId == null);
                var name = "bl100-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                var created = await folders.AddFolderAsync(new Folder
                {
                    SiteId = alias.SiteId,
                    ParentId = rootFolder.FolderId,
                    Name = name,
                    Type = FolderTypes.Private,
                    Path = name + "/",
                    Order = 1,
                    ImageSizes = "",
                    Capacity = capacityMegabytes,
                    IsSystem = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    }
                });
                Directory.CreateDirectory(await folders.GetFolderPathAsync(created));
                return created;
            });

        public Task<string> FolderPathAsync(Folder folder) =>
            GateDrive.InTenantAsync(App, FileIngestionHostFixture.Host, (sp, alias) => sp.GetRequiredService<IFolderRepository>().GetFolderPathAsync(folder));

        public async Task<IReadOnlyList<string>> DiskNamesAsync(Folder folder)
        {
            var path = await FolderPathAsync(folder);
            return Directory.Exists(path)
                ? Directory.GetFiles(path).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList()!
                : new List<string>();
        }

        public Task<IReadOnlyList<Models.File>> RowsAsync(Folder folder) =>
            GateDrive.InTenantAsync(App, FileIngestionHostFixture.Host, async (sp, alias) =>
                (IReadOnlyList<Models.File>)(await sp.GetRequiredService<IFileRepository>().GetFilesAsync(folder.FolderId, false)).ToList());

        public async Task<IReadOnlyList<string>> RowNamesAsync(Folder folder) =>
            (await RowsAsync(folder)).Select(file => file.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

        /// <summary>Places a file straight on disk and registers its row - a file that did NOT come through the guard.</summary>
        public async Task<Models.File> PlaceFileAsync(Folder folder, string name, byte[] content, int? declaredSize = null)
        {
            var path = await FolderPathAsync(folder);
            Directory.CreateDirectory(path);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(path, name), content);
            return await GateDrive.InTenantAsync(App, FileIngestionHostFixture.Host, (sp, alias) =>
                sp.GetRequiredService<IFileRepository>().AddFileAsync(new Models.File
                {
                    FolderId = folder.FolderId,
                    Name = name,
                    Extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
                    Size = declaredSize ?? content.Length,
                    ImageHeight = 0,
                    ImageWidth = 0
                }));
        }

        public Task<string> StagingRootAsync() =>
            GateDrive.InTenantAsync(App, FileIngestionHostFixture.Host, (sp, alias) =>
                Task.FromResult(FileIngestionPaths.StagingRoot(_fixture.ContentRoot, alias.TenantId, alias.SiteId)));

        /// <summary>Every file and every ingest directory left in the site's staging tree.</summary>
        public async Task<IReadOnlyList<string>> StagingLeftoversAsync()
        {
            var root = await StagingRootAsync();
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Concat(Directory.GetDirectories(root, FileIngestionPaths.IngestDirectoryPrefix + "*", SearchOption.AllDirectories))
                .Select(path => Path.GetRelativePath(root, path))
                .ToList();
        }

        // ---- HTTP -------------------------------------------------------------------------------------------

        public async Task<(HttpStatusCode Status, string Body)> UploadPartAsync(string folder, string fileName, byte[] content, int partCount, int totalParts)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(folder), "folder");
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "formfile", fileName);

            var request = NewRequest(HttpMethod.Post, "/api/File/upload");
            request.Content = form;
            request.Headers.TryAddWithoutValidation("PartCount", partCount.ToString());
            request.Headers.TryAddWithoutValidation("TotalParts", totalParts.ToString());
            return await SendAsync(request, $"POST upload {fileName} part {partCount}/{totalParts} ({content.Length} bytes)");
        }

        public Task<(HttpStatusCode Status, string Body)> UploadAsync(Folder folder, string fileName, byte[] content) =>
            UploadPartAsync(folder.FolderId.ToString(), fileName, content, 1, 1);

        public Task<(HttpStatusCode Status, string Body)> UnzipAsync(int fileId) =>
            SendAsync(NewRequest(HttpMethod.Put, $"/api/File/unzip/{fileId}"), $"PUT unzip {fileId}");

        public Task<(HttpStatusCode Status, string Body)> UploadByUrlAsync(Folder folder, string url, string name) =>
            SendAsync(
                NewRequest(HttpMethod.Post, $"/api/File/upload/url?url={WebUtility.UrlEncode(url)}&folderid={folder.FolderId}&name={WebUtility.UrlEncode(name)}"),
                $"POST upload/url {url}");

        /// <summary>The CLIENT's own <see cref="Stomme.Services.FileService"/>, sending through the test server the way the Files admin does.</summary>
        public async Task<Stomme.Services.FileService> ClientFileServiceAsync()
        {
            var alias = await GateDrive.AliasAsync(App, FileIngestionHostFixture.Host);
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(App, UserId);
            var http = GateDrive.NewClient(App, FileIngestionHostFixture.Host);
            var cookieHeader = Cookies.GetCookieHeader(new Uri("http://" + FileIngestionHostFixture.Host));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader + "; " + antiforgeryCookie);
            return new Stomme.Services.FileService(http, new SiteState { Alias = alias, AntiForgeryToken = token });
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(App, UserId);
            var uri = new Uri(new Uri("http://" + FileIngestionHostFixture.Host), path);
            var request = new HttpRequestMessage(method, uri);
            var cookieHeader = Cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            return request;
        }

        private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, string label)
        {
            var response = await Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"[{label}] -> {(int)response.StatusCode} {GateDrive.Truncate(body)}");
            if ((int)response.StatusCode >= 500)
            {
                foreach (var error in GateDrive.DrainCapturedErrors())
                {
                    _output.WriteLine($"[{label}] host log: {GateDrive.Truncate(error)}");
                }
            }

            return (response.StatusCode, body);
        }

        // ---- content ----------------------------------------------------------------------------------------

        public static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

        public static byte[] RandomBytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        public static byte[] Zip(params (string Name, byte[] Content, CompressionLevel Level)[] entries)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content, level) in entries)
                {
                    var entry = archive.CreateEntry(name, level);
                    using var writer = entry.Open();
                    writer.Write(content, 0, content.Length);
                }
            }

            return stream.ToArray();
        }

        /// <summary>
        /// Rewrites the declared UNCOMPRESSED size of every entry, in the local header AND the central directory, so
        /// <see cref="ZipArchiveEntry.Length"/> lies. Measured on .NET 10: a STORED entry then still yields every
        /// byte it holds, while a deflated one is truncated at the declared size - so the lie that matters is a
        /// stored entry whose header claims to be small.
        /// </summary>
        public static byte[] LieAboutUncompressedSizes(byte[] zip, uint declared)
        {
            var bytes = (byte[])zip.Clone();
            for (var i = 0; i + 30 <= bytes.Length; i++)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                {
                    BitConverter.GetBytes(declared).CopyTo(bytes, i + 22); // local file header
                }
                else if (i + 46 <= bytes.Length && bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                {
                    BitConverter.GetBytes(declared).CopyTo(bytes, i + 24); // central directory header
                }
            }

            return bytes;
        }
    }

    /// <summary>
    /// A raw-socket HTTP/1.1 responder on 127.0.0.1 that counts every connection it ACCEPTS - the instrument that
    /// tells "refused before connecting" apart from "connected, then refused".
    /// </summary>
    internal sealed class LoopbackHttpReceiver : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _body;
        private readonly Task _loop;
        private int _accepted;

        // Who connected, in order - the remote port and the request line (or why there was none) - so a count that
        // is not the one expected names its connections instead of leaving a bare number.
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _connections = new();

        public LoopbackHttpReceiver(byte[] body)
        {
            _body = body;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => _accepted;

        /// <summary>Each accepted connection as "#n from :port at hh:mm:ss.fff: request line", for an assertion's message.</summary>
        public string Connections => _connections.IsEmpty ? "(no connection)" : string.Join(" | ", _connections);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                var number = Interlocked.Increment(ref _accepted);
                var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Port.ToString() ?? "?";
                var at = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var seen = new StringBuilder();
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            while (!seen.ToString().Contains("\r\n\r\n"))
                            {
                                var read = await stream.ReadAsync(buffer);
                                if (read == 0)
                                {
                                    _connections.Enqueue($"#{number} from :{from} at {at}: closed before a request ({seen.Length} bytes)");
                                    return;
                                }

                                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }

                            var text = seen.ToString();
                            var agent = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) ?? "no User-Agent";
                            _connections.Enqueue($"#{number} from :{from} at {at}: {text.Split("\r\n")[0]} ({agent})");

                            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(head);
                            await stream.WriteAsync(_body);
                        }
                        catch
                        {
                            // the client may abort mid-body (a size refusal) - that is the point
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
            _stop.Dispose();
        }
    }
}