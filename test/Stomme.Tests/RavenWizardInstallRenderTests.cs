using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// THE INTERACTIVE WIZARD INSTALLS RAVENDB (not just the automated appsettings path).
    /// </summary>
    /// <remarks>
    /// <para>
    /// D54/D55a drove RavenDB through the AUTOMATED path only: appsettings pre-fills
    /// <c>ConnectionStrings:DefaultConnection</c>, so the lazy <c>IDocumentStore</c> reads it on first resolve. The
    /// INTERACTIVE wizard does not — it hands the connection string to <c>IDatabaseManager.InstallAsync(InstallConfig)</c>
    /// and only persists it to config inside <c>MigrateMasterAsync</c>, which runs AFTER <c>CreateDatabaseAsync</c>
    /// first resolves the store. Before D56's fix that first resolve reads EMPTY config and throws (the D54a ordering
    /// finding), so a wizard-driven RavenDB install fails while the automated one succeeds.
    /// </para>
    /// <para>
    /// This test reproduces the wizard EXACTLY: it boots with NO <c>DefaultConnection</c> / <c>DefaultDBType</c> in
    /// config (a fresh, uninstalled machine), then calls the same <c>InstallAsync(config)</c> the
    /// <c>InstallationController</c> calls, with the same <c>InstallConfig</c> shape <c>Installer.razor</c> builds —
    /// then asserts the seeded <c>"Welcome To Stomme"</c> renders. Without the fix, install returns
    /// <c>Success=false</c> and this fails at the install assertion. GATED — fails, not skips, when Raven is
    /// unreachable (default http://127.0.0.1:7000; override with RAVENDB_TEST_URL).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenWizardInstallRenderTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private static Uri Endpoint => new(Url);

        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public RavenWizardInstallRenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Interactive_wizard_installs_and_renders_a_raven_site()
        {
            Assert.True(IsReachable(),
                $"RavenDB is not reachable at {Url}, so nothing here proves the wizard installs a Raven site. " +
                "Start the local RavenDB instance (default http://127.0.0.1:7000) or point RAVENDB_TEST_URL at a reachable server.");

            var database = "stomme_d56_wizard_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            // A fresh, uninstalled boot: NO DefaultConnection / DefaultDBType in config. This is the whole point —
            // the connection string arrives ONLY in the InstallConfig below, exactly as the browser wizard delivers it.
            using var scratch = new ScratchInstallation();

            var html = await InstallViaWizardAndGetHomePageAsync(scratch, database);

            Assert.DoesNotContain("Default Site Template", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Installation Wizard", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<title>Home - Default Site</title>", html, StringComparison.OrdinalIgnoreCase);
            // THE PROOF: seeded HtmlText, written to Raven during a WIZARD-driven install and read back at render.
            Assert.Contains("Welcome To Stomme", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Site Not Configured Correctly", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blazor.web.js", html, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> InstallViaWizardAndGetHomePageAsync(ScratchInstallation scratch, string database)
        {
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                // The InstallConfig the interactive wizard builds (Installer.razor): the connection string comes from
                // the RavenDBConfig control, NOT from appsettings. Resolved and called exactly as InstallationController does.
                var config = new InstallConfig
                {
                    DatabaseType = RavenType,
                    ConnectionString = $"Url={Url};Database={database}",
                    Aliases = "localhost",
                    HostUsername = "wizardhost",
                    HostPassword = "W1zard!Raven$2026",
                    HostEmail = "wizard-raven@example.com",
                    HostName = "Host",
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                };

                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
                var installTask = Task.Run(() => databaseManager.InstallAsync(config));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException($"The RavenDB wizard install did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
                }

                var install = await installTask;
                _output.WriteLine($"[wizard install] Success={install.Success} Message={Truncate(install.Message)}");

                // Without D56's ordering fix, CreateDatabaseAsync resolves the lazy store against empty config and throws,
                // so this is Success=false with the "An Error Occurred Creating The Database" message.
                Assert.True(install.Success,
                    $"The interactive wizard did not report success installing RavenDB: {install.Message}");

                await app.StartAsync();

                using var client = app.GetTestClient();
                using var renderCts = new CancellationTokenSource(RenderTimeout);

                var requestTask = client.GetAsync("/", renderCts.Token);
                if (await Task.WhenAny(requestTask, Task.Delay(RenderTimeout)) != requestTask)
                {
                    throw new TimeoutException(
                        $"GET / did not respond within {RenderTimeout.TotalSeconds:0}s on a wizard-installed RavenDB site.");
                }

                var response = await requestTask;
                var html = await response.Content.ReadAsStringAsync();
                _output.WriteLine($"[RavenDB wizard] GET / -> {(int)response.StatusCode}, {html.Length} chars");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return html;
            }
            finally
            {
                await SafeShutdownAsync(app);
                await BestEffortDropAsync(database);
            }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            await BestEffortAsync(() => app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token));
            await BestEffortAsync(() => app.DisposeAsync().AsTask());
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

        private static async Task BestEffortDropAsync(string database)
        {
            try
            {
                using var store = (DocumentStore)new DocumentStore { Urls = [Url], Database = database }.Initialize();
                await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
            }
            catch
            {
                // a leftover database is harmless
            }
        }

        private static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= 400 ? value : value.Substring(0, 400) + " …");

        private static bool IsReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(Endpoint.Host, Endpoint.Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        // A throwaway content root whose appsettings.override.json is DELIBERATELY empty of DB config — the fresh
        // uninstalled state a wizard install starts from. The installer's own config writes land in the override
        // (via STOMME_APPSETTINGS_PATH), never the repository's appsettings.json.
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-raven-wizard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                // Empty override: no DefaultDBType, no DefaultConnection. The install writes them here as it runs.
                File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    new JsonObject().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

                // A RavenDB install still writes SQLite SIDECARS under its content root - the FeatureManagement
                // flag store and the shared module-aggregate store are SQLite on EVERY install provider - so the
                // pools have to be released here too or the tree survives.
                ScratchSqlite.ReleaseInstall(ContentRoot);

                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
