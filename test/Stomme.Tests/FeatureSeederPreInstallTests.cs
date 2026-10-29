using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Stomme.Tests
{
    /// <summary>
    /// A fast guard for the seeder's pre-install fix: on the wizard path the feature seeder runs at host start
    /// BEFORE any database exists, so enumerating tenants through <see cref="ITenantDirectory"/> reaches a
    /// master store that is not there and throws. That is an EXPECTED boot state — no site is served
    /// pre-install — so the seeder must swallow it QUIETLY (no Error-level log) and defer, and then seed
    /// normally once the store becomes reachable (the restart every wizard install triggers). Before the fix
    /// the throw bubbled to the seeder's outer catch and logged
    /// <c>Unable to initialize the feature flag store; features will evaluate as disabled.</c> at Error — the
    /// benign-boot-state-as-noise trap this codebase warns about.
    /// <para>
    /// This composes the REAL seeder, the REAL EF store and evaluator, and the REAL tenancy adapter over a
    /// throwaway SQLite file — the same composition as <see cref="PerTenantFeatureFlagTests"/> — with a tenant
    /// directory that throws until it is told the store is ready. The end-to-end lifecycle across a real install
    /// is DRIVEN on PostgreSQL by <see cref="FeatureSeedingLifecycleTests"/>; this pins the log/seed behaviour
    /// deterministically without a live server.
    /// </para>
    /// </summary>
    public sealed class FeatureSeederPreInstallTests : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"stomme-preinstall-{Guid.NewGuid():N}.db");

        [Fact]
        public async Task Pre_install_the_seeder_defers_quietly_and_then_seeds_once_the_store_is_ready()
        {
            var logs = new CapturingLoggerProvider();
            var directory = new SwitchableTenantDirectory(); // starts "not installed": GetTenantIdsAsync throws.

            var services = new ServiceCollection();
            services.AddLogging(builder => { builder.SetMinimumLevel(LogLevel.Trace); builder.AddProvider(logs); });

            // The real tenancy adapter, exactly as AddStommeTenancy wires it ahead of the module scan, so the
            // module's single-tenant fallback stands down and the seeder walks tenants through the directory.
            services.AddScoped<SiteState>();
            services.AddScoped<IAliasAccessor>(_ => new StubAliasAccessor(alias: null));
            services.AddScoped<ITenantContext, StommeTenantContext>();
            services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
            services.AddSingleton<ITenantDirectory>(directory);
            services.AddSingleton<IFeatureContextProvider>(new AnonymousProvider());

            services.AddFeatureManagement(options => options.UseSqlite($"Data Source={_databasePath}"));

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            // ---- BOOT 1: master not installed. The seeder must NOT crash and must NOT log Error. ----------
            await RunSeederAsync(provider);

            var preInstallErrors = logs.Entries
                .Where(e => e.Level >= LogLevel.Error && e.Category.Contains("FeatureSeeder"))
                .ToList();
            Assert.Empty(preInstallErrors);

            // Nothing seeded yet: correctly off pre-install.
            Assert.False(await IsEnabledAsync(provider, FeatureCatalog.EmailNotifications));

            // ---- BOOT 2: the install has run; the directory now reports the master tenant. Seed + evaluate. --
            directory.MarkInstalled(TenantId.Default);
            await RunSeederAsync(provider);

            Assert.True(await IsEnabledAsync(provider, FeatureCatalog.EmailNotifications),
                $"{FeatureCatalog.EmailNotifications} evaluated OFF after the store became reachable and the seeder re-ran.");
            Assert.True(await IsEnabledAsync(provider, FeatureCatalog.VisitorTracking));

            // Still no Error across the whole lifecycle.
            Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error && e.Category.Contains("FeatureSeeder"));
        }

        private static async Task RunSeederAsync(IServiceProvider provider)
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        private static async Task<bool> IsEnabledAsync(IServiceProvider provider, string key)
        {
            var scopes = provider.GetRequiredService<ITenantScopeFactory>();
            using var tenantScope = scopes.BeginScope(TenantId.Default.Value);
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key);
        }

        public void Dispose()
        {
            // This database's pool only - ClearAllPools is process-wide. The string matches the one
            // handed to UseSqlite above to the byte, because the pool is keyed by the raw string.
            ScratchSqlite.Release($"Data Source={_databasePath}");
            try { File.Delete(_databasePath); } catch (IOException) { }
        }

        /// <summary>
        /// A tenant directory that reproduces the wizard lifecycle: it throws (master store unreachable) until
        /// <see cref="MarkInstalled"/> is called, after which it reports the installed tenant(s). A throw — not
        /// an empty list — is what a not-yet-provisioned relational master actually does (see
        /// <c>Content/Log/error.log</c>: the seeder's stack ran through <c>GetTenantIds</c> into an
        /// uninitialised <c>MasterDBContext</c>).
        /// </summary>
        private sealed class SwitchableTenantDirectory : ITenantDirectory
        {
            private IReadOnlyList<TenantId>? _tenants;

            public void MarkInstalled(params TenantId[] tenants) => _tenants = tenants;

            public Task<IReadOnlyList<TenantId>> GetTenantIdsAsync() =>
                _tenants is null
                    ? throw new InvalidOperationException("The master store is not installed yet (pre-install boot).")
                    : Task.FromResult(_tenants);
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
