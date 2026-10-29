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
    /// DRIVES the bundled-module config gate (Wave 18 / W2) on a REAL install + REAL render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W2 lets a Stomme app ship an external module by build-time <c>PackageReference</c>: the module's
    /// <c>.Stomme</c> assembly bakes into the publish output and is discovered UNCONDITIONALLY by the existing
    /// <c>InstallationManager</c>/<c>IsStommeAssembly</c> <c>/bin</c> scan. What a config flag gates is whether
    /// that discovered module is SEEDED onto a page — and therefore whether it renders. The knob is W1's
    /// <c>module.&lt;id&gt;.enabled</c>, read from the <c>FeatureManagement</c> config section.
    /// </para>
    /// <para>
    /// This proves the gate WITHOUT needing an external module binary, by driving it against a REAL bundled
    /// framework module that self-seeds onto the home page: <b>HtmlText</b>, whose seeded "Welcome To Stomme"
    /// content is the single most discriminating render marker in the suite (see
    /// <see cref="InstalledSiteRenderTests"/>). Both directions are asserted on the always-on SQLite provider:
    /// <c>module.htmltext.enabled = false</c> removes it from the rendered page; <c>= true</c> (identical to
    /// absent) seeds and renders it. The stock, config-silent case is already pinned by the render guard.
    /// </para>
    /// <para>
    /// The end-to-end <c>PackageReference</c> bundling + discovery + render of the independent example
    /// <c>examples/Modules/Blogs</c> module is driven separately, under the <c>IncludeDemoModules</c> build property
    /// (see <c>BlogsBundlingDriveTests</c>), so the default suite never depends on the demo <c>.nupkg</c> being
    /// packed. SQLite is used here for the same reason the render guard does: no external infrastructure, so
    /// this runs on every commit.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BundledModuleGatingTests
    {
        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        // The seeded HtmlText module's content — present only if the module was discovered, resolved, migrated,
        // read AND placed on the home page. Its absence with the page otherwise intact is the gate firing.
        private const string HtmlTextMarker = "Welcome To Stomme";

        private readonly ITestOutputHelper _output;

        public BundledModuleGatingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Config_false_removes_a_seeded_module_from_the_rendered_page()
        {
            var html = await InstallAndGetHomePageAsync(new JsonObject { ["module.htmltext.enabled"] = false });

            // The site itself renders — this is not the installer, and the seeded Home page + theme are intact.
            Assert.DoesNotContain("Installation Wizard", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<title>Home - Default Site</title>", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blazor.web.js", html, StringComparison.OrdinalIgnoreCase);

            // ...but the config-disabled module was NOT seeded onto the page, so its content is gone.
            Assert.DoesNotContain(HtmlTextMarker, html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Config_true_seeds_and_renders_the_module()
        {
            var html = await InstallAndGetHomePageAsync(new JsonObject { ["module.htmltext.enabled"] = true });

            Assert.Contains("<title>Home - Default Site</title>", html, StringComparison.OrdinalIgnoreCase);
            // Explicit true seeds exactly as config-silent does — the module renders.
            Assert.Contains(HtmlTextMarker, html, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> InstallAndGetHomePageAsync(JsonObject featureManagement)
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
                var requestTask = client.GetAsync("/", renderCts.Token);
                if (await Task.WhenAny(requestTask, Task.Delay(RenderTimeout)) != requestTask)
                {
                    throw new TimeoutException($"GET / did not respond within {RenderTimeout.TotalSeconds:0}s.");
                }

                var response = await requestTask;
                var html = await response.Content.ReadAsStringAsync();
                _output.WriteLine($"GET / -> {(int)response.StatusCode}, {html.Length} chars, htmltext-marker={html.Contains(HtmlTextMarker, StringComparison.OrdinalIgnoreCase)}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return html;
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

        /// <summary>
        /// A throwaway SQLite install whose override config carries a <c>FeatureManagement</c> section — the
        /// same file <c>Program.BuildAppAsync</c> reads LAST into the <c>IConfigurationRoot</c> the config-backed
        /// feature source resolves, so the <c>module.&lt;id&gt;.enabled</c> knob is live at install-time seeding.
        /// Mirrors <see cref="InstalledSiteRenderTests"/>'s scratch isolation (self-writes land in the temp
        /// override, never the repo's appsettings.json).
        /// </summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;
            private readonly string _connectionString;

            public ScratchInstall(JsonObject featureManagement)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-module-gate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                _connectionString = $"Data Source={Path.Combine(ContentRoot, "stomme-module-gate.db")};";

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject
                    {
                        ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server"
                    },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = _connectionString
                    },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "M0duleGate!2026",
                        ["HostEmail"] = "module-gate@example.com",
                        ["HostName"] = "Host"
                    },
                    // The knob under test — Microsoft.FeatureManagement's default section name, read verbatim
                    // (dots kept) by ConfigurationFeatureConfigSource.
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

                // Only the pools this install opened. ClearAllPools is process-wide, so it reached into whatever
                // sibling class happened to be mid-request; this reads the post-install connection strings back
                // out of the content root instead.
                ScratchSqlite.ReleaseInstall(ContentRoot);

                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort — SQLite may still hold the file
                }
            }
        }
    }
}
