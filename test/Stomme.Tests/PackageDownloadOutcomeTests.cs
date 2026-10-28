using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Server;
using Stomme.Services;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-109: the hosted package download. A registry that refused, a package named without an address and a download
    /// that failed each left the admin screens saying "downloaded" - the server answered 200 with an empty body (or threw
    /// a 500 on the null address), and the client's <see cref="PackageService.DownloadPackageAsync"/> logged the status
    /// and returned. Driven here through the CLIENT service against a host whose registry is a loopback stand-in: every
    /// failure now throws a <see cref="ServiceRefusalException"/> (which each screen's catch turns into its error message),
    /// leaves no package file behind, and a good download still lands whole under its name.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PackageDownloadOutcomeTests : IClassFixture<PackageRegistryHostFixture>
    {
        private readonly PackageRegistryHostFixture _fixture;
        private readonly ITestOutputHelper _output;

        public PackageDownloadOutcomeTests(PackageRegistryHostFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Theory]
        [InlineData("Refused.Package")]
        [InlineData("NoAddress.Package")]
        [InlineData("Missing.Package")]
        [InlineData("NotJson.Package")]
        public async Task A_failed_download_throws_and_leaves_no_package(string packageId)
        {
            var service = await ClientPackageServiceAsync();

            var refusal = await Assert.ThrowsAsync<ServiceRefusalException>(() => service.DownloadPackageAsync(packageId, "1.0.0"));

            _output.WriteLine($"{packageId}: {(int)refusal.StatusCode} {refusal.Message}");
            Assert.Equal((HttpStatusCode)424, refusal.StatusCode); // Failed Dependency - a 502 the edge replaces with its own page
            Assert.Empty(PackageFiles(packageId));
        }

        [Fact]
        public async Task A_good_download_lands_whole_under_its_name()
        {
            var service = await ClientPackageServiceAsync();

            await service.DownloadPackageAsync("Good.Package", "1.0.0");

            var files = PackageFiles("Good.Package");
            Assert.Equal(new[] { "Good.Package.1.0.0.nupkg" }, files.Select(Path.GetFileName).ToArray());
            Assert.Equal(PackageRegistryStub.GoodBytes, await System.IO.File.ReadAllBytesAsync(files[0]));
            System.IO.File.Delete(files[0]);
        }

        private IReadOnlyList<string> PackageFiles(string packageId)
        {
            var folder = Path.Combine(_fixture.ContentRoot, Constants.PackagesFolder);
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, packageId + "*").OrderBy(name => name, StringComparer.Ordinal).ToList()
                : new List<string>();
        }

        private async Task<PackageService> ClientPackageServiceAsync()
        {
            var app = _fixture.App;
            var host = PackageRegistryHostFixture.Host;
            var client = GateDrive.NewClient(app, host);
            var cookies = await GateDrive.LoginAsync(app, client, host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
            var userId = await GateDrive.UserIdAsync(app, host, GateDrive.HostUsername);
            var alias = await GateDrive.AliasAsync(app, host);
            var (token, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var http = GateDrive.NewClient(app, host);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(new Uri("http://" + host)) + "; " + antiforgeryCookie);
            return new PackageService(http, new SiteState { Alias = alias, AntiForgeryToken = token });
        }
    }

    /// <summary>An installed SQLite host whose <c>PackageRegistryUrl</c> is a <see cref="PackageRegistryStub"/> on loopback.</summary>
    public class PackageRegistryHostFixture : IAsyncLifetime
    {
        public const string Host = "localhost";

        private ScratchGateInstall? _scratch;
        private PackageRegistryStub? _registry;

        public WebApplication App { get; private set; } = null!;

        public string ContentRoot => _scratch!.ContentRoot;

        public async Task InitializeAsync()
        {
            _registry = new PackageRegistryStub();
            _scratch = new ScratchGateInstall(new SqliteGateStore(), Host);

            var overridePath = Path.Combine(_scratch.ContentRoot, "appsettings.override.json");
            var overrides = JsonNode.Parse(System.IO.File.ReadAllText(overridePath))!.AsObject();
            overrides["PackageRegistryUrl"] = _registry.BaseUrl;
            System.IO.File.WriteAllText(overridePath, overrides.ToJsonString());

            GateDrive.CapturedErrors.Clear();
            App = await Program.BuildAppAsync(
                new[] { "--contentRoot", _scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Logging.AddProvider(new CapturingLoggerProvider(GateDrive.CapturedErrors));
                });

            var installTask = Task.Run(() => Program.InstallAsync(App));
            if (await Task.WhenAny(installTask, Task.Delay(GateDrive.InstallTimeout)) != installTask)
            {
                throw new TimeoutException("The SQLite installer did not finish in time.");
            }

            var install = await installTask;
            Assert.True(install.Success, "installer failed: " + install.Message);
            await App.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (App != null)
            {
                await GateDrive.ShutdownAsync(App);
            }

            _scratch?.Dispose();
            if (_registry != null)
            {
                await _registry.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A marketplace registry on loopback, its answer chosen by the requested package id: <c>Refused.*</c> 403,
    /// <c>NotJson.*</c> an HTML page, <c>NoAddress.*</c> a package with no <c>packageUrl</c>, <c>Missing.*</c> a package
    /// whose file answers 404, anything else a package whose file is <see cref="GoodBytes"/>.
    /// </summary>
    public sealed class PackageRegistryStub : IAsyncDisposable
    {
        public static readonly byte[] GoodBytes = Encoding.ASCII.GetBytes("PK-a-stand-in-for-a-nupkg");

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public PackageRegistryStub()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

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

                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            return;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var target = seen.ToString().Split("\r\n")[0].Split(' ')[1];
                    var (status, type, body) = Route(target);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch
                {
                    // a client that went away
                }
            }
        }

        private (string Status, string Type, byte[] Body) Route(string target)
        {
            if (target.StartsWith("/files/Missing", StringComparison.Ordinal))
            {
                return ("404 Not Found", "text/plain", Encoding.ASCII.GetBytes("no such file"));
            }

            if (target.StartsWith("/files/", StringComparison.Ordinal))
            {
                return ("200 OK", "application/octet-stream", GoodBytes);
            }

            var package = target.Split('?').Skip(1).SelectMany(query => query.Split('&'))
                .Select(pair => pair.Split('=', 2)).Where(pair => pair[0] == "package").Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault() ?? "";
            if (package.StartsWith("Refused", StringComparison.Ordinal))
            {
                return ("403 Forbidden", "application/json", Encoding.ASCII.GetBytes("{\"error\":\"not entitled\"}"));
            }

            if (package.StartsWith("NotJson", StringComparison.Ordinal))
            {
                return ("200 OK", "text/html", Encoding.ASCII.GetBytes("<html>Sign in</html>"));
            }

            var url = package.StartsWith("NoAddress", StringComparison.Ordinal) ? "null" : $"\"{BaseUrl}/files/{package}.nupkg\"";
            var json = $"{{\"packageId\":\"{package}\",\"version\":\"1.0.0\",\"name\":\"{package}\",\"packageUrl\":{url}}}";
            return ("200 OK", "application/json", Encoding.UTF8.GetBytes(json));
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
