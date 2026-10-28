using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Xunit;

namespace Stomme.FeatureManagement.Tests;

/// <summary>
/// Drives the feature-flag system end-to-end against a live relational store: seeding, evaluation,
/// targeting, per-user overrides, flag dependencies, and the audit trail. Each test gets its own store.
/// </summary>
public class FeatureFlagLiveTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Seeder_materialises_the_declared_catalog_and_flags_evaluate()
    {
        using var fixture = new FeatureStoreFixture();

        var views = await fixture.ListAsync();

        // Every declared flag exists after startup, without anyone having toggled it.
        Assert.Contains(views, v => v.Key == FeatureCatalog.EmailNotifications);
        Assert.Contains(views, v => v.Key == FeatureCatalog.VisitorTracking);
        // The contributor's descriptor metadata reaches the view through the registry.
        Assert.Equal("Notifications", views.Single(v => v.Key == FeatureCatalog.EmailNotifications).Category);
        // Declared DefaultEnabled: true, and nothing narrows the audience, so it is effectively on.
        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Unknown_flag_is_off()
    {
        using var fixture = new FeatureStoreFixture();

        Assert.False(await fixture.IsEnabledAsync("nope.does.not.exist"));
    }

    [Fact]
    public async Task Toggling_a_flag_off_persists_and_evaluates_off()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        // Re-read in a fresh scope: this proves the write reached the store, not just the identity map.
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.VisitorTracking));
        Assert.False((await fixture.ListAsync()).Single(v => v.Key == FeatureCatalog.VisitorTracking).Enabled);
    }

    [Fact]
    public async Task Toggle_creates_a_flag_that_was_never_declared()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync("adhoc.flag", true));

        Assert.True(await fixture.IsEnabledAsync("adhoc.flag"));
    }

    [Fact]
    public async Task Role_targeting_admits_only_users_holding_a_targeted_role()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        fixture.Principal.SetUser(Alice, "Administrators");
        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));

        fixture.Principal.SetUser(Bob, "Registered Users");
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));

        // An anonymous caller holds no roles, so a role-gated flag is off.
        fixture.Principal.SetAnonymous();
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Role_targeting_is_case_insensitive()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        fixture.Principal.SetUser(Alice, "ADMINISTRATORS");

        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Zero_percent_rollout_excludes_everyone_and_a_hundred_includes_everyone()
    {
        using var fixture = new FeatureStoreFixture();
        var admin = new[] { "Administrators" };

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, [], 0));

        fixture.Principal.SetUser(Alice, admin);
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
        fixture.Principal.SetUser(Bob, admin);
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, [], 100));

        fixture.Principal.SetUser(Alice, admin);
        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
        fixture.Principal.SetUser(Bob, admin);
        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Partial_rollout_is_deterministic_per_user_and_excludes_the_anonymous()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, [], 50));

        // Whatever bucket Alice lands in, she must land in it every time — a user must not flip in and out
        // of a rollout between requests.
        fixture.Principal.SetUser(Alice);
        var first = await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
        }

        // No stable bucket exists for an anonymous caller, so a partial rollout excludes them.
        fixture.Principal.SetAnonymous();
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task User_override_off_is_a_kill_switch_and_on_bypasses_targeting()
    {
        using var fixture = new FeatureStoreFixture();

        // Target the flag at a role Bob does not hold.
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators"], 100));

        // OFF for Alice, who would otherwise qualify by role.
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Alice, false));
        fixture.Principal.SetUser(Alice, "Administrators");
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));

        // ON for Bob, who does not qualify by role — the override force-includes him.
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, true));
        fixture.Principal.SetUser(Bob, "Registered Users");
        Assert.True(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));

        // Clearing returns Bob to the default audience rules, which exclude him.
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, null));
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task User_override_on_never_bypasses_the_admin_toggle()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.EmailNotifications, false));
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Alice, true));

        fixture.Principal.SetUser(Alice, "Administrators");

        // A disabled flag stays off for everyone; an override targets the audience, not the switch.
        Assert.False(await fixture.IsEnabledAsync(FeatureCatalog.EmailNotifications));
    }

    [Fact]
    public async Task Overrides_survive_a_round_trip_through_the_store()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Alice, false));
        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetUserOverrideAsync(FeatureCatalog.EmailNotifications, Bob, true));

        // The JSON-mapped override map must survive materialisation in a fresh scope/context.
        var view = (await fixture.ListAsync()).Single(v => v.Key == FeatureCatalog.EmailNotifications);

        Assert.NotNull(view.UserOverrides);
        Assert.False(view.UserOverrides![Alice]);
        Assert.True(view.UserOverrides[Bob]);
    }

    [Fact]
    public async Task Targeting_survives_a_round_trip_through_the_store()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp => sp.GetRequiredService<IFeatureAdminService>()
            .SetTargetingAsync(FeatureCatalog.EmailNotifications, ["Administrators", "Host Users"], 25));

        var view = (await fixture.ListAsync()).Single(v => v.Key == FeatureCatalog.EmailNotifications);

        Assert.Equal(25, view.RolloutPercentage);
        Assert.Equal(["Administrators", "Host Users"], view.TargetRoles);
    }

    [Fact]
    public async Task A_flag_is_off_when_the_flag_it_depends_on_is_off()
    {
        using var fixture = new FeatureStoreFixture();

        // tests.dependent is declared with RequiredFlag = visitors.tracking, and the seeder honours it.
        Assert.True(await fixture.IsEnabledAsync(TestFeatures.Dependent));

        // Turning the parent off must cascade to the dependent flag.
        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        Assert.False(await fixture.IsEnabledAsync(TestFeatures.Dependent));

        // ...and turning it back on restores the dependent flag.
        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));

        Assert.True(await fixture.IsEnabledAsync(TestFeatures.Dependent));
    }

    [Fact]
    public async Task Every_flag_change_is_recorded_to_the_audit_trail()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));
        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));

        var audits = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.VisitorTracking));

        // Seeding wrote the first entry; the two toggles wrote the next two. Most recent first.
        Assert.True(audits.Count >= 3);
        Assert.True(audits[0].Enabled);
        Assert.False(audits[1].Enabled);
        Assert.All(audits, entry => Assert.Equal(FeatureCatalog.VisitorTracking, entry.Key));
    }

    [Fact]
    public async Task An_audit_entry_records_the_acting_principal_and_seeding_records_none()
    {
        using var fixture = new FeatureStoreFixture();

        var seeded = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.VisitorTracking));
        // Nobody is signed in at startup.
        Assert.Null(Assert.Single(seeded).ChangedBy);

        fixture.Principal.SetUser(Alice, "Administrators");
        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        var audits = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.VisitorTracking));

        Assert.Equal(Alice.ToString(), audits[0].ChangedBy);
    }

    [Fact]
    public async Task Audit_history_can_be_narrowed_to_one_flag()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.EmailNotifications, false));

        var narrowed = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.EmailNotifications));
        var all = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync());

        Assert.All(narrowed, entry => Assert.Equal(FeatureCatalog.EmailNotifications, entry.Key));
        Assert.True(all.Count > narrowed.Count);
    }

    [Fact]
    public async Task An_idempotent_toggle_writes_no_audit_entry()
    {
        using var fixture = new FeatureStoreFixture();

        var before = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.VisitorTracking));

        // The flag seeded on; setting it on again is a no-op and must not raise an event.
        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, true));

        var after = await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ListAuditsAsync(FeatureCatalog.VisitorTracking));

        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public async Task Targeting_an_unknown_flag_fails_rather_than_creating_one()
    {
        using var fixture = new FeatureStoreFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().SetTargetingAsync("nope.unknown", ["Administrators"], 100)));
    }

    [Fact]
    public async Task A_rollout_percentage_outside_zero_to_a_hundred_is_rejected()
    {
        using var fixture = new FeatureStoreFixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().SetTargetingAsync(FeatureCatalog.EmailNotifications, [], 101)));
    }

    [Fact]
    public async Task The_registry_unions_every_discovered_contributor()
    {
        using var fixture = new FeatureStoreFixture();

        var known = await fixture.InScopeAsync(sp =>
            Task.FromResult(sp.GetRequiredService<IFeatureRegistry>().Known));

        // The framework's own contributor, discovered by reflection from Stomme.FeatureManagement...
        Assert.Contains(known, d => d.Key == FeatureCatalog.EmailNotifications);
        // ...and this test assembly's contributor, discovered the same way — the module-author seam.
        Assert.Contains(known, d => d.Key == TestFeatures.ModuleAuthored);
        Assert.Equal("A flag contributed by a module, discovered by reflection.",
            known.Single(d => d.Key == TestFeatures.ModuleAuthored).Description);
    }

    [Fact]
    public async Task A_module_authored_flag_is_seeded_and_evaluates_from_its_declaration_alone()
    {
        using var fixture = new FeatureStoreFixture();

        // Nothing registered this flag but the contributor type existing in a Stomme.* assembly.
        Assert.True(await fixture.IsEnabledAsync(TestFeatures.ModuleAuthored));
        Assert.False(await fixture.IsEnabledAsync(TestFeatures.ModuleAuthoredOffByDefault));
    }

    [Fact]
    public async Task The_standard_IFeatureManager_surface_is_backed_by_the_evaluator()
    {
        using var fixture = new FeatureStoreFixture();

        await fixture.InScopeAsync(sp =>
            sp.GetRequiredService<IFeatureAdminService>().ToggleAsync(FeatureCatalog.VisitorTracking, false));

        await fixture.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<IFeatureManager>();

            Assert.True(await manager.IsEnabledAsync(FeatureCatalog.EmailNotifications));
            Assert.False(await manager.IsEnabledAsync(FeatureCatalog.VisitorTracking));

            // The context overload evaluates against an explicit principal instead of the ambient one.
            Assert.True(await manager.IsEnabledAsync(
                FeatureCatalog.EmailNotifications, new FeatureContext(Alice, new HashSet<string> { "Administrators" })));

            var names = new List<string>();
            await foreach (var name in manager.GetFeatureNamesAsync())
            {
                names.Add(name);
            }

            Assert.Contains(FeatureCatalog.EmailNotifications, names);
            return true;
        });
    }
}

