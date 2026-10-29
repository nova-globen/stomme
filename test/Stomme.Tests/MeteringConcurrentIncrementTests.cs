using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Concurrent recorders lose no ledger row and leave the counter equal to what was consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case the whole fold design exists for, and the providers do NOT agree about it on their own: EF
    /// has a concurrency token and a translation for it, RavenDB has change vectors but only for a document loaded
    /// in the same session, and the File store has neither. So the serialisation is the module's own - the
    /// <c>IDistributedLock</c> where a provider registers one (PostgreSQL's advisory lock, RavenDB's
    /// compare-exchange) and a keyed in-process gate where none does (SQLite, SQL Server, File). The in-process
    /// fallback is a RECORDED DIVERGENCE, not a claim of cross-host safety, and this suite is what makes the claim
    /// it DOES support concrete.
    /// </para>
    /// <para>
    /// Every recorder uses a DISTINCT operation id, so a lost row shows up as a missing ledger entry and a
    /// short counter - the two things a naive read-modify-write produces under contention.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class MeteringConcurrentIncrementTests
    {
        private const string Host = "localhost";
        private const string Metric = "tests.concurrent";
        private const int Recorders = 8;

        private readonly ITestOutputHelper _output;

        public MeteringConcurrentIncrementTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Concurrent_recorders_lose_no_row_and_the_counter_equals_what_was_consumed(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    await sp.GetRequiredService<IQuotaRepository>().AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(tenantId, siteId, Metric, QuotaPeriod.Monthly), tenantId, siteId, Metric, QuotaPeriod.Monthly, 1000, 0, DateTime.UtcNow));
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var outcomes = await Task.WhenAll(Enumerable.Range(0, Recorders).Select(_ =>
                    GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                        sp.GetRequiredService<IUsageRecorder>().RecordAsync(new UsageEvent(Metric, 1, Guid.NewGuid(), "tests")))));

                var rows = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).ToList());
                var quota = await GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                    sp.GetRequiredService<IQuotaRepository>().GetAsync(FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!, siteId, Metric, QuotaPeriod.Monthly));

                _output.WriteLine($"[{storeName}] {outcomes.Count(o => o.Recorded)}/{Recorders} recorded; ledger rows={rows.Count}; consumed={quota!.Consumed}; " +
                                  $"pending={rows.Count(row => row.FoldState == FoldState.Pending)}");

                Assert.All(outcomes, outcome => Assert.True(outcome.Recorded));
                Assert.Equal(Recorders, rows.Count);
                Assert.Equal(Recorders, quota.Consumed);
                Assert.All(rows, row => Assert.Equal(FoldState.Applied, row.FoldState));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
