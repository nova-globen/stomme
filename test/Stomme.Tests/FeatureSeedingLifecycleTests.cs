using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using File = System.IO.File;

namespace Stomme.Tests
{
    /// <summary>
    /// DRIVES the feature-flag seeder across a REAL install lifecycle on PostgreSQL (D68), because that is the
    /// only place the survey's open question can be answered: on a fresh WIZARD install — where the host boots
    /// uninstalled, the seeder runs and fails at StartAsync (no master DB yet), the install then completes at
    /// runtime, and only a subsequent host start re-runs the seeder — do the flags actually end up seeded and
    /// evaluable, or do they stay silently OFF? <see cref="FeatureEvaluator"/> answers <c>false</c> for any
    /// flag missing from the store, so a seeding failure is not an error, it is every capability silently off.
    /// <para>
    /// Two orderings are driven against the real production host (<see cref="Program.BuildAppAsync"/>, TestServer
    /// transport only), on PostgreSQL (the shared infra UAT instance by default; see <see cref="PostgresTestEndpoint"/>):
    /// </para>
    /// <list type="bullet">
    /// <item><b>Automated install</b> (config prefilled): <c>InstallAsync</c> runs BEFORE the host's hosted
    /// services start, so the seeder sees the tenant on its first and only run — flags must be seeded and
    /// evaluable with no restart.</item>
    /// <item><b>Wizard install</b> (host boots uninstalled): the seeder runs pre-install and cannot reach the
    /// master store; the install then runs at runtime through <see cref="IDatabaseManager"/>; the host is
    /// restarted (a fresh <see cref="Program.BuildAppAsync"/> over the same content root + now-installed
    /// config); the seeder's next run must seed the flags. The pre-install run must fail QUIETLY — no
    /// Error-level log — because a not-yet-installed master is expected, not a fault.</item>
    /// </list>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class FeatureSeedingLifecycleTests
    {
        private const string PostgresType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

        // The framework's tenant 1 ("Master") encodes to TenantId.Default (FrameworkTenantId); that is the
        // tenant the master install creates, and the scope every evaluation below runs in.
        private static readonly Guid MasterTenant = TenantId.Default.Value;

        private static readonly string[] SeededFlags = [FeatureCatalog.EmailNotifications, FeatureCatalog.VisitorTracking];

        private readonly ITestOutputHelper _output;

        public FeatureSeedingLifecycleTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Automated_install_seeds_flags_that_evaluate_on_first_boot_with_no_restart()
        {
            RequirePostgres();
            using var scratch = new PgScratch(installationPrefilled: true);
            var logs = new CapturingLoggerProvider();

            var app = await BuildAsync(scratch, logs);
            try
            {
                var install = await RaceInstallAsync(app, scratch.DatabaseName);
                Assert.True(install.Success, $"Automated install failed: {install.Message}");
                await app.StartAsync();

                foreach (var flag in SeededFlags)
                {
                    var enabled = await EvaluateAsync(app, MasterTenant, flag);
                    _output.WriteLine($"[automated] {flag} => {enabled}");
                    Assert.True(enabled, $"{flag} evaluated OFF on a fresh automated install with no restart.");
                }

                Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error && e.Category.Contains("FeatureSeeder"));
            }
            finally
            {
                await ShutdownAsync(app);
                scratch.DropDatabase();
            }
        }

        [Fact]
        public async Task Wizard_install_ends_with_flags_seeded_and_evaluable_after_the_restart_it_triggers()
        {
            RequirePostgres();
            using var scratch = new PgScratch(installationPrefilled: false);

            // -- BOOT 1: uninstalled. The seeder runs at StartAsync and cannot reach the master store. ----------
            var boot1Logs = new CapturingLoggerProvider();
            var boot1 = await BuildAsync(scratch, boot1Logs);
            bool seededBeforeInstall;
            try
            {
                await boot1.StartAsync();

                // The seeder ran during StartAsync. Its failure to reach a not-yet-installed master must be
                // QUIET: an Error here is the "benign pre-install failure mistaken for a real one" noise D68
                // exists to remove.
                var seederErrors = boot1Logs.Entries
                    .Where(e => e.Level >= LogLevel.Error && e.Category.Contains("FeatureSeeder"))
                    .ToList();
                foreach (var e in seederErrors)
                {
                    _output.WriteLine($"[boot1 pre-install SEEDER ERROR] {e.Message}");
                }
                Assert.Empty(seederErrors);

                // Nothing is seeded yet (no master, no tenant) — this is correct: no site is served pre-install.
                seededBeforeInstall = await EvaluateAsync(boot1, MasterTenant, FeatureCatalog.EmailNotifications);
                _output.WriteLine($"[boot1 pre-install] {FeatureCatalog.EmailNotifications} => {seededBeforeInstall}");

                // -- The wizard POST: install at runtime, exactly as InstallationController.Post does. ----------
                var install = await RaceInstallAsync(boot1, WizardConfig(scratch), scratch.DatabaseName);
                Assert.True(install.Success, $"Wizard install failed: {install.Message}");
            }
            finally
            {
                // The installer calls RestartApplication() (StopApplication). Model that as a full host stop.
                await ShutdownAsync(boot1);
            }

            Assert.False(seededBeforeInstall, "A flag evaluated ON before any install ran — the store was not empty.");

            // -- BOOT 2: the restart the install triggers. Same content root + the config the install wrote. ----
            var boot2Logs = new CapturingLoggerProvider();
            var boot2 = await BuildAsync(scratch, boot2Logs);
            try
            {
                await boot2.StartAsync();

                foreach (var flag in SeededFlags)
                {
                    var enabled = await EvaluateAsync(boot2, MasterTenant, flag);
                    _output.WriteLine($"[boot2 post-restart] {flag} => {enabled}");
                    Assert.True(enabled, $"{flag} evaluated OFF after a wizard install + the restart it triggers.");
                }

                Assert.DoesNotContain(boot2Logs.Entries, e => e.Level >= LogLevel.Error && e.Category.Contains("FeatureSeeder"));
            }
            finally
            {
                await ShutdownAsync(boot2);
                scratch.DropDatabase();
            }
        }

