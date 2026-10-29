#if INCLUDE_DEMO_MODULES
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
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
    /// THE W2 END-TO-END DRIVE: the independent example module <c>examples/Modules/Blogs</c>, bundled into this
    /// host by a build-time <c>&lt;PackageReference&gt;</c> (NOT installed through InstallationManager, NOT a
    /// project reference), is discovered by the existing <c>/bin</c> convention scan and its render is flipped by
    /// the <c>module.blogs.enabled</c> config knob — driven both directions on a REAL SQLite install + render.
    /// </summary>
    /// <remarks>
    /// Compiled ONLY under <c>-p:IncludeDemoModules=true</c> (the PackageReference + PackageVersion + the
    /// INCLUDE_DEMO_MODULES constant are all scoped to that flag in Stomme.Tests.csproj), so the default suite
    /// never depends on the demo <c>.nupkg</c>. Reproduce:
    /// <code>
    /// pwsh examples/pack-sdk.ps1; pwsh examples/Modules/Blogs/pack-blogs.ps1
    /// Copy-Item examples/Modules/Blogs/Package/NovaGloben.Module.Blogs.*.nupkg examples/local-feed
    /// dotnet restore test/Stomme.Tests -p:IncludeDemoModules=true --source examples/local-feed --source https://api.nuget.org/v3/index.json
    /// dotnet test test/Stomme.Tests -p:IncludeDemoModules=true --no-restore --filter FullyQualifiedName~BlogsBundlingDriveTests
    /// </code>
    /// The PackageReference bakes the three split <c>NovaGloben.Module.Blogs.{Client,Server,Shared}.Stomme.dll</c>
    /// assemblies into the test output <c>/bin</c>; because their names contain "Stomme" the
    /// convention loader loads them and discovery reflects the Blogs
    /// <c>ModuleDefinition</c> — the same convention an InstallationManager-unpacked module rides. What W2
    /// adds is that a <c>PackageReference</c> is a valid delivery vehicle for that same assembly, and that
    /// <c>module.blogs.enabled</c> gates whether the self-placed "blogs" page is seeded.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BlogsBundlingDriveTests
    {
        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        // Distinctive Blogs markup, present only if the bundled module was discovered, seeded onto the "blogs"
        // page, and prerendered (Index.razor : ModuleBase, Prerender => true).
        private const string BlogsMarker = "No posts yet. Be the first to publish.";

        private readonly ITestOutputHelper _output;

        public BlogsBundlingDriveTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Config_true_the_bundled_Blogs_module_is_discovered_seeded_and_renders()
        {
            var (blogsHtml, status) = await InstallAndGetAsync(
                new JsonObject { ["module.blogs.enabled"] = true }, "/blogs");

            _output.WriteLine($"[enabled] GET /blogs -> {(int)status}, blogs-marker={blogsHtml.Contains(BlogsMarker, StringComparison.OrdinalIgnoreCase)}");
            Assert.Equal(HttpStatusCode.OK, status);
            // The bundled module rendered its own component on its self-seeded page.
            Assert.Contains(BlogsMarker, blogsHtml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("db-blogs", blogsHtml, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Config_false_the_assembly_is_present_but_Blogs_is_not_seeded_or_rendered()
        {
            var (blogsHtml, status) = await InstallAndGetAsync(
                new JsonObject { ["module.blogs.enabled"] = false }, "/blogs");

            _output.WriteLine($"[disabled] GET /blogs -> {(int)status}, blogs-marker={blogsHtml.Contains(BlogsMarker, StringComparison.OrdinalIgnoreCase)}");
            // The self-placed "blogs" page was never seeded, so the module's content does not render — whether
            // the router 404s or serves a "page does not exist" body, the Blogs markup is absent.
            Assert.DoesNotContain(BlogsMarker, blogsHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("db-blogs", blogsHtml, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<(string Html, HttpStatusCode Status)> InstallAndGetAsync(JsonObject featureManagement, string path)
        {
            using var scratch = new ScratchInstall(featureManagement);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException($"The installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
                }

                var install = await installTask;
                Assert.True(install.Success, $"Installer did not report success: {install.Message}");

                await app.StartAsync();

                using var client = app.GetTestClient();
                using var renderCts = new CancellationTokenSource(RenderTimeout);
                var requestTask = client.GetAsync(path, renderCts.Token);
                if (await Task.WhenAny(requestTask, Task.Delay(RenderTimeout)) != requestTask)
                {
                    throw new TimeoutException($"GET {path} did not respond within {RenderTimeout.TotalSeconds:0}s.");
                }

                var response = await requestTask;
                var html = await response.Content.ReadAsStringAsync();
                return (html, response.StatusCode);
            }
            finally
            {
                await BestEffortAsync(() => app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token));
                await BestEffortAsync(() => app.DisposeAsync().AsTask());
            }
        }

        private static async Task BestEffortAsync(Func<Task> operation)
        {
            try
            {
                var task = operation();
                await Task.WhenAny(task, Task.Delay(ShutdownTimeout));
            }
            catch
            {
                // best effort
            }
        }

        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall(JsonObject featureManagement)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-blogs-bundle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject
                    {
                        ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server"
                    },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = $"Data Source={Path.Combine(ContentRoot, "stomme-blogs-bundle.db")};"
                    },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "Bl0gsBundle!2026",
                        ["HostEmail"] = "blogs-bundle@example.com",
                        ["HostName"] = "Host"
                    },
                    ["FeatureManagement"] = featureManagement
                };

                File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }
    }
}
#endif
