#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (D106 item 8, gate <c>BackupScheduleJobTests</c>) - the scheduled job on an INSTALLED host, run exactly as
    /// <c>HostedServiceBase</c> runs it (per tenant: its tenant scope, a DI scope, <c>SetTenant</c>): off twice by default; with
    /// TWO tenants a due schedule is taken ONCE per tick, not twice; without the configured passphrase nothing is taken and the
    /// passphrase never reaches a note; the lease skips while a snapshot is being taken and fails an abandoned one; retention
    /// keeps the newest scheduled snapshots and never touches the operator's own. The queue's worker survives a failed item.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BackupScheduleJobTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Passphrase = "the scheduled passphrase";

        [Fact]
        public async Task The_job_is_off_by_default_and_does_nothing_while_Backup_is_disabled_or_without_its_passphrase()
        {
            foreach (var (settings, expected) in new[]
            {
                (new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = false } }, "Backup Is Disabled"),
                (new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } }, "Passphrase Is Not Set"),
            })
            {
                using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
                var app = await BootAsync(scratch);
                try
                {
                    Assert.False(new BackupScheduleJob(app.Services.GetRequiredService<IServiceScopeFactory>()).IsEnabled);
                    await Engine(app).SaveScheduleAsync(BackupScheduleAggregate.Create(24, 30, 3, enabled: true, DateTime.UtcNow));
                    var notes = await TickAsync(app);
                    output.WriteLine(notes);
                    Assert.Contains(expected, notes, StringComparison.Ordinal);
                    Assert.Empty(await Engine(app).ListSnapshotsAsync());
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }
        }

        [Fact]
        public async Task The_passphrase_file_wins_over_the_setting_and_a_named_file_that_is_missing_refuses_naming_it()
        {
            // Infra's deployed sites give a secret as a FILE (2026-09-29): Backup:Schedule:PassphraseFile wins over Passphrase.
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stomme-passphrase-" + Guid.NewGuid().ToString("N") + ".secret");
            await System.IO.File.WriteAllTextAsync(file, Passphrase + "\n");   // a secret file usually ends with a line break
            try
            {
                foreach (var (named, taken) in new[] { (file, true), (file + ".absent", false) })
                {
                    var settings = new JsonObject
                    {
                        ["Backup"] = new JsonObject
                        {
                            ["Enabled"] = true,
                            // Too short to be used: a snapshot taken proves the file won.
                            ["Schedule"] = new JsonObject { ["Passphrase"] = "short", ["PassphraseFile"] = named },
                        },
                    };
                    using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
                    var app = await BootAsync(scratch);
                    try
                    {
                        await Engine(app).SaveScheduleAsync(BackupScheduleAggregate.Create(24, 30, 3, enabled: true, DateTime.UtcNow));
                        var notes = await TickAsync(app);
                        output.WriteLine(notes);
                        Assert.DoesNotContain(Passphrase, notes, StringComparison.Ordinal);
                        if (taken)
                        {
                            Assert.Equal(1, CountOf(notes, " Taken"));
                            Assert.Equal(BackupSnapshotStatus.Available, Assert.Single(await Engine(app).ListSnapshotsAsync()).Status);
                        }
                        else
                        {
                            Assert.Contains("Backup:Schedule:PassphraseFile Is Not Set", notes, StringComparison.Ordinal);
                            Assert.Empty(await Engine(app).ListSnapshotsAsync());
                        }
                    }
                    finally
                    {
                        await GateDrive.ShutdownAsync(app);
                    }
                }
            }
            finally
            {
                System.IO.File.Delete(file);
            }
        }

        [Fact]
        public async Task A_due_schedule_is_taken_once_per_tick_with_two_tenants_and_the_passphrase_never_reaches_a_note()
        {
            using var scratch = await ScratchAsync();
            var app = await BootAsync(scratch);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var tenants = sp.GetRequiredService<ITenantRepository>();
                    var first = (await tenants.GetTenantsAsync()).Single(t => t.TenantId == alias.TenantId);
                    await tenants.AddTenantAsync(new Tenant { Name = "Second", DBConnectionString = first.DBConnectionString, DBType = first.DBType });
                    return 0;
                });
                var schedule = BackupScheduleAggregate.Create(24, 30, 3, enabled: true, DateTime.UtcNow);
                await Engine(app).SaveScheduleAsync(schedule);

                var notes = await TickAsync(app);
                output.WriteLine(notes);
                Assert.Equal(2, TenantsRun);
                var taken = Assert.Single(await Engine(app).ListSnapshotsAsync());
                Assert.Equal((BackupSnapshotStatus.Available, RetentionTag.Scheduled), (taken.Status, taken.RetentionTag));
                Assert.Equal(1, CountOf(notes, " Taken"));
                Assert.Equal(1, CountOf(notes, "Run On Tenant"));                     // the second tenant's turn defers - retention runs once too
                Assert.DoesNotContain(Passphrase, notes, StringComparison.Ordinal);
                Assert.NotNull(Assert.Single(await Engine(app).ListSchedulesAsync()).LastRunOnUtc);

                // Not due again until the interval has passed.
                await TickAsync(app);
                Assert.Single(await Engine(app).ListSnapshotsAsync());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_lease_skips_while_a_snapshot_is_being_taken_and_fails_an_abandoned_one()
        {
            using var scratch = await ScratchAsync();
            var app = await BootAsync(scratch);
            try
            {
                await Engine(app).SaveScheduleAsync(BackupScheduleAggregate.Create(24, 30, 3, enabled: true, DateTime.UtcNow));
                var inProgress = await Engine(app).StartSnapshotAsync(RetentionTag.Manual);
                var abandoned = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Scheduled, "10.2.2", DateTime.UtcNow - BackupScheduleJob.Lease - TimeSpan.FromMinutes(5));
                await CatalogueAsync(app, repository => repository.AddAsync(abandoned));

                var notes = await TickAsync(app);
                output.WriteLine(notes);
                Assert.Contains("Being Taken - Skipped", notes, StringComparison.Ordinal);
                Assert.Contains("1 Abandoned Snapshot(s) Failed", notes, StringComparison.Ordinal);
                var rows = await Engine(app).ListSnapshotsAsync();
                Assert.Equal(2, rows.Count);
                Assert.Equal((BackupSnapshotStatus.Failed, "abandoned"), (rows.Single(row => row.Id == abandoned.Id).Status, rows.Single(row => row.Id == abandoned.Id).FailureReason));
                Assert.Equal(BackupSnapshotStatus.Creating, rows.Single(row => row.Id == inProgress).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Retention_keeps_the_newest_scheduled_snapshots_and_never_the_operators_own()
        {
            using var scratch = await ScratchAsync();
            var app = await BootAsync(scratch);
            try
            {
                var now = DateTime.UtcNow;
                var schedule = BackupScheduleAggregate.Create(24, retentionDays: 7, keepLast: 2, enabled: true, now);
                schedule.MarkRun(now);                                               // not due: this tick is retention only
                await Engine(app).SaveScheduleAsync(schedule);

                var scheduled30 = await RowAsync(app, RetentionTag.Scheduled, now.AddDays(-30));
                var scheduled20 = await RowAsync(app, RetentionTag.Scheduled, now.AddDays(-20));
                var scheduled10 = await RowAsync(app, RetentionTag.Scheduled, now.AddDays(-10));
                var scheduledNow = await RowAsync(app, RetentionTag.Scheduled, now.AddHours(-1));
                var manual = await RowAsync(app, RetentionTag.Manual, now.AddDays(-30));
                var indefinite = await RowAsync(app, RetentionTag.Indefinite, now.AddDays(-30));
                var preRestore = await RowAsync(app, RetentionTag.PreRestore, now.AddDays(-10));
                var preUpgrade = await RowAsync(app, RetentionTag.PreUpgrade, now.AddDays(-10));           // BL-172
                var preUpgradeRecent = await RowAsync(app, RetentionTag.PreUpgrade, now.AddDays(-1));
                var failed = await RowAsync(app, RetentionTag.Scheduled, now.AddDays(-10), fail: true);

                var notes = await TickAsync(app);
                output.WriteLine(notes);
                Assert.Contains("5 Snapshot(s) Past Retention Deleted", notes, StringComparison.Ordinal);
                Assert.Equal(new[] { scheduled10, scheduledNow, manual, indefinite, preUpgradeRecent }.OrderBy(id => id), (await Engine(app).ListSnapshotsAsync()).Select(row => row.Id).OrderBy(id => id));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Queued_work_runs_in_its_own_scope_and_a_failed_item_does_not_stop_the_worker()
        {
            using var scratch = await ScratchAsync();
            var app = await BootAsync(scratch);
            try
            {
                var queue = app.Services.GetRequiredService<BackupWorkQueue>();
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(queue.TryEnqueue((_, _) => throw new InvalidOperationException("planted: this item fails")));
                Assert.True(queue.TryEnqueue((provider, _) => { done.SetResult(provider.GetService<BackupEngine>() is not null); return Task.CompletedTask; }));
                Assert.True(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private int TenantsRun { get; set; }

        private Task<ScratchGateInstall> ScratchAsync() =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true, ["Schedule"] = new JsonObject { ["Passphrase"] = Passphrase } } });

        private async Task<WebApplication> BootAsync(ScratchGateInstall scratch)
        {
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            return app;
        }

        /// <summary>One tick, as <c>HostedServiceBase</c> runs it: per tenant, its tenant scope, a fresh DI scope, SetTenant.</summary>
        private async Task<string> TickAsync(WebApplication app)
        {
            List<Tenant> tenants;
            using (var listing = app.Services.CreateScope())
            {
                tenants = (await listing.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
            }

            var notes = new StringBuilder();
            TenantsRun = 0;
            foreach (var tenant in tenants)
            {
                using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!.Value);
                using var jobScope = app.Services.CreateScope();
                jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes.Append(await new BackupScheduleJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider));
                TenantsRun++;
            }

            return notes.ToString();
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static async Task CatalogueAsync(WebApplication app, Func<IBackupSnapshotRepository, Task> action)
        {
            using var scope = app.Services.CreateScope();
            using var system = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().BeginScope(null);
            await action(scope.ServiceProvider.GetRequiredService<IBackupSnapshotRepository>());
        }

        private static async Task<Guid> RowAsync(WebApplication app, RetentionTag tag, DateTime createdOnUtc, bool fail = false)
        {
            var row = BackupSnapshotAggregate.Start(Guid.NewGuid(), tag, "10.2.2", createdOnUtc);
            if (fail)
            {
                row.Fail("IOException", createdOnUtc);
            }
            else
            {
                row.MarkCompleted(Checksum.Compute(Encoding.UTF8.GetBytes(row.Id.ToString())), 10, createdOnUtc);
            }

            await CatalogueAsync(app, repository => repository.AddAsync(row));
            return row.Id;
        }

        private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    }
}
