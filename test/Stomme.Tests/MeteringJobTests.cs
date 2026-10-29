using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Metering.Persistence;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Driven on a composed host: the nightly pass and the erasure participant.
    /// </summary>
    /// <remarks>
    /// These are the guarantees that are wrong by DEFAULT if nobody writes them down - a rollup job that appends
    /// duplicates, a repair that silently hands back spent capacity, a retention that eats the window the repair
    /// needs, an erasure that deletes the counts along with the person. Each is driven against the real module
    /// wiring on an installed SQLite site; the clock is never mocked, because every case can be arranged by writing
    /// a row with the instant it needs.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringJobTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;

        private readonly ITestOutputHelper _output;

        public MeteringJobTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_rollup_pass_is_idempotent_and_removes_a_slice_the_ledger_no_longer_supports()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var today = DayStart(DateTime.UtcNow);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var ledger = sp.GetRequiredService<IAiUsageRecordRepository>();
                    await ledger.AddAsync(Call(tenantId, siteId, "OpenAI", "gpt-4o", "Tester", 100, 50, today.AddHours(1)));
                    await ledger.AddAsync(Call(tenantId, siteId, "OpenAI", "gpt-4o", "Tester", 10, 5, today.AddHours(2)));
                    await ledger.AddAsync(Call(tenantId, siteId, "Anthropic", "claude", "Tester", 7, 3, today.AddHours(3)));
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });


                var first = await RunPassAsync(app);
                var afterFirst = await RollupsAsync(app, siteId);
                _output.WriteLine($"[first] written={first.RollupsWritten} deleted={first.RollupsDeleted} rows={afterFirst.Count}");

                Assert.Equal(2, afterFirst.Count); // two slices: one per provider/model pair
                var openAi = afterFirst.Single(row => row.ProviderNormalized == "openai");
                Assert.Equal(2, openAi.Calls);
                Assert.Equal(165, openAi.BillableTokens()); // 110 in + 55 out

                // A second pass over an unchanged ledger must converge, not duplicate.
                var second = await RunPassAsync(app);
                var afterSecond = await RollupsAsync(app, siteId);
                _output.WriteLine($"[second] written={second.RollupsWritten} deleted={second.RollupsDeleted} rows={afterSecond.Count}");
                Assert.Equal(2, afterSecond.Count);
                Assert.Equal(2, afterSecond.Single(row => row.ProviderNormalized == "openai").Calls);

                // Remove one slice's ledger rows: the rollup that no longer has support is DELETED, not left behind.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var ledger = sp.GetRequiredService<IAiUsageRecordRepository>();
                    foreach (var row in (await ledger.ListBySiteAsync(siteId)).Where(row => row.ProviderNormalized == "anthropic"))
                    {
                        await ledger.DeleteAsync(row);
                    }

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var third = await RunPassAsync(app);
                var afterThird = await RollupsAsync(app, siteId);
                _output.WriteLine($"[third] written={third.RollupsWritten} deleted={third.RollupsDeleted} rows={afterThird.Count}");
                Assert.Equal(1, third.RollupsDeleted);
                Assert.Single(afterThird);
                Assert.Equal("openai", afterThird[0].ProviderNormalized);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_repair_corrects_a_monthly_counter_and_refuses_to_touch_a_total_one()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var nowUtc = DateTime.UtcNow;
                const string monthly = "tests.repairable";
                const string total = "tests.forever";

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var ledger = sp.GetRequiredService<IUsageRecordRepository>();

                    // A monthly counter that DISAGREES with its ledger - the case the repair exists for. Three rows
                    // of 2 inside the window; the counter says 99.
                    var monthlyQuota = QuotaAggregate.Define(QuotaIds.For(tenantId, siteId, monthly, QuotaPeriod.Monthly), tenantId, siteId, monthly, QuotaPeriod.Monthly, 1000, 0, nowUtc);
                    monthlyQuota.Apply(99, nowUtc);
                    await quotas.AddAsync(monthlyQuota);

                    var windowStart = QuotaAggregate.WindowStart(QuotaPeriod.Monthly, nowUtc);
                    foreach (var offset in new[] { 1, 2, 3 })
                    {
                        var row = UsageRecordAggregate.Append(
                            QuotaIds.ForUsage(tenantId, siteId, monthly, Guid.NewGuid()), tenantId, siteId, monthly, 2, null, "tests", Guid.NewGuid(),
                            windowStart.AddMinutes(offset));
                        row.MarkApplied();
                        await ledger.AddAsync(row);
                    }

                    // A Total counter with NO ledger behind it at all. Repairing it from an empty ledger would hand
                    // the site back every unit it had spent, which is exactly what must not happen.
                    var totalQuota = QuotaAggregate.Define(QuotaIds.For(tenantId, siteId, total, QuotaPeriod.Total), tenantId, siteId, total, QuotaPeriod.Total, 10, 0, nowUtc);
                    totalQuota.Apply(8, nowUtc);
                    await quotas.AddAsync(totalQuota);

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var result = await RunPassAsync(app);
                _output.WriteLine($"[repair] repaired={result.QuotasRepaired}");

                var repaired = await QuotaAsync(app, siteId, monthly, QuotaPeriod.Monthly);
                var untouched = await QuotaAsync(app, siteId, total, QuotaPeriod.Total);
                _output.WriteLine($"[monthly] consumed={repaired!.Consumed} (was 99, ledger says 6)");
                _output.WriteLine($"[total]   consumed={untouched!.Consumed} (was 8, ledger says 0)");

                Assert.Equal(6, repaired.Consumed);
                Assert.Equal(8, untouched.Consumed);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Retention_purges_only_rows_past_the_floor_and_only_for_this_site()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var nowUtc = DateTime.UtcNow;
                const string metric = "tests.retained";

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var ledger = sp.GetRequiredService<IUsageRecordRepository>();

                    // One inside the 90-day default, one well outside it.
                    foreach (var age in new[] { 10, 200 })
                    {
                        var row = UsageRecordAggregate.Append(
                            QuotaIds.ForUsage(tenantId, siteId, metric, Guid.NewGuid()), tenantId, siteId, metric, 1, null, "tests", Guid.NewGuid(),
                            nowUtc.AddDays(-age));
                        row.MarkApplied();
                        await ledger.AddAsync(row);
                    }

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var result = await RunPassAsync(app);
                var remaining = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).ToList());

                _output.WriteLine($"[purge] purged={result.LedgerPurged} remaining={remaining.Count}");
                Assert.Equal(1, result.LedgerPurged);
                Assert.Single(remaining);
                Assert.True(remaining[0].OccurredOnUtc > nowUtc.AddDays(-90));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_retention_floor_cannot_be_lowered_below_the_window_the_repair_needs()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                var withoutSetting = await RetentionAsync(app, siteId);
                await WriteSettingAsync(app, siteId, MeteringSiteSettingNames.UsageRetention, "7");
                var lowered = await RetentionAsync(app, siteId);
                await WriteSettingAsync(app, siteId, MeteringSiteSettingNames.UsageRetention, "365");
                var raised = await RetentionAsync(app, siteId);

                _output.WriteLine($"[retention] default={withoutSetting} requested 7 -> {lowered}; requested 365 -> {raised}");

                Assert.Equal(SettingRepositoryMeteringSiteSettings.DefaultUsageRetentionDays, withoutSetting);
                Assert.Equal(SettingRepositoryMeteringSiteSettings.MinimumUsageRetentionDays, lowered);
                Assert.Equal(365, raised);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Erasing_a_user_nulls_the_subject_and_leaves_the_counts_exactly_as_they_were()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                const string metric = "tests.personal";
                const int userId = 4242;
                var nowUtc = DateTime.UtcNow;

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var ledger = sp.GetRequiredService<IUsageRecordRepository>();
                    var aiLedger = sp.GetRequiredService<IAiUsageRecordRepository>();

                    foreach (var quantity in new[] { 3L, 5L })
                    {
                        var row = UsageRecordAggregate.Append(
                            QuotaIds.ForUsage(tenantId, siteId, metric, Guid.NewGuid()), tenantId, siteId, metric, quantity, userId, "tests", Guid.NewGuid(), nowUtc);
                        row.MarkApplied();
                        await ledger.AddAsync(row);
                    }

                    await aiLedger.AddAsync(AiUsageRecordAggregate.Append(
                        Guid.NewGuid(), tenantId, siteId, "OpenAI", "gpt-4o", "Tester", 10, 4, 12, AiCallStatus.Success, 0m, "USD", null, userId, Guid.NewGuid(), nowUtc));

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                var before = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).Sum(row => row.Quantity));

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetServices<Stomme.Erasure.ISubjectErasureParticipant>().OfType<MeteringUserErasure>().Single().EraseAsync(new Stomme.Erasure.ErasureSubject(siteId, userId, null));
                    return true;
                });

                var after = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var rows = await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId);
                    var ai = await sp.GetRequiredService<IAiUsageRecordRepository>().ListBySiteAsync(siteId);
                    return (Total: rows.Sum(row => row.Quantity), Rows: rows.Count, Attributed: rows.Count(row => row.UserId != null) + ai.Count(row => row.UserId != null));
                });

                _output.WriteLine($"[erasure] before total={before}; after total={after.Total}, rows={after.Rows}, still attributed={after.Attributed}");

                Assert.Equal(before, after.Total);
                Assert.Equal(2, after.Rows);
                Assert.Equal(0, after.Attributed);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static AiUsageRecordAggregate Call(Tenancy.Domain.TenantId tenantId, int siteId, string provider, string model, string agent, long input, long output, DateTime occurredOnUtc) =>
            AiUsageRecordAggregate.Append(
                Guid.NewGuid(), tenantId, siteId, provider, model, agent, input, output, 5, AiCallStatus.Success, 0m, "USD", null, null, Guid.NewGuid(), occurredOnUtc);

        private static Task<RollupPassResult> RunPassAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                await sp.GetRequiredService<UsageRollupPass>().RunSiteAsync(
                    alias.SiteId, sp.GetRequiredService<ITenantContext>().CurrentTenantId!));

        private static Task<List<AiUsageRollupAggregate>> RollupsAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<IAiUsageRollupRepository>().ListBySiteAsync(siteId)).ToList());

        private static Task<QuotaAggregate> QuotaAsync(WebApplication app, int siteId, string metric, QuotaPeriod period) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<IQuotaRepository>().GetAsync(FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!, siteId, metric, period));

        private static Task<int> RetentionAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<IMeteringSiteSettings>().GetUsageRetentionDaysAsync(siteId));

        private static Task<bool> WriteSettingAsync(WebApplication app, int siteId, string name, string value) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var settings = sp.GetRequiredService<Repository.ISettingRepository>();
                var existing = await settings.GetSettingAsync(Shared.EntityNames.Site, siteId, name);
                if (existing is null)
                {
                    await settings.AddSettingAsync(new Models.Setting
                    {
                        EntityName = Shared.EntityNames.Site,
                        EntityId = siteId,
                        SettingName = name,
                        SettingValue = value,
                        IsPrivate = true,
                    });
                }
                else
                {
                    existing.SettingValue = value;
                    await settings.UpdateSettingAsync(existing);
                }

                return true;
            });

        private static DateTime DayStart(DateTime value) =>
            new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Utc);
    }
}
