#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.ContextualHelp.Contributions;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Modules.ContextualHelp.Infrastructure;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tests.Fixtures.HelpTestModule;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using TenantId = Stomme.Tenancy.Domain.TenantId;

namespace Stomme.Tests.Help
{
    /// <summary>
    /// Contributed help topics reach every site of every
    /// tenant on the boot path, under THAT tenant, once - and an upgrade touches only what it owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arrangement is the point.</b> Two tenants share ONE database and BOTH have an alias onto the SAME site id -
    /// the site-per-alias topology, and the one where a site id means nothing without its tenant. Before the fix,
    /// <c>DatabaseManager.InitializeSitesAsync</c> ran every tenant in one DI scope, and its tenant context cached the
    /// first tenant, so a reconciler called there would write tenant 2's topics under tenant 1 - invisible to tenant 2,
    /// and re-created on every boot because no provider has a unique index. The shared runner enters each tenant's
    /// scope BEFORE it creates a DI scope; this suite is how that is proven for help.
    /// </para>
    /// <para>
    /// <b>Validated by mutation - and the first prediction was wrong.</b> The prediction said "validated by removing
    /// <c>BeginScope</c>". Measured: removing it alone leaves this suite GREEN, and so does sharing one DI scope across
    /// sites while keeping it - the runner has two guards and each suffices (see <c>SiteContentReconciliationRunner</c>).
    /// Removing BOTH, which was <c>InitializeSitesAsync</c>'s shape before the fix, turns the two-tenant cases red on all three providers.
    /// </para>
    /// <para>
    /// <b>The upgrade is driven the only way that class of defect shows</b>: TWO hosts over ONE database, never a
    /// fresh install. PostgreSQL co-locates the module store so that leg is PostgreSQL end to end. PostgreSQL and
    /// RavenDB FAIL rather than skip.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class HelpTopicReconciliationTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HelpTopicReconciliationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        // ---- two tenants, one site id ---------------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Each_tenant_gets_its_OWN_topics_on_a_shared_site_id_and_a_second_run_writes_nothing(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);

