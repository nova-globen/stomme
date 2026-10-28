using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D102, driven on a composed host: the recorder appends once per operation, folds into the quota, and the
    /// inspector answers before consumption.
    /// </summary>
    /// <remarks>
    /// These are the guarantees the design argued about, so they are exercised against the REAL module wiring on
    /// an installed SQLite site rather than against fakes: the deterministic ledger id, the pending-then-applied
    /// fold, the calendar window, the permissive answer for an undefined metric, and the fact that over-quota
    /// usage is still recorded.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringRecordingTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public MeteringRecordingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Usage_is_appended_once_per_operation_and_folded_into_the_quota()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                const string metric = "tests.widgets";

                // A quota an operator would have defined: 5 a month, warn at 80%.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IRepository<QuotaAggregate, Guid>>();
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var quota = QuotaAggregate.Define(
                        QuotaIds.For(tenantId, siteId, metric, QuotaPeriod.Monthly), tenantId, siteId, metric, QuotaPeriod.Monthly, 5, 80, DateTime.UtcNow);
                    await quotas.AddAsync(quota);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                // ---- one operation, recorded twice: appended once ------------------------------------------
                var operation = Guid.NewGuid();
                var first = await RecordAsync(app, new UsageEvent(metric, 2, operation, "tests"));
                var repeat = await RecordAsync(app, new UsageEvent(metric, 2, operation, "tests"));
                _output.WriteLine($"[first] recorded={first.Recorded} within={first.WithinQuota} reason={first.Reason} idempotent={first.Idempotent}");
                _output.WriteLine($"[repeat] recorded={repeat.Recorded} within={repeat.WithinQuota} reason={repeat.Reason} idempotent={repeat.Idempotent}");

                Assert.True(first.Recorded);
                Assert.True(first.WithinQuota);
                Assert.False(first.Idempotent);
                Assert.True(repeat.Idempotent);

                var rows = await LedgerAsync(app, metric);
                Assert.Single(rows);
                Assert.Equal(FoldState.Applied, rows[0].FoldState);
                Assert.Equal(2, rows[0].Quantity);
                Assert.Equal("tests", rows[0].Source);

                var counter = await QuotaAsync(app, siteId, metric);
                _output.WriteLine($"[quota] consumed={counter!.Consumed} limit={counter.Limit}");
                Assert.Equal(2, counter.Consumed);

                // ---- consuming past the limit: still recorded, and reported as exceeded ---------------------
                await RecordAsync(app, new UsageEvent(metric, 3, Guid.NewGuid(), "tests"));
                var over = await RecordAsync(app, new UsageEvent(metric, 1, Guid.NewGuid(), "tests"));
                _output.WriteLine($"[over] within={over.WithinQuota} reason={over.Reason}");
                Assert.False(over.WithinQuota);
                Assert.Equal(QuotaDecisionReason.Exceeded, over.Reason);
                Assert.Equal(3, (await LedgerAsync(app, metric)).Count);
                Assert.Equal(6, (await QuotaAsync(app, siteId, metric))!.Consumed);

                // ---- an undefined metric is permissive, and still recorded ----------------------------------
                var undefined = await RecordAsync(app, new UsageEvent("tests.unmetered", 1, Guid.NewGuid(), "tests"));
                _output.WriteLine($"[undefined] within={undefined.WithinQuota} reason={undefined.Reason}");
                Assert.True(undefined.WithinQuota);
                Assert.Equal(QuotaDecisionReason.Undefined, undefined.Reason);
                Assert.Single(await LedgerAsync(app, "tests.unmetered"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_inspector_answers_before_consumption_and_a_bad_key_is_refused()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                const string metric = "tests.seats";

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IRepository<QuotaAggregate, Guid>>();
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    await quotas.AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(tenantId, siteId, metric, QuotaPeriod.Total), tenantId, siteId, metric, QuotaPeriod.Total, 2, 0, DateTime.UtcNow));
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var before = await InspectAsync(app, metric, 2);
                _output.WriteLine($"[before] allowed={before.Allowed} reason={before.Reason} limit={before.Limit} remaining={before.Remaining}");
                Assert.True(before.Allowed);
                Assert.Equal(QuotaDecisionReason.Within, before.Reason);

                await RecordAsync(app, new UsageEvent(metric, 2, Guid.NewGuid(), null));

                var after = await InspectAsync(app, metric, 1);
                _output.WriteLine($"[after] allowed={after.Allowed} reason={after.Reason} consumed={after.Consumed}");
                Assert.False(after.Allowed);
                Assert.Equal(QuotaDecisionReason.Exceeded, after.Reason);

                // A non-canonical metric or source is REFUSED, not rewritten.
                await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(app, new UsageEvent("Tests.Mixed Case", 1, Guid.NewGuid(), null)));
                await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync(app, new UsageEvent(metric, 1, Guid.NewGuid(), "someone@example.com")));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<UsageOutcome> RecordAsync(WebApplication app, UsageEvent usage) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) => sp.GetRequiredService<IUsageRecorder>().RecordAsync(usage));

        private static Task<QuotaDecision> InspectAsync(WebApplication app, string metric, long quantity) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) => sp.GetRequiredService<IQuotaInspector>().EvaluateAsync(metric, quantity));

        private static Task<System.Collections.Generic.List<UsageRecordAggregate>> LedgerAsync(WebApplication app, string metric) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<IRepository<UsageRecordAggregate, Guid>>().FindAsync(row => row.Metric == metric)).ToList());

        private static Task<QuotaAggregate?> QuotaAsync(WebApplication app, int siteId, string metric) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<IRepository<QuotaAggregate, Guid>>().GetByIdAsync(QuotaIds.For(FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!, siteId, metric, QuotaPeriod.Monthly)));
    }
}
