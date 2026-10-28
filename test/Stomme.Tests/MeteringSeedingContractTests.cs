using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Metering.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D102 item 6 (with the D103 correction): a quota a NEWER release declares reaches an ALREADY-INSTALLED site on
    /// the boot path, an operator's edit to an existing quota survives the upgrade, and a stock install - which
    /// declares nothing - seeds nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven the only way this class of defect shows: TWO hosts over ONE database, never a fresh install. The boot
    /// path is observed BEFORE the hosted services start (<c>start: false</c>), which is where <c>Program.Main</c>
    /// stands after <c>InstallAsync</c> and before <c>RunAsync</c> - and where the module store's tables may not
    /// exist yet.
    /// </para>
    /// <para>
    /// <b>Why a deterministic id is not enough on its own, and why this runs on more than one store.</b> Only
    /// RavenDB's <c>AddAsync</c> is a blind upsert; EF raises a primary-key violation and the File store throws on
    /// an existing path. So "create if absent" has to be a GetById followed by an Add, and a seeder that leaned on
    /// the id alone would pass on Raven and fail everywhere else. PostgreSQL and RavenDB FAIL rather than skip (D46).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class MeteringSeedingContractTests
    {
        private const string Host = "localhost";
        private const string DeclaredMetric = "upgrade.declared";

        private readonly ITestOutputHelper _output;

        public MeteringSeedingContractTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_later_quota_reaches_a_preinstalled_site_and_an_operator_edit_survives(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName);

            // ---- Phase 1: the older release declares NO quota. A stock install seeds nothing. ------------------
            var older = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            int siteId;
            try
            {
                var seeded = await QuotasAsync(older);
                _output.WriteLine($"[{storeName}] phase 1 (stock, boot path): {seeded.Count} quota(s); host errors: {string.Join(" || ", GateDrive.DrainCapturedErrors())}");
                Assert.Empty(seeded);
                siteId = (await GateDrive.AliasAsync(older, Host)).SiteId;
            }
            finally
            {
                await GateDrive.ShutdownAsync(older);
            }

            // ---- Phase 2: same database, a newer release that DECLARES one -------------------------------------
            var newer = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.AddSingleton<IDeclaredQuotaSource, DeclaringSource>(), start: false);
            try
            {
                var quotas = await QuotasAsync(newer);
                _output.WriteLine($"[{storeName}] phase 2 (boot path): {string.Join(", ", quotas.Select(q => $"{q.Metric}/{q.Period}={q.Limit}"))}");

                var declared = Assert.Single(quotas, quota => quota.Metric == DeclaredMetric);
                Assert.Equal(QuotaPeriod.Monthly, declared.Period);
                Assert.Equal(100, declared.Limit);
                Assert.Equal(QuotaIds.For(declared.TenantId, siteId, DeclaredMetric, QuotaPeriod.Monthly), declared.Id);

                // The operator lowers the limit and suspends enforcement - what the next boot must not revert.
                await GateDrive.InTenantAsync(newer, Host, async (sp, alias) =>
                {
                    var repository = sp.GetRequiredService<IQuotaRepository>();
                    var quota = await repository.GetForCurrentSiteAsync(declared.Id, alias.SiteId);
                    quota.Redefine(7, 50);
                    quota.Suspend();
                    await repository.UpdateAsync(quota);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                // ---- The runner again: additive means it adds NOTHING and reverts NOTHING ----------------------
                var report = await new SiteContentReconciliationRunner(
                    newer.Services.GetRequiredService<IServiceScopeFactory>(),
                    newer.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test")).RunAsync();

                Assert.Empty(report.Failures);
                Assert.Equal(1, report.SitesVisited);

                var again = await QuotasAsync(newer);
                var survivor = Assert.Single(again, quota => quota.Metric == DeclaredMetric);
                _output.WriteLine($"[{storeName}] after re-run: {again.Count} quota(s); declared limit={survivor.Limit} warn={survivor.WarnAtPercent} status={survivor.Status}");

                Assert.Equal(quotas.Count, again.Count);
                Assert.Equal(7, survivor.Limit);
                Assert.Equal(50, survivor.WarnAtPercent);
                Assert.Equal(QuotaStatus.Suspended, survivor.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(newer);
            }
        }

        /// <summary>
        /// The vacuity guard: the phase-1 "seeds nothing" assertion has to be capable of failing, and a source that
        /// declares nothing is indistinguishable from a reconciler that never ran. This drives the reconciler with
        /// a source that DOES declare and proves rows appear - and that a metric key the module would refuse is
        /// refused rather than rewritten.
        /// </summary>
        [Fact]
        public async Task The_reconciler_refuses_a_bad_metric_key_and_still_seeds_the_good_ones()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output,
                builder => builder.Services.AddSingleton<IDeclaredQuotaSource, MixedSource>(), start: false);

            try
            {
                var quotas = await QuotasAsync(app);
                _output.WriteLine($"[mixed] {string.Join(", ", quotas.Select(q => q.Metric))}");

                Assert.Single(quotas);
                Assert.Equal(DeclaredMetric, quotas[0].Metric);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<IReadOnlyList<QuotaAggregate>> QuotasAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(alias.SiteId));

        /// <summary>The "newer release": one declared quota.</summary>
        private sealed class DeclaringSource : IDeclaredQuotaSource
        {
            public string Name => "upgrade test";

            public IReadOnlyList<DeclaredQuota> Declare(int siteId) =>
                [new DeclaredQuota(DeclaredMetric, QuotaPeriod.Monthly, 100, 80)];
        }

        /// <summary>A source declaring one valid quota and one whose metric key is not canonical.</summary>
        private sealed class MixedSource : IDeclaredQuotaSource
        {
            public string Name => "mixed test";

            public IReadOnlyList<DeclaredQuota> Declare(int siteId) =>
            [
                new DeclaredQuota(DeclaredMetric, QuotaPeriod.Monthly, 100, 80),
                new DeclaredQuota("Upgrade.Not Canonical", QuotaPeriod.Daily, 10, 0),
            ];
        }
    }
}