            try
            {
                var (first, second) = await HelpDrive.AddSecondTenantAsync(app);
                var expected = ExpectedTopicCount(app);
                _output.WriteLine($"[{storeName}] tenants {first.Tenant.TenantId} and {second.Tenant.TenantId} on site {first.Alias.SiteId}; {expected} contributed topics");

                // The install's boot reconciled tenant 1 only - tenant 2 did not exist yet. This run is tenant 2's first.
                var report = await HelpDrive.RunReconcilersAsync(app);
                _output.WriteLine($"[{storeName}] run 1: {report.SitesVisited} site(s), failures: {string.Join(" || ", report.Failures)}");
                Assert.Empty(report.Failures);
                Assert.Equal(2, report.SitesVisited);

                var mine = await HelpDrive.TopicsAsync(app, first);
                var theirs = await HelpDrive.TopicsAsync(app, second);
                _output.WriteLine($"[{storeName}] tenant 1 sees {mine.Count}, tenant 2 sees {theirs.Count}");

                // Every contributed topic, ONCE, under EACH tenant - and each row carries its own tenant.
                Assert.Equal(expected, mine.Count);
                Assert.Equal(expected, theirs.Count);
                Assert.All(mine, topic => Assert.Equal(first.TenantId, topic.TenantId));
                Assert.All(theirs, topic => Assert.Equal(second.TenantId, topic.TenantId));

                // Same site id, same anchor, same locale - DIFFERENT rows (the tenant is part of the key).
                var mineIds = mine.Select(topic => topic.Id).ToHashSet();
                Assert.DoesNotContain(theirs, topic => mineIds.Contains(topic.Id));

                // ---- a second boot: nothing to write ----------------------------------------------------------------
                var before = Snapshot(mine.Concat(theirs));
                var again = await HelpDrive.RunReconcilersAsync(app);
                Assert.Empty(again.Failures);

                var after = Snapshot((await HelpDrive.TopicsAsync(app, first)).Concat(await HelpDrive.TopicsAsync(app, second)));
                Assert.Equal(before, after);       // same ids, same ContentVersion, same UpdatedOnUtc - one row per id
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_reconciler_that_THROWS_first_costs_itself_and_help_still_reaches_both_tenants()
        {
            // InitializeSitesAsync's loop sat inside ONE catch (before the fix), so a throwing step on site 1 silently
            // aborted everything after it. The runner contains each reconciler per site - asserted with the thrower
            // registered FIRST, so it runs before help on every site.
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, colocateModuleAggregates: false,
                extraSettings: HelpContributorConflictTests.HelpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.Insert(0, ServiceDescriptor.Scoped<ISiteContentReconciler, ThrowingReconciler>()),
                start: false);

            try
            {
                var (first, second) = await HelpDrive.AddSecondTenantAsync(app);
                var report = await HelpDrive.RunReconcilersAsync(app);
                _output.WriteLine($"run: {report.SitesVisited} site(s), failures: {string.Join(" || ", report.Failures)}");

                Assert.Equal(2, report.Failures.Count(failure => failure.StartsWith(ThrowingReconciler.ReconcilerName, StringComparison.Ordinal)));
                Assert.DoesNotContain(report.Failures, failure => failure.StartsWith("Help topics", StringComparison.Ordinal));

                var expected = ExpectedTopicCount(app);
                Assert.Equal(expected, (await HelpDrive.TopicsAsync(app, first)).Count);
                Assert.Equal(expected, (await HelpDrive.TopicsAsync(app, second)).Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- the upgrade: two hosts over one database -------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task An_upgrade_creates_what_is_new_updates_only_what_it_OWNS_and_leaves_every_edit_alone(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: HelpContributorConflictTests.HelpEnabled());

            // ---- Phase 1: the older release -----------------------------------------------------------------------------
            var older = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.AddSingleton<IHelpTopicContributor>(new Planted("upgrade",
                    Topic("upgrade:changed", "Version one."), Topic("upgrade:same", "Never changes."), Topic("upgrade:handed", "Upgrade's text."))),
                start: false);

            Dictionary<string, (Guid Id, int Version, DateTime Updated)> phase1;
            try
            {
                var site = await HelpDrive.SiteAsync(older, Host);
                var topics = await HelpDrive.TopicsAsync(older, site);
                _output.WriteLine($"[{storeName}] phase 1: {string.Join(", ", topics.Select(topic => topic.Anchor))}");
                Assert.Contains(topics, topic => topic.Anchor == "upgrade:changed");
                Assert.DoesNotContain(topics, topic => topic.Anchor == "upgrade:new");
                phase1 = topics.ToDictionary(topic => topic.Anchor + "|" + topic.Locale, topic => (topic.Id, topic.ContentVersion, topic.UpdatedOnUtc));

                // The operator edits a FRAMEWORK topic and writes a draft of their own - what no boot may revert.
                await HelpDrive.InSiteAsync(older, site, async sp =>
                {
                    var repository = sp.GetRequiredService<IHelpTopicRepository>();
                    var admin = (await repository.GetAsync("page:admin", "en"))!;
                    admin.UpdateContent("Our administration", "Written by **our** operator.", DateTime.UtcNow);
                    await repository.UpdateAsync(admin);
                    await repository.AddAsync(HelpTopicAggregate.Author(site.TenantId, site.Alias.SiteId, "page:handmade", "en", "Handmade", "A draft.", DateTime.UtcNow));
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(older);
            }

            // ---- Phase 2: same database, a newer release -------------------------------------------------------------
            // "upgrade" changes one body, keeps one, adds one, and no longer ships upgrade:handed - which "replacement"
            // now ships with different text. A replacement must NOT take a row another contributor seeded.
            var newer = await GateDrive.InstallAsync(scratch, _output, builder =>
            {
                builder.Services.AddSingleton<IHelpTopicContributor>(new Planted("upgrade",
                    Topic("upgrade:changed", "Version TWO."), Topic("upgrade:same", "Never changes."), Topic("upgrade:new", "Arrived with the upgrade.")));
                builder.Services.AddSingleton<IHelpTopicContributor>(new Planted("replacement", Topic("upgrade:handed", "Replacement's text.")));
            }, start: false);

            try
            {
                var site = await HelpDrive.SiteAsync(newer, Host);
                var topics = (await HelpDrive.TopicsAsync(newer, site)).ToDictionary(topic => topic.Anchor + "|" + topic.Locale);
                _output.WriteLine($"[{storeName}] phase 2: {string.Join(", ", topics.Values.Select(topic => $"{topic.Anchor} v{topic.ContentVersion} seeded={topic.IsSeeded}/{topic.SeedContributorId}"))}; host errors: {string.Join(" || ", GateDrive.DrainCapturedErrors())}");

                // A NEW contributed topic reached a site that existed before it.
                var created = topics["upgrade:new|en"];
                Assert.True(created.IsPublished);
                Assert.Equal("upgrade", created.SeedContributorId);

                // An OWNED, still-seeded topic whose text changed is updated - once.
                Assert.Equal("Version TWO.", topics["upgrade:changed|en"].Markdown);
                Assert.Equal(2, topics["upgrade:changed|en"].ContentVersion);

                // An UNCHANGED seed writes NOTHING: same version, same timestamp, so its cache key survives.
                var same = topics["upgrade:same|en"];
                Assert.Equal(phase1["upgrade:same|en"].Version, same.ContentVersion);
                Assert.Equal(phase1["upgrade:same|en"].Updated, same.UpdatedOnUtc);

                // Another contributor's row is NOT taken over.
                Assert.Equal("Upgrade's text.", topics["upgrade:handed|en"].Markdown);
                Assert.Equal("upgrade", topics["upgrade:handed|en"].SeedContributorId);

                // The operator's edit survives the boot, and the topic is theirs now.
                Assert.Equal("Written by **our** operator.", topics["page:admin|en"].Markdown);
                Assert.False(topics["page:admin|en"].IsSeeded);

                // The hand-written draft survives, still a draft.
                Assert.False(topics["page:handmade|en"].IsPublished);

                // ---- and a further run changes nothing --------------------------------------------------------------
                var before = Snapshot(topics.Values);
                var report = await HelpDrive.RunReconcilersAsync(newer);
                Assert.Empty(report.Failures);
                Assert.Equal(before, Snapshot(await HelpDrive.TopicsAsync(newer, site)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(newer);
            }
        }

        // ---- the switches ------------------------------------------------------------------------------------------------

        [Fact]
        public async Task With_Help_OFF_a_boot_writes_NO_topic()
        {
            // A stock install: Help:Enabled absent. The reconciler is not even registered.
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                using (var scope = app.Services.CreateScope())
                {
                    Assert.DoesNotContain(scope.ServiceProvider.GetServices<ISiteContentReconciler>(), reconciler => reconciler is HelpTopicReconciler);
                }

                await HelpDrive.RunReconcilersAsync(app);
                Assert.Empty(await HelpDrive.TopicsAsync(app, await HelpDrive.SiteAsync(app, Host)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_module_knob_OFF_excludes_the_module_while_Help_itself_is_on()
        {
            // The module.contextualhelp.enabled knob, read from the MERGED configuration (the override file) - not
            // the container's IConfiguration, which was measured not to carry it.
            var settings = HelpContributorConflictTests.HelpEnabled();
            settings["FeatureManagement"] = new JsonObject { [ModuleFeatureFlags.FeatureKey(HelpTopicReconciler.ModuleName)] = false };

            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                var report = await HelpDrive.RunReconcilersAsync(app);
                Assert.Empty(report.Failures);
                Assert.Equal(1, report.SitesVisited);                                   // it WAS visited, and chose to write nothing
                Assert.Empty(await HelpDrive.TopicsAsync(app, await HelpDrive.SiteAsync(app, Host)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------------------------------

        /// <summary>How many topics the registry accepts on this host - the number every site must end up with.</summary>
        private static int ExpectedTopicCount(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<HelpTopicContributorRegistry>();
            Assert.Empty(registry.Conflicts);
            Assert.Contains(registry.Topics, topic => topic.ContributorId == StommeHelpContributor.Id);
            Assert.Contains(registry.Topics, topic => topic.ContributorId == HelpTestContributor.Id);
            return registry.Topics.Count;
        }

        private static string Snapshot(IEnumerable<HelpTopicAggregate> topics) =>
            string.Join("\n", topics
                .OrderBy(topic => topic.Id)
                .Select(topic => $"{topic.Id} v{topic.ContentVersion} {topic.UpdatedOnUtc:O} {topic.IsSeeded} {topic.SeedContributorId}"));

        private static HelpTopicContribution Topic(string anchor, string markdown) => new(anchor, "en", anchor, markdown);

        /// <summary>A contributor built by hand (a parameterised constructor, so the convention never discovers it).</summary>
        private sealed class Planted : IHelpTopicContributor
        {
            public Planted(string id, params HelpTopicContribution[] topics)
            {
                ContributorId = id;
                Topics = topics;
            }

            public string ContributorId { get; }

            public IReadOnlyCollection<HelpTopicContribution> Topics { get; }
        }

        private sealed class ThrowingReconciler : ISiteContentReconciler
        {
            public const string ReconcilerName = "Deliberately throwing";

            public string Name => ReconcilerName;

            public Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default) =>
                throw new InvalidOperationException("planted failure on site " + context.Site.SiteId);
        }
    }
}
