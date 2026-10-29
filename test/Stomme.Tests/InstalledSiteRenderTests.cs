using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// THE BOOT/INSTALL/RENDER GUARD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only test that answers the question "does an installed site actually SERVE A PAGE".
    /// Everything else in the suite can be green while every Blazor route hangs or blank-pages — that has
    /// shipped a dead master twice (a rename that desynced assembly discovery, and a renderer
    /// deadlock, which reported 262 passed / 1 skipped while no route rendered at all).
    /// </para>
    /// <para>
    /// WHY IT IS NOT A BOOT CHECK. HTTP 200 and a byte count would both have PASSED against the dead
    /// master: when configuration is missing the app silently serves the INSTALLER, which is a perfectly
    /// healthy 200 of a few KB. So the assertions below are discriminating on purpose — they pin the
    /// seeded page's title, the seeded HtmlText module content, and the theme, and they assert the
    /// response is NOT the installer.
    /// </para>
    /// <para>
    /// WHY IT EXERCISES THE REAL RENDERER. The host is the production host: <see cref="Program.BuildAppAsync"/>
    /// composes the same container and the same middleware pipeline that <see cref="Program.Main"/> serves
    /// from, including the real <c>MapRazorComponents&lt;App&gt;</c> endpoint. Only the transport is swapped
    /// (TestServer instead of Kestrel), and the transport is not what installs the
    /// <c>RendererSynchronizationContext</c> — the Blazor endpoint's own renderer dispatcher is. So
    /// <c>App.OnInitializedAsync</c> -> <c>SiteService.GetSiteAsync</c> -> <c>ThemeRepository.ProcessThemes</c>
    /// -> <c>PermissionRepository.UpdatePermissions</c> runs here on the serialized renderer pump, exactly as
    /// in production. That claim is not taken on faith: reverting the renderer-deadlock fix makes the PostgreSQL case stop
    /// responding, which can only happen if the renderer's context is genuinely installed on the thread that
    /// reaches the repository.
    /// </para>
    /// <para>
    /// WHY IT INSTALLS FRESH EVERY RUN. <c>ProcessThemes</c> only writes when theme permissions are MISSING,
    /// so only a first render against a newly-installed database enters the branch that deadlocks. A guard
    /// pointed at an already-installed database never re-enters it and cannot see this bug at all.
    /// The throwaway database per case is load-bearing, not hygiene.
    /// </para>
    /// <para>
    /// PROVIDER DIMENSION — AND A WARNING. The cases are parameterized by provider so a new one slots in
    /// without redesign. SQLite is the always-on case and needs no external infrastructure, so this genuinely
    /// runs on every commit. But do NOT read the SQLite case as cover for the renderer deadlock. It is not, and
    /// this was measured, not assumed: with the fix reverted, the SQLite case PASSES (200, 12450 chars,
    /// 4s) while the PostgreSQL case FAILS on the 60s deadline. The deadlock needs the store write to
    /// actually suspend; Microsoft.Data.Sqlite's async surface completes synchronously and never posts a
    /// continuation to the blocked pump, whereas Npgsql's real async socket I/O does. So: the SQLite case guards the blank-page / broken-install / installer-served-instead-of-site
    /// class on every commit, and the PostgreSQL case — the shared PostgreSQL test server (see PostgresTestEndpoint),
    /// gated on a reachable instance — is the ONLY case here that guards the deadlock class. RavenDB IS
    /// install-selectable (it is a sixth <c>AvailableDatabases</c> entry with a
    /// <c>RavenDBConfig</c> control), but this render guard's <see cref="Providers"/> matrix is
    /// RELATIONAL-only and does not include it; RavenDB's render is covered by the standalone
    /// <c>RavenInstalledSiteRenderTests</c> (automated) and <c>RavenWizardInstallRenderTests</c> (wizard).
    /// </para>
    /// <para>
    /// ISOLATION. Every case runs against a throwaway content root and a throwaway database, and points
    /// <c>STOMME_APPSETTINGS_PATH</c> at that scratch directory so the installer's self-writes
    /// (InstallationId, DefaultDBType, the connection string) land in a temp <c>appsettings.override.json</c>
    /// and NEVER touch the repository's appsettings.json.
    /// </para>
    /// <para>
    /// SQLITE FILE RELEASE (task #41). Microsoft.Data.Sqlite pools connections by default, so after the
    /// SQLite case runs the pooled connections keep the scratch <c>.db</c> file OPEN past
    /// <see cref="ScratchInstallation.Dispose"/> — <c>Directory.Delete</c> then fails (swallowed) and leaks the
    /// temp tree; whether the handle happened to be free at teardown was itself racy (measured: 175 leaked
    /// <c>stomme-render-guard-*</c> dirs, most still holding the <c>.db</c>). <c>SqliteProvider.Cleanup</c>
    /// now clears that connection string's pool so the file is released deterministically. NOTE: the
    /// separately reported "~1 in 5" SQLite render FAILURE could not be reproduced here — 102 isolated SQLite
    /// runs across five conditions (isolated, SQLite-only, parallel load, background jobs forced to write at
    /// startup concurrent with the render, and full-suite contention) were all green — so this addresses the
    /// confirmed file-release defect, not a reproduced test failure. The renderer-deadlock class remains
    /// PostgreSQL-only; SQLite's async surface completes synchronously and cannot reproduce it.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [CollectionDefinition(nameof(InstalledSiteRenderTests), DisableParallelization = true)]
    [Trait("Category", "ExternalDatabase")]
    public class InstalledSiteRenderTests
    {
        // A deadlocked renderer must FAIL, not hang the suite forever. This is the whole point of the guard:
        // the bug it exists to catch does not throw, it stops responding.
        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;

        public InstalledSiteRenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Providers()
        {
            // Always on: no external infrastructure, so the guard actually runs on every commit.
            yield return new object[] { SqliteProvider.ProviderName };

            // Additional, service-gated. Absent a reachable PostgreSQL (the shared PostgreSQL test server, or
            // whatever POSTGRES_TEST_HOST/PORT point at) the case is simply not generated — the
            // every-commit path must never depend on it. That silence is what
            // Deadlock_class_is_covered_by_a_real_async_provider exists to break.
            if (PostgresProvider.IsReachable())
            {
                yield return new object[] { PostgresProvider.ProviderName };
            }
        }

        /// <summary>
        /// Fails when no deadlock-capable provider is reachable, so the gap is LOUD rather than silent.
        /// </summary>
        /// <remarks>
        /// Without this, an absent container does not skip the PostgreSQL case — it never generates it. The
        /// theory quietly drops from two cases to one and the suite still reports green, while the ONLY cover
        /// for the renderer deadlock class silently disappears. That is the precise failure mode this whole file
        /// exists to end: a green gate that does not cover the thing. `262 passed / 1 skipped` was green while
        /// every Blazor route hung, because the one skip WAS the guard.
        /// <para>
        /// So the coverage itself is asserted, not assumed. SQLite cannot stand in — measured, not inferred:
        /// with the renderer-deadlock fix reverted the SQLite case PASSES and only the PostgreSQL case FAILS, because
        /// Microsoft.Data.Sqlite's async surface completes synchronously and never suspends.
        /// </para>
        /// </remarks>
        [Fact]
        public void Deadlock_class_is_covered_by_a_real_async_provider()
        {
            var covered = Providers()
                .Select(row => (string)row[0])
                .Any(name => name != SqliteProvider.ProviderName);

            Assert.True(covered,
                $"No deadlock-capable provider is reachable, so nothing here guards the D29 renderer-deadlock " +
                $"class — and the suite would otherwise pass green while that cover is absent. SQLite does not " +
                $"count: its async surface completes synchronously, so it cannot reproduce the deadlock " +
                $"(measured — see D30). Make PostgreSQL reachable: the shared infra UAT instance at " +
                $"{PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (or set POSTGRES_TEST_HOST/PORT). " +
                $"Set POSTGRES_TEST_HOST/PORT/USER/PASSWORD to a reachable PostgreSQL.");
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Installer_seeds_a_renderable_site(string providerName)
        {
            var provider = ResolveProvider(providerName);
            using var scratch = new ScratchInstallation(provider);

            var html = await InstallAndGetHomePageAsync(scratch);

            // ---- NEGATIVE MARKER -------------------------------------------------------------------
            // When configuration is missing the app silently serves the installer wizard instead of the
            // site — a 200 with plausible bytes. The wizard renders a "Default Site Template" <option>;
            // the site never does. Assert this FIRST: it is the failure that fooled us for several rounds,
            // and it makes every assertion below meaningful rather than accidental.
            Assert.DoesNotContain("Default Site Template", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Installation Wizard", html, StringComparison.OrdinalIgnoreCase);

            // ---- THE SEEDED PAGE -------------------------------------------------------------------
            // The exact title. Both halves are seeded state resolved out of the database and rendered
            // through the theme's <PageTitle>: "Home" is the Page row the DefaultSiteTemplate created,
            // "Default Site" is Constants.DefaultSite as stamped on the Site row by CreateSite. Pinning
            // the whole string rather than a substring is deliberate — "Default Site" on its own also
            // occurs inside the installer's "Default Site Template" option, i.e. in the failure case.
            Assert.Contains("<title>Home - Default Site</title>", html, StringComparison.OrdinalIgnoreCase);

            // The seeded HtmlText module's content. This is the single most discriminating assertion in
            // the file: it can only appear if the module was discovered, its ModuleDefinition resolved,
            // its own DbContext migrated, its content row read, and its component rendered.
            Assert.Contains("Welcome To Stomme", html, StringComparison.OrdinalIgnoreCase);

            // The theme rendered rather than a bare error/blank body.
            Assert.DoesNotContain("Site Not Configured Correctly", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Site Is Disabled", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blazor.web.js", html, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> InstallAndGetHomePageAsync(ScratchInstallation scratch)
        {
            // Build the production host — same DI composition, same middleware pipeline, same
            // MapRazorComponents<App> endpoint — with only the transport swapped for TestServer.
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                // Drive the installer to completion, exactly where Program.Main drives it: after the host
                // is built, before it serves. With the connection string, alias, host password and host
                // email all present in configuration this takes DatabaseManager's automated-install path,
                // which is the documented way to skip the wizard.
                // Raced against a timeout rather than cancelled: InstallAsync takes no CancellationToken, so
                // there is nothing to signal once it is running. Task.Run keeps the await off this thread's
                // context; the Task.WhenAny below is what turns "hung installer" into a named failure instead
                // of a test that never returns.
                var installTask = Task.Run(() => Program.InstallAsync(app));
                if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
                {
                    throw new TimeoutException(
                        $"The installer did not finish within {InstallTimeout.TotalMinutes:0} minute(s) for " +
                        $"{scratch.Provider.Name}.");
                }

                var install = await installTask;

                Assert.True(string.IsNullOrEmpty(install.Message),
                    $"Installer reported an error for {scratch.Provider.Name}: {install.Message}");
                Assert.True(install.Success,
                    $"Installer did not report success for {scratch.Provider.Name}.");

                await app.StartAsync();

                using var client = app.GetTestClient();
                using var renderCts = new CancellationTokenSource(RenderTimeout);

                // The alias seeded from Installation:DefaultAlias is "localhost", which is exactly the
                // authority TestServer's client sends, so tenant resolution finds the site.
                //
                // The deadline is enforced by RACING the request rather than by trusting the cancellation
                // token. That is deliberate. A CancellationToken only unblocks a request that is still
                // observing it, and the bug this guard exists to catch blocks a thread dead — the token
                // was observed to buy nothing. Racing a Task.Delay is unconditional: it fires whether or
                // not anything downstream is still listening. The abandoned request leaks a wedged thread
                // in a process that is about to die anyway; a guard that hangs instead of failing is the
                // one thing worse than no guard.
                var requestTask = client.GetAsync("/", renderCts.Token);
                if (await Task.WhenAny(requestTask, Task.Delay(RenderTimeout)) != requestTask)
                {
                    // This is the renderer-deadlock signature. The request does not fail, it stops responding: the
                    // renderer's serialized pump is blocked on a sync-over-async wait whose continuation
                    // was posted back to that same pump.
                    throw new TimeoutException(
                        $"GET / did not respond within {RenderTimeout.TotalSeconds:0}s for " +
                        $"{scratch.Provider.Name}. The site did not render — a renderer deadlock " +
                        "(see docs/decisions.md D29) looks like exactly this.");
                }

                var response = await requestTask;
                var html = await response.Content.ReadAsStringAsync();
                var title = System.Text.RegularExpressions.Regex.Match(html, "<title>(.*?)</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                _output.WriteLine($"[{scratch.Provider.Name}] GET / -> {(int)response.StatusCode}, {html.Length} chars, title={(title.Success ? title.Groups[1].Value : "(none)")}");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return html;
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        // Teardown must not hang the suite either. When the renderer pump is wedged, a thread is blocked
        // forever by definition — nothing can unblock it — so both stopping and disposing are best-effort
        // AND time-bounded. Without the bound on DisposeAsync the mutation run hangs indefinitely instead
        // of reporting a failure, which would reproduce the very sin this guard exists to prevent: a suite
        // that never says "no". Abandoning the task leaks a thread in an already-doomed process; that is
        // the correct trade for a test harness.
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

        private static IRenderProvider ResolveProvider(string name) => name switch
        {
            SqliteProvider.ProviderName => new SqliteProvider(),
            PostgresProvider.ProviderName => new PostgresProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        /// <summary>
        /// A database the installer can be pointed at. Adding a provider — RavenDB, once it becomes
        /// install-selectable — means adding one implementation and one line in <see cref="Providers"/>.
        /// </summary>
        public interface IRenderProvider
        {
            string Name { get; }

            /// <summary>The assembly-qualified IDatabase type, as it appears in AvailableDatabases.</summary>
            string DatabaseType { get; }

            string CreateConnectionString(string scratchDirectory);

            /// <summary>
            /// Releases/drops what this run created. <paramref name="contentRoot"/> is passed because a
            /// file-backed install has more than one database under it and they are released by their exact
            /// connection strings, never by a process-wide clear.
            /// </summary>
            void Cleanup(string connectionString, string contentRoot);
        }

        private sealed class SqliteProvider : IRenderProvider
        {
            public const string ProviderName = "SQLite";

            public string Name => ProviderName;

            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";

            // A file inside the scratch directory: no server, no container, no port. This is what makes
            // the guard runnable on every commit.
            public string CreateConnectionString(string scratchDirectory)
                => ScratchSqlite.ConnectionString(Path.Combine(scratchDirectory, "stomme-render-guard.db"));

            public void Cleanup(string connectionString, string contentRoot)
            {
                // Microsoft.Data.Sqlite POOLS connections by default, keyed by connection string. After the
                // install+render, the pooled connections to the scratch databases stay OPEN and keep the OS
                // file handles live, so ScratchInstallation.Dispose's Directory.Delete then fails and leaks the
                // whole temp tree. This is not theoretical: after a measurement run 175 stomme-render-guard-*
                // dirs were left behind, most still holding stomme-render-guard.db — i.e. the file the render
                // read from was NOT released at teardown, and whether it happened to be released was itself
                // racy (some dirs deleted, some not). That is the "SQLite file not released between install and
                // render (WAL/journal/pooling)" hazard: a pooled connection keeps the file open.
                //
                // There is more than ONE SQLite file per run: the tenant/master store
                // (stomme-render-guard.db, this connectionString) AND the FeatureManagement module's own
                // Data/Stomme.Features.db, which has its own connection string. So clearing only this
                // connection string's pool released the tenant DB but left the features DB held. ClearAllPools
                // drops every idle pooled connection, releasing both handles deterministically so the scratch
                // directory can be removed and no stale pooled connection carries a file lock forward.
                //
                // CORRECTION. What used to stand here was ClearAllPools, justified by "the suite's
                // other SQLite users are all in-memory, so nothing else is in a pool". That was true when it was
                // written and STOPPED being true once the install suites became file-backed — after which this
                // teardown was reaching into whatever sibling class happened to be mid-request, in a suite whose
                // classes run in parallel collections. Measured cost of the process-wide form: a probe calling it
                // every 25 ms during a definition PUT produced 3 non-200 in 60.
                //
                // Both files are still released — that part of the old comment was right and is the whole reason
                // one connection string was not enough. ReleaseInstall covers the store named in the
                // post-install settings files AND the FeatureManagement sidecar, by their exact strings.
                // The install's own store. The SIDECARS are released by ScratchInstallation.Dispose for every
                // provider, not here - see the note there.
                ScratchSqlite.Release(connectionString);
            }
        }

        private sealed class PostgresProvider : IRenderProvider
        {
            public const string ProviderName = "PostgreSQL";

            // Endpoint + credentials are resolved centrally (PostgresTestEndpoint): the shared test server
            // instance by default, env-overridable. The account must be able to CREATE DATABASE — the guard
            // CREATEs a fresh database per run and drops it in Cleanup.
            private static readonly string DatabaseName =
                "stomme_render_guard_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public string Name => ProviderName;

            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";

            public string CreateConnectionString(string scratchDirectory)
                => PostgresTestEndpoint.ConnectionString(DatabaseName);

            public void Cleanup(string connectionString, string contentRoot)
            {
                // Drop the throwaway database this run created — otherwise a stomme_render_guard_* database
                // accumulates on the (persistent, shared) instance per run. Best-effort by design.
                var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
                PostgresTestEndpoint.DropDatabase(database);
            }

            public static bool IsReachable() => PostgresTestEndpoint.IsReachable();
        }

        /// <summary>
        /// A throwaway content root plus the override settings file the installer is allowed to scribble on.
        /// </summary>
        /// <remarks>
        /// Performing an install persists DefaultDBType, the connection string and InstallationId — which
        /// these cases do, because every one of them runs a REAL install. That is correct behaviour, not the
        /// old self-mutation defect: since a boot with no install intent writes nothing at all (pinned by
        /// <see cref="BootDoesNotMutateConfigTests"/>), but an actual install still writes, so the redirect
        /// below is still what keeps the repository's appsettings.json clean.
        /// <para>
        /// ConfigManager sends every one of those writes to the file named by STOMME_APPSETTINGS_PATH when
        /// that variable points at a directory containing an appsettings.override.json. The override is also
        /// read LAST by Program.BuildAppAsync, so it is simultaneously how the install configuration gets in —
        /// i.e. it is load-bearing here in its own right, and not merely a workaround for a defect.
        /// </para>
        /// </remarks>
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;
            private readonly string _connectionString;

            public ScratchInstallation(IRenderProvider provider)
            {
                Provider = provider;

                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-render-guard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);

                // NO wwwroot, deliberately. This line used to create an empty one, calling the ArgumentNullException
                // DefaultSiteTemplate threw without it "an artifact of the scratch content root, not a product defect".
                // CORRECTION: it WAS one - an app built on the Stomme.Server package has no web
                // root under `dotnet run` and its first install failed exactly there. Program.EnsureWebRoot gives the
                // host one, and this guard installs and renders without it so that it stays given.

                // The REAL shipped appsettings.json is the base layer — AvailableDatabases, RenderMode,
                // Logging and all. Testing against a hand-rolled config would test a fiction.
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                _connectionString = provider.CreateConnectionString(ContentRoot);

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject
                    {
                        ["DefaultDBType"] = provider.DatabaseType
                    },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = _connectionString
                    },
                    // All four of these must be non-empty or DatabaseManager refuses the automated install
                    // and falls back to serving the wizard.
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "R3nder!Guard$2026",
                        ["HostEmail"] = "render-guard@example.com",
                        ["HostName"] = "Host"
                    }
                };

                File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public IRenderProvider Provider { get; }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

                try
                {
                    Provider.Cleanup(_connectionString, ContentRoot);
                }
                catch
                {
                    // best effort
                }

                // EVERY provider, not just SQLite. An install's FeatureManagement flag store and the shared
                // module-aggregate store are SQLite files under the content root whatever the install's own
                // database is - so on the PostgreSQL leg the provider drops its PG database and nothing released
                // these, leaving the scratch tree behind. Releasing here covers both legs.
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