/// <summary>Flags declared by this assembly, standing in for a module author's catalog.</summary>
public static class TestFeatures
{
    public const string ModuleAuthored = "tests.module.authored";
    public const string ModuleAuthoredOffByDefault = "tests.module.authored.off";
    public const string Dependent = "tests.dependent";
}

/// <summary>
/// A module-author contributor: a public parameterless type in a <c>Stomme.*</c> assembly is all it takes
/// for its flags to reach the catalog, be seeded, and evaluate — no framework code changes, no registration.
/// </summary>
public sealed class TestFeatureContributor : IFeatureContributor
{
    public IReadOnlyCollection<FeatureDescriptor> Features =>
    [
        new(TestFeatures.ModuleAuthored, "A flag contributed by a module, discovered by reflection.",
            Category: "Tests", DefaultEnabled: true),
        new(TestFeatures.ModuleAuthoredOffByDefault, "A contributed flag that defaults off.",
            Category: "Tests", DefaultEnabled: false),
        // A declared dependency on another module's flag: this one can never be on while visitors.tracking
        // is off, which is how a module expresses "my capability needs theirs".
        new(TestFeatures.Dependent, "A contributed flag that depends on another flag.",
            Category: "Tests", DefaultEnabled: true, RequiredFlag: FeatureCatalog.VisitorTracking),
    ];
}
