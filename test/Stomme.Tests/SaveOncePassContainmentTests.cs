#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What a concurrency conflict inside a multi-row pass ACTUALLY costs (BL-152).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BL-152 was booked on a premise this suite REFUTES.</b> The row reads: "three more <i>update many, save
    /// once</i> loops … all mutate a collection and call <c>SaveChangesAsync</c> ONCE afterwards. If any single row
    /// conflicts, the whole pass is lost." The first half describes the source faithfully; the conclusion does not
    /// follow, because <b>this framework's <c>IUnitOfWork</c> persists nothing</b> (D67). Its
    /// <c>SaveChangesAsync</c> drains tracked aggregates and dispatches their domain events; the WRITE already
    /// happened, eagerly, inside the repository call — <c>EfRepository.Add/Update/DeleteAsync</c> each end in
    /// <c>context.SaveChangesAsync</c>, and it is there that a <c>DbUpdateConcurrencyException</c> becomes a
    /// <see cref="ConcurrencyConflictException"/>.
    /// </para>
    /// <para>
    /// So the loops are not "save once" in any sense that matters, and the real failure shape is different and
    /// worth naming precisely, which is what this suite measures: a conflict on one row <b>keeps every write the
    /// loop already made</b> and <b>abandons every row it had not reached yet</b>. Nothing is rolled back; the
    /// remainder is skipped. That distinction decides the fix — the containment has to wrap the REPOSITORY CALL,
    /// not a trailing save, which is exactly what BL-151 did for the quota repair.
    /// </para>
    /// <para>
    /// Driven on a real installed store through the gate harness rather than a double, because the whole question
    /// is what the provider does at commit time.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class SaveOncePassContainmentTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public SaveOncePassContainmentTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_conflict_mid_pass_keeps_the_earlier_writes_and_abandons_the_rest()
        {
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var metrics = new[] { "bl152.a", "bl152.b", "bl152.c" };

                await SeedAsync(app, siteId, metrics);

                // Loaded in ONE scope and used after it ends: three instances carrying the concurrency token as it
                // stood at load. This is the pass's own view of the world.
                var stale = await ReadAllAsync(app, siteId, metrics);

                // Somebody else updates the MIDDLE row - a live fold racing the pass, which is the routine case.
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var fresh = await quotas.GetAsync(tenantId, siteId, metrics[1], QuotaPeriod.Monthly);
                    fresh!.Repair(99, fresh.PeriodStartUtc);
                    await quotas.UpdateAsync(fresh);
                    return true;
                });

                // The pass, written the way all five of BL-152's loops are: mutate each row, no per-row guard.
                var written = new List<string>();
                var conflict = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    try
                    {
                        foreach (var quota in stale)
                        {
                            quota.Repair(7, quota.PeriodStartUtc);
                            await quotas.UpdateAsync(quota);
                            written.Add(quota.Metric);
                        }

                        return (ConcurrencyConflictException?)null;
                    }
                    catch (ConcurrencyConflictException ex)
                    {
                        return ex;
                    }
                });

                var after = await ReadAllAsync(app, siteId, metrics);
                foreach (var quota in after)
                {
                    _output.WriteLine($"[after] {quota.Metric} consumed={quota.Consumed}");
                }
                _output.WriteLine($"[pass] wrote {string.Join(", ", written)} before {(conflict is null ? "no conflict" : "the conflict")}");

                Assert.NotNull(conflict);

                // 1. The write made BEFORE the conflict is COMMITTED - it was never pending a trailing save.
                Assert.Equal(7, after.Single(q => q.Metric == metrics[0]).Consumed);

                // 2. The conflicting row keeps the other writer's value, which is the correct outcome.
                Assert.Equal(99, after.Single(q => q.Metric == metrics[1]).Consumed);

                // 3. The row the loop never REACHED is untouched. This is what the pass actually loses - the
                //    remainder, not the whole pass, and not anything already written.
                Assert.Equal(0, after.Single(q => q.Metric == metrics[2]).Consumed);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Per_row_containment_costs_only_the_conflicting_row()
        {
            // The same pass with the guard BL-151 put on the quota repair: the conflict is absorbed where it
            // happens, so every other row in the pass still lands. This is the shape any of BL-152's five loops
            // should take IF a conflict is ever shown to reach them.
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var metrics = new[] { "bl152.a", "bl152.b", "bl152.c" };

                await SeedAsync(app, siteId, metrics);
                var stale = await ReadAllAsync(app, siteId, metrics);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var fresh = await quotas.GetAsync(tenantId, siteId, metrics[1], QuotaPeriod.Monthly);
                    fresh!.Repair(99, fresh.PeriodStartUtc);
                    await quotas.UpdateAsync(fresh);
                    return true;
                });

                var skipped = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var missed = new List<string>();
                    foreach (var quota in stale)
                    {
                        quota.Repair(7, quota.PeriodStartUtc);
                        try
                        {
                            await quotas.UpdateAsync(quota);
                        }
                        catch (ConcurrencyConflictException ex)
                        {
                            // Record WHICH aggregate the conflict names, not just that one happened. If a later
                            // row's failure names an EARLIER row's id, the context is retrying the failed entry
                            // and the per-row guard is not containing anything.
                            missed.Add($"{quota.Metric} (conflict named {ex.EntityId})");
                        }
                    }

                    return missed;
                });

                var after = await ReadAllAsync(app, siteId, metrics);
                _output.WriteLine($"[contained] skipped={string.Join(", ", skipped)}; " +
                                  string.Join("; ", after.Select(q => $"{q.Metric}={q.Consumed}")));

                Assert.Equal(7, after.Single(q => q.Metric == metrics[0]).Consumed);
                Assert.Equal(99, after.Single(q => q.Metric == metrics[1]).Consumed);
                Assert.Equal(7, after.Single(q => q.Metric == metrics[2]).Consumed);   // the row the naive pass lost
                Assert.Single(skipped);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_fresh_scope_per_row_is_what_actually_contains_the_conflict()
        {
            // THE DECISIVE COMPARISON. In the test above, row 'c' was never touched by the other writer, so its
            // concurrency token was CURRENT and its update should have succeeded - yet it failed, and failed
            // inside a per-row try/catch that was supposed to contain the damage. The only thing different about
            // it is that it followed a failed save ON THE SAME DbContext.
            //
            // EfRepository.SaveAsync turns DbUpdateConcurrencyException into ConcurrencyConflictException and
            // rethrows, but it does NOT detach the entry that failed. The entry therefore stays Modified in the
            // context, and every later SaveChangesAsync in that scope re-submits it and fails again. (The
            // exception's EntityId cannot show this - it is built from the aggregate passed to the CURRENT call,
            // not from the entry that actually conflicted, which is why that instrument was dropped.)
            //
            // So: run the same pass with a fresh scope per row. If the untouched row lands, the shared context
            // is the cause and per-row try/catch alone is not containment - which also means BL-151's shipped
            // per-quota guard in UsageRollupJob.RepairQuotasAsync does not contain what it looks like it does.
            var store = LiftedRequiresGateTests.ResolveStore(SqliteGateStore.StoreName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var metrics = new[] { "bl152.a", "bl152.b", "bl152.c" };

                await SeedAsync(app, siteId, metrics);
                var stale = await ReadAllAsync(app, siteId, metrics);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var fresh = await quotas.GetAsync(tenantId, siteId, metrics[1], QuotaPeriod.Monthly);
                    fresh!.Repair(99, fresh.PeriodStartUtc);
                    await quotas.UpdateAsync(fresh);
                    return true;
                });

                var missed = new List<string>();
                foreach (var quota in stale)
                {
                    quota.Repair(7, quota.PeriodStartUtc);

                    // One scope - and therefore one DbContext - per row.
                    var conflicted = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    {
                        try
                        {
                            await sp.GetRequiredService<IQuotaRepository>().UpdateAsync(quota);
                            return false;
                        }
                        catch (ConcurrencyConflictException)
                        {
                            return true;
                        }
                    });

                    if (conflicted)
                    {
                        missed.Add(quota.Metric);
                    }
                }

                var after = await ReadAllAsync(app, siteId, metrics);
                _output.WriteLine($"[per-scope] skipped={string.Join(", ", missed)}; " +
                                  string.Join("; ", after.Select(q => $"{q.Metric}={q.Consumed}")));

                // Only the genuinely stale row is lost; the untouched row that follows it lands.
                Assert.Equal(new[] { metrics[1] }, missed);
                Assert.Equal(7, after.Single(q => q.Metric == metrics[0]).Consumed);
                Assert.Equal(99, after.Single(q => q.Metric == metrics[1]).Consumed);
                Assert.Equal(7, after.Single(q => q.Metric == metrics[2]).Consumed);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<bool> SeedAsync(Microsoft.AspNetCore.Builder.WebApplication app, int siteId, string[] metrics) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var quotas = sp.GetRequiredService<IQuotaRepository>();
                var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

                foreach (var metric in metrics)
                {
                    await quotas.AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(tenantId, siteId, metric, QuotaPeriod.Monthly),
                        tenantId, siteId, metric, QuotaPeriod.Monthly, 1000, 0, start));
                }

                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return true;
            });

        private static Task<List<QuotaAggregate>> ReadAllAsync(
            Microsoft.AspNetCore.Builder.WebApplication app, int siteId, string[] metrics) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(siteId))
                    .Where(quota => metrics.Contains(quota.Metric))
                    .OrderBy(quota => quota.Metric, StringComparer.Ordinal)
                    .ToList());
    }
}
