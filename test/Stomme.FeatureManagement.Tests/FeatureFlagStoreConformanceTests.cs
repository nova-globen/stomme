using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// The feature-flag system's observable contract, expressed ONCE and driven against EVERY store provider it
/// can run on (D73 / brief 06 §"both-providers invariant"). A subclass supplies a fixture — the relational
/// (EF Core / SQLite) store or the RavenDB document store — and the tests below know nothing about either.
/// Both providers must produce the SAME observable results: seed the declared catalog, toggle, target by role,
/// pin a per-user override, and read the audit trail — so a provider-divergence bug (the deepest class in this
/// project) fails HERE rather than silently on a live Raven install.
/// <para>
/// The two shapes most at risk of diverging through a document serializer get their own round-trip tests:
/// <see cref="Targeting_survives_a_round_trip"/> (the <c>TargetRoles</c> collection, a read-only view over a
/// private <c>HashSet</c>) and <see cref="Overrides_survive_a_round_trip"/> (the <c>UserOverrides</c>
/// dictionary, a read-only view over a private <c>Dictionary&lt;Guid,bool&gt;</c>). EF JSON-columns them through
/// their backing fields; RavenDB must round-trip them through its Newtonsoft session with the same result.
/// </para>
/// </summary>
public abstract class FeatureFlagStoreConformanceTests : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private ServiceProvider _services = default!;

    // A live, mutable in-memory configuration the tests drive the REAL config source through (D84). The
    // provider is read on every evaluation, so SetConfig in a test body takes effect immediately without a
    // rebuild — which is how the config-precedence facts below run on BOTH the EF and RavenDB stores.
    private readonly MemoryConfigurationProvider _configProvider = new(new MemoryConfigurationSource());

    /// <summary>The principal every evaluation in a test sees; mutate it to switch users/roles.</summary>
    protected TestFeatureContextProvider Principal { get; } = new();

    /// <summary>
    /// Sets (or, with null, clears) a flag's state in the configuration source under test. "FeatureManagement"
    /// is Microsoft.FeatureManagement's default section name — the section the real config source reads.
    /// </summary>
    private void SetConfig(string key, bool? enabled) =>
        _configProvider.Set($"FeatureManagement:{key}", enabled?.ToString());

    /// <summary>Provider hook: register the store (EF/SQLite or RavenDB) onto <paramref name="services"/>.</summary>
    protected abstract void AddStore(IServiceCollection services);

    /// <summary>Provider hook: async store setup before composition (e.g. create the RavenDB database).</summary>
    protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

    /// <summary>Provider hook: async store teardown after disposal (e.g. drop the RavenDB database).</summary>
    protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await InitializeStoreAsync();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // Registered before the module composes so its anonymous fallback (a TryAdd) stands down — the seam a
        // host uses to supply its principal.
        services.AddSingleton(Principal);
        services.AddSingleton<IFeatureContextProvider>(Principal);

        // A live IConfiguration, so the module's real config-backed source (D84) composes over it exactly as
        // in a host. Registered before AddStore so the module's TryAdd factory reads THIS config root.
        services.AddSingleton<IConfiguration>(new ConfigurationRoot([_configProvider]));

        AddStore(services);

        _services = services.BuildServiceProvider();

        // Run the real seeder exactly as the host does at startup, so the declared catalog is materialised in
        // the store under test.
        foreach (var hosted in _services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await DisposeStoreAsync();
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task InScopeAsync(Func<IServiceProvider, Task> action) =>
        InScopeAsync<object?>(async sp =>
        {
            await action(sp);
            return null;
        });

    private Task<IReadOnlyList<FeatureView>> ListAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>().ListAsync());

    private Task<bool> IsEnabledAsync(string key) =>
        InScopeAsync(sp => sp.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(key));

    private Task<IReadOnlyList<FeatureAuditView>> ListAuditsAsync(string key) =>
        InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(key));

    // --- seed + evaluate ---

    [Fact]
    public async Task Seeder_materialises_the_declared_catalog_and_flags_evaluate()
    {
        var views = await ListAsync();

        Assert.Contains(views, v => v.Key == FeatureCatalog.EmailNotifications);
        Assert.Contains(views, v => v.Key == FeatureCatalog.VisitorTracking);
        // Contributor descriptor metadata reaches the view through the registry.
        Assert.Equal("Notifications", views.Single(v => v.Key == FeatureCatalog.EmailNotifications).Category);
        // Declared DefaultEnabled: true, nothing narrows the audience, so it is effectively on.
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Unknown_flag_is_off() =>
        Assert.False(await IsEnabledAsync("nope.does.not.exist"));

    // --- toggle ---

    [Fact]
    public async Task Toggling_a_flag_off_persists_and_evaluates_off()
    {
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        // Re-read in a fresh scope: proves the write reached the store, not just the identity map.
        Assert.False(await IsEnabledAsync(FeatureCatalog.VisitorTracking));
        Assert.False((await ListAsync()).Single(v => v.Key == FeatureCatalog.VisitorTracking).Enabled);
    }

    [Fact]
    public async Task Toggle_creates_a_flag_that_was_never_declared()
    {
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync("adhoc.flag", true));

        Assert.True(await IsEnabledAsync("adhoc.flag"));
    }

    // --- targeting (the TargetRoles collection) ---

    [Fact]
    public async Task Role_targeting_admits_only_users_holding_a_targeted_role()
    {
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        Principal.SetUser(Alice, "Administrators");
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        Principal.SetUser(Bob, "Registered Users");
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        Principal.SetAnonymous();
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Role_targeting_is_case_insensitive()
    {
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        // ORDINAL-IGNORECASE role match must hold on BOTH providers: the set is compared in memory after the
        // repository materialises it, so this pins the round-trip preserved the role AND the comparer semantics.
        Principal.SetUser(Alice, "ADMINISTRATORS");
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    /// <summary>
    /// THE collection round-trip. The role allow-list is a read-only <c>IReadOnlyCollection&lt;string&gt;</c>
    /// over a private <c>HashSet</c>; it must survive materialisation in a fresh scope on every provider, with
    /// the same members and the same rollout percentage.
    /// </summary>
    [Fact]
    public async Task Targeting_survives_a_round_trip()
    {
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators", "Host Users"], 25));

        var view = (await ListAsync()).Single(v => v.Key == FeatureCatalog.EmailNotifications);

        Assert.Equal(25, view.RolloutPercentage);
        // ListAsync projects TargetRoles ordered ordinal-ignore-case, so the expected order is deterministic.
        Assert.Equal(["Administrators", "Host Users"], view.TargetRoles);
    }

    // --- per-user overrides (the UserOverrides dictionary) ---

    [Fact]
    public async Task User_override_off_is_a_kill_switch_and_on_bypasses_targeting()
    {
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Alice, false));
        Principal.SetUser(Alice, "Administrators");
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, true));
        Principal.SetUser(Bob, "Registered Users");
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, null));
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    /// <summary>
    /// THE dictionary round-trip. The per-user overrides are a read-only <c>IReadOnlyDictionary&lt;Guid,bool&gt;</c>
    /// over a private <c>Dictionary</c>; both entries — the false kill switch and the true force-include — must
    /// materialise in a fresh scope with their exact values on every provider.
    /// </summary>
    [Fact]
    public async Task Overrides_survive_a_round_trip()
    {
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Alice, false));
        await InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, true));

        var view = (await ListAsync()).Single(v => v.Key == FeatureCatalog.EmailNotifications);

        Assert.NotNull(view.UserOverrides);
        Assert.False(view.UserOverrides![Alice]);
        Assert.True(view.UserOverrides[Bob]);
    }

    // --- audit trail ---

    [Fact]
    public async Task Every_flag_change_is_recorded_to_the_audit_trail()
    {
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));

        var audits = await ListAuditsAsync(FeatureCatalog.VisitorTracking);

        // Seeding wrote the first entry; the two toggles wrote the next two. Most recent first.
        Assert.True(audits.Count >= 3);
        Assert.True(audits[0].Enabled);
        Assert.False(audits[1].Enabled);
        Assert.All(audits, entry => Assert.Equal(FeatureCatalog.VisitorTracking, entry.Key));
    }

    [Fact]
    public async Task An_audit_entry_records_the_acting_principal_and_seeding_records_none()
    {
        var seeded = await ListAuditsAsync(FeatureCatalog.VisitorTracking);
        // Nobody is signed in at startup.
        Assert.Null(Assert.Single(seeded).ChangedBy);

        Principal.SetUser(Alice, "Administrators");
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        var audits = await ListAuditsAsync(FeatureCatalog.VisitorTracking);
        Assert.Equal(Alice.ToString(), audits[0].ChangedBy);
    }

    [Fact]
    public async Task An_idempotent_toggle_writes_no_audit_entry()
    {
        var before = await ListAuditsAsync(FeatureCatalog.VisitorTracking);

        // Seeded on; setting it on again is a no-op and must not raise an event on any provider.
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));

        var after = await ListAuditsAsync(FeatureCatalog.VisitorTracking);
        Assert.Equal(before.Count, after.Count);
    }

    // --- flag dependency + the standard surface ---

    [Fact]
    public async Task A_flag_is_off_when_the_flag_it_depends_on_is_off()
    {
        Assert.True(await IsEnabledAsync(TestFeatures.Dependent));

        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));
        Assert.False(await IsEnabledAsync(TestFeatures.Dependent));

        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));
        Assert.True(await IsEnabledAsync(TestFeatures.Dependent));
    }

    // --- configuration precedence (D84): config -> DB -> catalog default, on BOTH providers ---

    /// <summary>
    /// Config present WINS, in both directions, over the DB toggle — and it must do so identically on the EF
    /// and RavenDB stores, since a config override that only held on one provider would silently diverge a
    /// document install (the deepest bug class here).
    /// </summary>
    [Fact]
    public async Task Config_present_overrides_the_db_state_in_both_directions()
    {
        // Seeded on (DefaultEnabled: true). Config false must force it off.
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
        SetConfig(FeatureCatalog.EmailNotifications, false);
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        // Toggle the DB row OFF, then config true must force it on — proving config beats the admin toggle,
        // not merely a default.
        await InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));
        Assert.False(await IsEnabledAsync(FeatureCatalog.VisitorTracking));
        SetConfig(FeatureCatalog.VisitorTracking, true);
        Assert.True(await IsEnabledAsync(FeatureCatalog.VisitorTracking));
    }

    /// <summary>Config ABSENT falls through to the DB state — the historical behaviour, unchanged.</summary>
    [Fact]
    public async Task Config_absent_falls_through_to_the_db_state()
    {
        // No config entry for this flag; the seeded DB default (on) stands.
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));

        // Set then clear config: clearing must restore the DB decision, not leave a stuck override.
        SetConfig(FeatureCatalog.EmailNotifications, false);
        Assert.False(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
        SetConfig(FeatureCatalog.EmailNotifications, null);
        Assert.True(await IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    /// <summary>
    /// The M5 knob: a config key with NO backing DB row turns a flag on (and, absent or false, off) — the
    /// shape W2 uses to enable a bundled module by config. Identical on both stores.
    /// </summary>
    [Fact]
    public async Task Config_only_flag_with_no_db_row_is_on_when_true_and_off_otherwise()
    {
        const string moduleFlag = "module.blogs.enabled";

        Assert.False(await IsEnabledAsync(moduleFlag)); // no row, no config -> off

        SetConfig(moduleFlag, true);
        Assert.True(await IsEnabledAsync(moduleFlag)); // config-only enable

        SetConfig(moduleFlag, false);
        Assert.False(await IsEnabledAsync(moduleFlag));
    }
}
