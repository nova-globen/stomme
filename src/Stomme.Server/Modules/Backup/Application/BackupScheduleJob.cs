#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Domain;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Application
{
    /// <summary>
    /// Scheduled snapshots and retention. Installation-wide work on a per-tenant loop
    /// (<c>HostedServiceBase</c> calls this once per tenant): it acts ONCE per tick, on the lowest tenant's turn, and never
    /// enumerates tenants itself (refutation 7). Off twice by default: the Job row is created disabled, and
    /// <c>Backup:Enabled</c> must be on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lease.</b> A snapshot row Creating for less than <see cref="Lease"/> means one is being taken - by this node's last
    /// tick or another node - and the tick skips; a Creating row older than that was abandoned by a process that died, and
    /// is failed.
    /// </para>
    /// <para>
    /// <b>The passphrase</b> is <c>Backup:Schedule:Passphrase</c> - operator configuration, never a row, a note or a log
    /// line. Without it a due schedule is reported and nothing is taken.
    /// </para>
    /// <para>
    /// <b>Retention runs here, not through D104's <c>IRetentionPurger</c></b> (D106 item 8 named it): that seam is PER SITE,
    /// configured by a per-site setting in whole days; backup retention is installation-wide and configured per schedule
    /// (days AND keep-last-N) - drained per site, it would repeat one pass N times against one catalogue.
    /// </para>
    /// </remarks>
    public sealed class BackupScheduleJob : HostedServiceBase
    {
        public static readonly TimeSpan Lease = TimeSpan.FromHours(6);

        public BackupScheduleJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Backup Schedule Job";
            Frequency = "H";
            Interval = 1;
            IsEnabled = false;
            MaximumDuration = (int)Lease.TotalMinutes;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var options = provider.GetRequiredService<BackupOptions>();
            if (!options.Enabled)
            {
                return "Backup Is Disabled (" + BackupOptions.SectionName + ":Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var first = (await provider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Select(candidate => candidate.TenantId).DefaultIfEmpty().Min();
            if (tenant is null || tenant.TenantId != first)
            {
                return $"Backup: Installation-Wide, Run On Tenant {first}'s Turn<br />";
            }

            var engine = provider.GetRequiredService<BackupEngine>();
            var logger = provider.GetRequiredService<ILogger<BackupScheduleJob>>();
            var stopping = provider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            var now = DateTime.UtcNow;
            var log = new StringBuilder();

            var abandoned = await engine.FailAbandonedAsync(Lease, now);
            if (abandoned > 0)
            {
                log.Append("Backup: ").Append(abandoned).Append(" Abandoned Snapshot(s) Failed<br />");
            }

            var due = (await engine.ListSchedulesAsync(stopping)).Where(schedule => schedule.IsDue(now)).ToList();
            if (due.Count > 0)
            {
                var passphrase = options.Schedule.ResolvePassphrase();
                if (BackupEngine.PassphraseProblem(passphrase) is not null)
                {
                    log.Append("Backup: A Schedule Is Due, But ").Append(options.Schedule.SettingName).Append(" Is Not Set (12+ Characters) - No Snapshot Taken<br />");
                }
                else if (await engine.IsSnapshotInProgressAsync(Lease, now))
                {
                    log.Append("Backup: A Snapshot Is Being Taken - Skipped<br />");
                }
                else
                {
                    try
                    {
                        var id = await engine.CreateSnapshotAsync(passphrase, RetentionTag.Scheduled, stopping);
                        log.Append("Backup: Snapshot ").Append(id).Append(" Taken<br />");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "The scheduled backup failed.");
                        log.Append("Backup: The Scheduled Snapshot Failed (").Append(ex.GetType().Name).Append(")<br />");
                    }

                    foreach (var schedule in due)
                    {
                        schedule.MarkRun(now);
                        await engine.SaveScheduleAsync(schedule, stopping);
                    }
                }
            }

            var removed = await engine.ApplyRetentionAsync(now, stopping);
            if (removed > 0)
            {
                log.Append("Backup: ").Append(removed).Append(" Snapshot(s) Past Retention Deleted<br />");
            }

            return log.Length == 0 ? "Backup: Nothing Due<br />" : log.ToString();
        }
    }

    /// <summary>
    /// Long backup work handed off by a request (a snapshot, a pre-flight and staging): held IN MEMORY only - the operator's
    /// passphrase travels in the closure and is never written down. Bounded; a full queue refuses rather than grows.
    /// </summary>
    public sealed class BackupWorkQueue
    {
        private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
            Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(new BoundedChannelOptions(8) { SingleReader = true });

        public bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> work) => _channel.Writer.TryWrite(work);

        internal ChannelReader<Func<IServiceProvider, CancellationToken, Task>> Reader => _channel.Reader;
    }

    /// <summary>Runs queued backup work, one item at a time, each in a scope of its own. A failure is logged, never fatal.</summary>
    public sealed class BackupWorker(BackupWorkQueue queue, IServiceScopeFactory scopes, ILogger<BackupWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
                {
                    using var scope = scopes.CreateScope();
                    try
                    {
                        await work(scope.ServiceProvider, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "A queued backup operation failed.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // the host is stopping
            }
        }
    }
}