        // ---- driving helpers -----------------------------------------------------------------------------

        private static async Task<WebApplication> BuildAsync(PgScratch scratch, CapturingLoggerProvider logs) =>
            await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<ILoggerProvider>(logs);
                });

        private static InstallConfig WizardConfig(PgScratch scratch) => new()
        {
            ConnectionString = scratch.ConnectionString,
            DatabaseType = PostgresType,
            TenantName = TenantNames.Master,
            IsNewTenant = true,
            Aliases = "localhost",
            SiteName = Constants.DefaultSite,
            HostUsername = "host",
            HostPassword = "R3nder!Guard$2026",
            HostEmail = "d68@example.com",
            HostName = "Host"
        };

        private static async Task<Installation> RaceInstallAsync(WebApplication app, string database) =>
            await RaceInstallAsync(app, null, database);

        private static async Task<Installation> RaceInstallAsync(WebApplication app, InstallConfig? config, string database)
        {
            var manager = app.Services.GetRequiredService<IDatabaseManager>();
            var installTask = Task.Run(() => config is null ? manager.InstallAsync() : manager.InstallAsync(config));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"Install did not finish within {InstallTimeout.TotalMinutes:0} min for {database}.");
            }

            return await installTask;
        }

        /// <summary>Evaluates a flag as <paramref name="tenant"/>, through the real evaluator, the way a job does.</summary>
        private static async Task<bool> EvaluateAsync(WebApplication app, Guid tenant, string key)
        {
            var scopes = app.Services.GetRequiredService<ITenantScopeFactory>();
            using var tenantScope = scopes.BeginScope(tenant);
            await using var scope = app.Services.CreateAsyncScope();
            var evaluator = scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>();
            return await evaluator.IsEnabledAsync(key);
        }

        private static async Task ShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }

        private static void RequirePostgres()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} " +
                "(set the " +
                "POSTGRES_TEST_* settings). D68's seeder lifecycle can only be settled by driving a " +
                "real install; SQLite proves nothing here.");
        }

        // ---- scratch install (own copy of the render guard's redirect pattern) ---------------------------

        private sealed class PgScratch : IDisposable
        {
            private readonly string? _previousOverridePath;

            public PgScratch(bool installationPrefilled)
            {
                DatabaseName = "stomme_d68_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                ConnectionString = PostgresTestEndpoint.ConnectionString(DatabaseName);

                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-d68-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ContentRoot);
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = PostgresType }
                };

                // The automated case carries the connection string + host account so InstallAsync installs before
                // serving. The wizard case carries NEITHER — the host boots uninstalled and the install arrives at
                // runtime, writing them itself, which the restart then reads.
                if (installationPrefilled)
                {
                    overrides["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = ConnectionString };
                    overrides["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = "localhost",
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "R3nder!Guard$2026",
                        ["HostEmail"] = "d68@example.com",
                        ["HostName"] = "Host"
                    };
                }

                File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }
            public string ConnectionString { get; }
            public string DatabaseName { get; }

            public void DropDatabase() => PostgresTestEndpoint.DropDatabase(DatabaseName);

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only - BL-104
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

            public void Dispose() { }

            private sealed class CapturingLogger(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    sink.Enqueue((category, logLevel, formatter(state, exception)));

                private sealed class NullScope : IDisposable
                {
                    public static readonly NullScope Instance = new();
                    public void Dispose() { }
                }
            }
        }
    }
}
