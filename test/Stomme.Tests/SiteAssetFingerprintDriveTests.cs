using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-228: the page an installed host renders carries the CONTENT hash of its web root's assets as their <c>?v=</c> - not
    /// the installation date a container rewrites at every boot, and not the framework version while the files are there.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SiteAssetFingerprintDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_rendered_page_carries_the_content_hash_of_its_assets()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                // The scratch host's web root is a folder of its own with none of the site's assets in it; put two there, as a
                // published app has them, so what is rendered can only be their content's hash.
                var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(environment.WebRootPath, "css"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(environment.WebRootPath, "js"));
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(environment.WebRootPath, "css", "app.css"), "body { margin: 0; }");
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(environment.WebRootPath, "js", "interop.js"), "export {};");
                using var client = GateDrive.NewClient(app, Host);
                var home = await GateDrive.GetAsync(client, null, Host, "/", output, "the home page");
                Assert.Equal(HttpStatusCode.OK, home.Status);

                var rendered = Regex.Match(home.Body, @"css/app\.css\?v=([0-9A-Fa-f]{8})");
                Assert.True(rendered.Success, "the home page links css/app.css with a ?v=");
                var expected = SiteAssetFingerprint.Of(environment.WebRootFileProvider);
                output.WriteLine($"rendered {rendered.Groups[1].Value}, content hash {expected}");

                Assert.NotEqual(Utilities.GenerateSimpleHash(Constants.Version), expected); // the files were found and read
                Assert.Equal(expected, rendered.Groups[1].Value);
                Assert.Contains($"js/interop.js?v={expected}", home.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
