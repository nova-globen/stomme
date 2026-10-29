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
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// THE FIRST RAVENDB SITE THAT RENDERS ITS HOME PAGE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the point of the drive. A RavenDB install COMPLETED, but did not render — HtmlText was pure EF, so
    /// the built-in content module that fills the home page could not construct its context on a document store.
    /// With HtmlText on the seam, this drives the WHOLE render pipeline on RavenDB for the FIRST time:
    /// <c>TenantResolver</c> → <c>RendererSynchronizationContext</c> → the renderer → the HtmlText module's
    /// component → <c>ServerHtmlTextService</c> → <c>HtmlTextRepository</c> → <see cref="Raven"/> session, plus
    /// <c>PermissionRepository</c> on the Raven framework store to authorize the anonymous read.
    /// </para>
    /// <para>
    /// WHY THIS IS THE BAR, NOT A BOOT CHECK. HTTP 200 + bytes would pass against a dead site (the installer is a
    /// healthy 200). So this asserts the seeded HtmlText content — "Welcome To Stomme" — is IN the rendered HTML,
    /// which can only appear if the module was discovered, its content row WRITTEN to Raven during install and READ
    /// back through the Raven session at render, and its component rendered. It also races a timeout: the
    /// renderer-deadlock class does not throw, it stops responding, and RavenDB's async socket I/O is exactly the
    /// kind of real-async surface that can reproduce it where SQLite cannot. A hang here is a finding.
    /// </para>
    /// <para>
    /// GATED — FAILS rather than skips when RavenDB is unreachable, the doctrine the conformance suite and the
    /// install tests share. Point it at a reachable server with <c>RAVENDB_TEST_URL</c> (default the local install at
    /// http://127.0.0.1:7000). Driven via automated appsettings config (Raven DBType + Url connection string)
    /// exactly as the earlier install drives did — RavenDB IS now an <c>AvailableDatabases</c> wizard entry; this test
    /// drives the automated-config path, while the wizard path is covered by <c>RavenWizardInstallRenderTests</c>.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenInstalledSiteRenderTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
        // RavenDB endpoint, shared with the conformance suite via RAVENDB_TEST_URL. Default is the repo's
        // local RavenDB install (http://127.0.0.1:7000); the Docker container the header once named is gone
        // (both stores are now local Windows services: RavenDB on 7000, PostgreSQL postgresql-x64-18 on
        // 127.0.0.1:5432). Override the env var for any other server.
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private static Uri Endpoint => new(Url);
        private static string Host => Endpoint.Host;
        private static int Port => Endpoint.Port;

        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public RavenInstalledSiteRenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Installer_seeds_a_renderable_raven_site()
        {
            Assert.True(IsReachable(),
                $"RavenDB is not reachable at {Url}, so nothing here proves a Raven site renders. " +
                "Start the local RavenDB instance (default http://127.0.0.1:7000) or point RAVENDB_TEST_URL at a reachable server.");

            var database = "stomme_d55a_render_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var connectionString = $"Url={Url};Database={database}";

            using var scratch = new ScratchInstallation(RavenType, connectionString);

            var html = await InstallAndGetHomePageAsync(scratch, database);

            // ---- NEGATIVE MARKER: not the installer wizard (a healthy 200 that fooled earlier rounds). ----------
            Assert.DoesNotContain("Default Site Template", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Installation Wizard", html, StringComparison.OrdinalIgnoreCase);

            // ---- THE SEEDED PAGE, resolved from RavenDB and rendered through the theme. --------------------------
            Assert.Contains("<title>Home - Default Site</title>", html, StringComparison.OrdinalIgnoreCase);

            // THE PROOF: the seeded HtmlText content. It can only appear if the module's content row was written to
            // Raven during install and read back through the Raven session at render — the first render-pipeline
            // read on RavenDB.
            Assert.Contains("Welcome To Stomme", html, StringComparison.OrdinalIgnoreCase);

            // The theme rendered rather than a bare error/blank body.
            Assert.DoesNotContain("Site Not Configured Correctly", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Site Is Disabled", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blazor.web.js", html, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> InstallAndGetHomePageAsync(ScratchInstallation scratch, string database)
        {
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                // Drive the installer to completion via the automated path (connection string, alias, host account
                // all in configuration) — exactly where Program.Main drives it. Raced against a timeout because
                // InstallAsync takes no CancellationToken.
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException($"The RavenDB installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
                }

                var install = await installTask;
                _output.WriteLine($"[install] Success={install.Success} Message={Truncate(install.Message)}");

                // With HtmlText on the seam the module no longer throws on Raven, so the install should now
                // report success end-to-end — the debt carried until the module could run on Raven.
                Assert.True(install.Success,
                    $"RavenDB installer did not report success: {install.Message}");

                await app.StartAsync();

                using var client = app.GetTestClient();
                using var renderCts = new CancellationTokenSource(RenderTimeout);

                var requestTask = client.GetAsync("/", renderCts.Token);
                if (await Task.WhenAny(requestTask, Task.Delay(RenderTimeout)) != requestTask)
                {
                    // The deadlock signature: the request does not fail, it stops responding. RavenDB's real async socket
                    // I/O is the kind of surface that can post a continuation back to a blocked renderer pump where
                    // SQLite cannot. If this fires, THAT is the finding.
                    throw new TimeoutException(
                        $"GET / did not respond within {RenderTimeout.TotalSeconds:0}s on RavenDB. The site did not " +
                        "render — a renderer deadlock (docs/decisions.md D29) looks like exactly this, and RavenDB " +
                        "had never met the renderer before D55a.");
                }

                var response = await requestTask;
                var html = await response.Content.ReadAsStringAsync();
                var title = System.Text.RegularExpressions.Regex.Match(html, "<title>(.*?)</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                _output.WriteLine($"[RavenDB] GET / -> {(int)response.StatusCode}, {html.Length} chars, title={(title.Success ? title.Groups[1].Value : "(none)")}");

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
                // disposable container; a leftover database is harmless
            }
        }

        private static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= 400 ? value : value.Substring(0, 400) + " …");

        private static bool IsReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        // A throwaway content root plus the override the installer's self-writes land in — so the repository's
        // appsettings.json is never touched, exactly as the render guard and the install tests isolate themselves.
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation(string databaseType, string connectionString)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-raven-render-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = databaseType },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                    // All four must be non-empty or DatabaseManager refuses the automated install and serves the wizard.
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "R3nder!Raven$2026",
                        ["HostEmail"] = "raven-render@example.com",
                        ["HostName"] = "Host"
                    }
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
