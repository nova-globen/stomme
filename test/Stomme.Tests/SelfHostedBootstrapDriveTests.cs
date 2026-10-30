#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Bootstrap is served by the host itself, not a public CDN (BL-274): BlazorTheme and a theme generated from the External
    /// template link <see cref="Constants.BootstrapStylesheetUrl"/> and <see cref="Constants.BootstrapScriptUrl"/>, which name
    /// <c>Stomme.Server</c>'s <c>wwwroot/lib/bootstrap/5.3.8</c>. Here: the files there carry exactly the bytes the SRI hashes pin
    /// and the licence beside them, and a folder site on BlazorTheme renders a page linking them and naming no CDN. That an app
    /// SERVES them is driven by the gate's app-template phase (scripts/drive-app-template.ps1), on an app booted for real.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SelfHostedBootstrapDriveTests
    {
        private const string BlazorTheme = "Stomme.Themes.BlazorTheme.Default, Stomme.Client";

        private readonly ITestOutputHelper _output;

        public SelfHostedBootstrapDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task BlazorThemes_Bootstrap_is_served_by_the_host_with_the_pinned_bytes()
        {
            using var scratch = AuthorityScratch.Sqlite();
            using var env = new FolderSiteCookieTests.EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Delato",
                ["Installation__Sites__0__Aliases"] = "localhost/delato",
                ["Installation__Sites__0__DefaultTheme"] = BlazorTheme
            });
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = GateDrive.NewClient(app, "localhost");

                // The files ship in Stomme.Server's web root with exactly the pinned bytes. (This test host serves no static
                // file at all, so serving them is proven where an app is booted for real: the gate's app-template phase.)
                AssertPinnedFile(Constants.BootstrapStylesheetUrl, Constants.BootstrapStylesheetIntegrity);
                AssertPinnedFile(Constants.BootstrapScriptUrl, Constants.BootstrapScriptIntegrity);
                Assert.True(File.Exists(Path.Combine(ServerWebRoot(), "lib", "bootstrap", "5.3.8", "LICENSE")), "Bootstrap ships without its licence");

                // The folder site on BlazorTheme links the host's copy, wherever its link resolves, and names no CDN.
                var page = await client.GetAsync("http://localhost/delato/");
                var html = await page.Content.ReadAsStringAsync();
                Assert.True(page.IsSuccessStatusCode, $"the folder site's page: {(int)page.StatusCode}");
                Assert.DoesNotContain("cdnjs", html, StringComparison.OrdinalIgnoreCase);
                var link = Regex.Match(html, "<link[^>]+href=\"([^\"]*lib/bootstrap/5\\.3\\.8/css/bootstrap\\.min\\.css)(\\?[^\"]*)?\"");
                Assert.True(link.Success, "the BlazorTheme page links no host copy of Bootstrap");
                var href = link.Groups[1].Value;
                _output.WriteLine($"stylesheet link on the folder site: {href}");
                Assert.DoesNotContain("://", href.Replace("http://localhost/", ""));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static void AssertPinnedFile(string url, string integrity)
        {
            Assert.DoesNotContain("://", url);
            var file = Path.Combine(ServerWebRoot(), url.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"{url} is not in Stomme.Server's web root");
            Assert.Equal(integrity, "sha512-" + Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(file))));
        }

        private static string ServerWebRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx).");
            return Path.Combine(directory!.FullName, "src", "Stomme.Server", "wwwroot");
        }
    }
}
