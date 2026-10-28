using System.Linq.Expressions;
using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Pins the BL-39 evaluator cache: N evaluations of a flag hit the store once; a flag WRITE (toggle,
/// targeting, per-user override — every mutating path in <see cref="IFeatureAdminService"/>) changes the
/// answer IMMEDIATELY; the D84 precedence (config → DB → catalog default) is untouched by caching — a
/// config-sourced answer keeps tracking LIVE configuration because config is never cached; and a tenant's
/// cached answer never answers another tenant (the cache key carries the same tenant identity the store's
/// tenant query filter reads by — the BL-41 trap, inverted: a cross-tenant cache hit would be a leak, a
/// worse defect than the slowness the cache removes).
/// <para>
/// Every evaluation here runs in its OWN DI scope, like a request or a background job would — so a store
/// hit collapsing across evaluations is collapsing across scopes, which is exactly the background-job shape
/// (a scoped evaluator over a singleton cache).
/// </para>
/// </summary>
public sealed class FeatureEvaluatorCacheTests
{
    // ---- composition helpers (mirrors FeatureConfigurationPrecedenceTests) ----

    private static ServiceProvider Compose(
        string databasePath,
        StoreHitCounter? counter = null,
        IConfiguration? configuration = null,
        ITenantContext? tenantContext = null,
        TestFeatureContextProvider? principal = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());

        if (tenantContext is not null)
        {
            // Registered before the module composes, so its DefaultTenantContext TryAdd stands down.
            services.AddSingleton(tenantContext);
        }

        if (principal is not null)
        {
            services.AddSingleton<IFeatureContextProvider>(principal);
        }

        services.AddFeatureManagement(options => options.UseSqlite($"Data Source={databasePath}"));

        if (counter is not null)
        {
            // A CLOSED registration wins over the module's open-generic EfRepository<,> (the MS.DI rule
            // verified for D80), so every FeatureFlag store read/write flows through the counter — the
            // probe that observes how many times evaluation actually reaches the store.
            services.AddScoped<IRepository<FeatureFlag, Guid>>(sp => new CountingFeatureFlagRepository(
                ActivatorUtilities.CreateInstance<EfRepository<FeatureFlag, Guid>>(sp),
                counter));
        }

        return services.BuildServiceProvider();
    }

    private static async Task RunSeederAsync(ServiceProvider services)
    {
        foreach (var hosted in services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static async Task<T> InScopeAsync<T>(ServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static Task<bool> IsEnabledAsync(ServiceProvider services, string key) =>
        InScopeAsync(services, sp => sp.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key));

    private static Task<Guid> ToggleAsync(ServiceProvider services, string key, bool enabled) =>
        InScopeAsync(services, sp => sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(key, enabled));

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"stomme-features-cache-{Guid.NewGuid():N}.db");

    private static void DropDb(string path)
    {
        // THIS database's pool, by the exact string Compose handed to UseSqlite. ClearAllPools is process-wide
        // and drops the pooled connections of whatever else is running, mid-request (BL-104).
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // ---- (1) N evaluations, one store hit ----

    /// <summary>
    /// The cache's reason to exist: evaluating the same flag N times (each in its own scope, as N requests
    /// or a background job would) reaches the store ONCE. Without the cache every evaluation is a
    /// GetAllAsync round trip — the per-request hot path the subsystem's own constraint forbids.
    /// </summary>
    [Fact]
    public async Task Repeated_evaluations_of_the_same_flag_hit_the_store_once()
    {
        var counter = new StoreHitCounter();
        var dbPath = TempDb();
        var services = Compose(dbPath, counter);
        try
        {
            await RunSeederAsync(services);
            counter.Reset(); // the seeder's own reads are not the evaluator's

            for (var i = 0; i < 5; i++)
            {
                Assert.True(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications));
            }

            Assert.Equal(1, counter.GetAllCalls);
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    // ---- (2) a write changes the answer immediately — the invalidation pin ----

    /// <summary>
    /// A toggle takes effect on the very next evaluation. The first evaluation warms the cache, so a
    /// missing (or broken) invalidation would keep serving the stale answer — this is the pin to
    /// mutation-check by disabling the invalidation call.
    /// </summary>
    [Fact]
    public async Task A_toggle_changes_the_answer_on_the_next_evaluation()
    {
        var dbPath = TempDb();
        var services = Compose(dbPath);
        try
        {
            await RunSeederAsync(services);

            Assert.True(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications));  // warms the cache

            await ToggleAsync(services, FeatureCatalog.EmailNotifications, false);
            Assert.False(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications)); // immediately off

            await ToggleAsync(services, FeatureCatalog.EmailNotifications, true);
            Assert.True(await IsEnabledAsync(services, FeatureCatalog.EmailNotifications));  // immediately back on
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    /// <summary>The other two mutating paths — per-user override and audience targeting — invalidate too.</summary>
    [Fact]
    public async Task Override_and_targeting_writes_change_the_answer_on_the_next_evaluation()
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var principal = new TestFeatureContextProvider();
        principal.SetUser(alice);

        var dbPath = TempDb();
        var services = Compose(dbPath, principal: principal);
        try
        {
            await RunSeederAsync(services);
            var key = FeatureCatalog.VisitorTracking;

            Assert.True(await IsEnabledAsync(services, key)); // warms the cache

            await InScopeAsync(services, sp =>
                sp.GetRequiredService<IFeatureAdminService>().SetUserOverrideAsync(key, alice, false));
            Assert.False(await IsEnabledAsync(services, key)); // the kill switch lands immediately

            await InScopeAsync(services, sp =>
                sp.GetRequiredService<IFeatureAdminService>().SetUserOverrideAsync(key, alice, null));
            Assert.True(await IsEnabledAsync(services, key));  // clearing it lands immediately

            await InScopeAsync(services, sp =>
                sp.GetRequiredService<IFeatureAdminService>().SetTargetingAsync(key, ["administrators"], 100));
            Assert.False(await IsEnabledAsync(services, key)); // alice holds no role — targeting lands immediately
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    // ---- (3) config precedence survives caching, and config stays LIVE ----

    /// <summary>
    /// The D84 precedence is untouched: after the DB answer is cached, a config value still wins over it —
    /// and because config answers are never cached, a LIVE config change (hot reload) takes effect on the
    /// very next evaluation, both appearing and disappearing.
    /// </summary>
    [Fact]
    public async Task Config_still_wins_over_a_cached_db_answer_and_tracks_live_config()
    {
        var configProvider = new MemoryConfigurationProvider(new MemoryConfigurationSource());
        var configuration = new ConfigurationRoot([configProvider]);

        var dbPath = TempDb();
        var services = Compose(dbPath, configuration: configuration);
        try
        {
            await RunSeederAsync(services);
            var key = FeatureCatalog.EmailNotifications;

            Assert.True(await IsEnabledAsync(services, key));   // DB says on; the cache is now warm

            configProvider.Set($"FeatureManagement:{key}", "false");
            Assert.False(await IsEnabledAsync(services, key));  // config wins over the CACHED db answer, immediately

            configProvider.Set($"FeatureManagement:{key}", null);
            Assert.True(await IsEnabledAsync(services, key));   // config withdrawn: the db answer stands again
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    // ---- (4) tenant isolation ----

    /// <summary>
    /// Tenant A's cached answer must never answer tenant B: the cache key carries the same tenant identity
    /// the store's tenant query filter reads by. A cache keyed on anything else would serve A's flag list to
    /// B — a cross-tenant LEAK, strictly worse than the slowness the cache removes.
    /// </summary>
    [Fact]
    public async Task A_tenants_cached_answer_does_not_answer_another_tenant()
    {
        var tenantA = TenantId.From(Guid.NewGuid());
        var tenantB = TenantId.From(Guid.NewGuid());
        var tenantContext = new TestTenantContext(tenantA);

        var dbPath = TempDb();
        var services = Compose(dbPath, tenantContext: tenantContext);
        try
        {
            await RunSeederAsync(services);
            const string key = "tests.cache.tenant";

            await ToggleAsync(services, key, true);            // creates the flag under tenant A
            Assert.True(await IsEnabledAsync(services, key));  // warms tenant A's cache entry

            tenantContext.Switch(tenantB);
            Assert.False(await IsEnabledAsync(services, key)); // B has no row and no config: off — a leaked
                                                               // cache hit from A would answer true here

            tenantContext.Switch(tenantA);
            Assert.True(await IsEnabledAsync(services, key));  // A's own answer still stands
        }
        finally
        {
            await services.DisposeAsync();
            DropDb(dbPath);
        }
    }

    // ---- test doubles ----

    /// <summary>Counts how many times evaluation actually reaches the store.</summary>
    private sealed class StoreHitCounter
    {
        private int _getAll;

        public int GetAllCalls => Volatile.Read(ref _getAll);

        public void CountGetAll() => Interlocked.Increment(ref _getAll);

        public void Reset() => Interlocked.Exchange(ref _getAll, 0);
    }

    /// <summary>
    /// The real EF repository behind a counter — registered CLOSED so it wins over the module's
    /// open-generic registration and every FeatureFlag store access flows through it.
    /// </summary>
    private sealed class CountingFeatureFlagRepository(IRepository<FeatureFlag, Guid> inner, StoreHitCounter counter)
        : IRepository<FeatureFlag, Guid>
    {
        public Task<FeatureFlag?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<FeatureFlag>> GetAllAsync(CancellationToken ct = default)
        {
            counter.CountGetAll();
            return inner.GetAllAsync(ct);
        }

        public Task<IReadOnlyList<FeatureFlag>> FindAsync(Expression<Func<FeatureFlag, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public Task AddAsync(FeatureFlag aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(FeatureFlag aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(FeatureFlag aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);
    }

    /// <summary>A mutable <see cref="ITenantContext"/> so one composition can act as different tenants.</summary>
    private sealed class TestTenantContext(TenantId initial) : ITenantContext
    {
        private TenantId _current = initial;

        public TenantId? CurrentTenantId => _current;

        public bool IsSystemScope => false;

        public void Switch(TenantId tenant) => _current = tenant;
    }
}
