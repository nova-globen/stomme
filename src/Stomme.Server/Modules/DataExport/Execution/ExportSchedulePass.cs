#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Modules.DataExport.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>What one site's schedule pass did.</summary>
    public sealed record ExportScheduleResult(int Queued, int Paused, int ClaimedElsewhere);

    /// <summary>
    /// Runs a site's due export schedules (BL-95, D108's correction of 2026-10-05), in the export queue job's per-site scope
    /// - before the drain, so a run is rendered in the same tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>As the owner, re-authorised now.</b> The owner must still hold <see cref="ExportPermissions.Create"/> and the
    /// source's own permission, read from STORED rows (the same evaluator that re-authorises every job at render). If not,
    /// the schedule is PAUSED - never skipped silently forever - and the owner and the site's operators are told. The job a
    /// run queues is an ordinary export requested as the owner, so the render re-checks again.
    /// </para>
    /// <para>
    /// <b>The claim is the schedule's save.</b> <see cref="ExportScheduleAggregate.RecordRun"/> moves the next occurrence
    /// forward and the store's concurrency check refuses a second host making the same move; only then is the job added. A
    /// host that dies between the two loses one run, logged by nobody - the price of never queueing one occurrence twice.
    /// </para>
    /// <para>One schedule's failure is logged and contained; the next still runs.</para>
    /// </remarks>
    public sealed class ExportSchedulePass(
        IExportScheduleRepository schedules,
        IExportJobRepository jobs,
        IExportAuditRepository audit,
        ExportSourceRegistry sources,
        IStoredRowPermissionEvaluator evaluator,
        IUserRoleRepository userRoles,
        ExportReadyNotifier notifier,
        IExportClock clock,
        ILogger<ExportSchedulePass> logger)
    {
        public async Task<ExportScheduleResult> RunSiteAsync(int siteId, CancellationToken ct = default)
        {
            var now = clock.UtcNow;
            int queued = 0, paused = 0, claimedElsewhere = 0;

            foreach (var schedule in await schedules.ListDueAsync(siteId, now, ct))
            {
                try
                {
                    // One write to the schedule either way: the instance just listed, paused or claimed.
                    var reason = await RefusalAsync(schedule, siteId, ct);
                    if (reason != ExportSchedulePauseReason.None)
                    {
                        schedule.Pause(reason, now);
                        await schedules.UpdateAsync(schedule, ct);
                        paused++;
                        var recipients = (await userRoles.GetSiteOperatorIdsAsync(siteId)).Append(schedule.OwnerUserId).Distinct().ToList();
                        await notifier.NotifySchedulePausedAsync(schedule, recipients, ct);
                    }
                    else
                    {
                        var jobId = Guid.NewGuid();
                        schedule.RecordRun(jobId, now);
                        await schedules.UpdateAsync(schedule, ct); // the claim: a second host making the same move is refused here

                        var job = ExportJobAggregate.Request(
                            jobId, schedule.TenantId, siteId, schedule.OwnerUserId, schedule.SourceKey, schedule.Format,
                            schedule.ArgumentsJson, schedule.OptionsJson, now, schedule.Id);
                        await jobs.AddAsync(job, ct);
                        await audit.AddAsync(
                            ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, siteId, job.Id, schedule.OwnerUserId, ExportAuditAction.Requested, now), ct);
                        queued++;
                    }
                }
                catch (ConcurrencyConflictException)
                {
                    claimedElsewhere++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Export schedule {ScheduleId} of site {SiteId} could not run.", schedule.Id, siteId);
                }
            }

            return new ExportScheduleResult(queued, paused, claimedElsewhere);
        }

        private async Task<ExportSchedulePauseReason> RefusalAsync(ExportScheduleAggregate schedule, int siteId, CancellationToken ct)
        {
            if (!sources.TryGet(schedule.SourceKey, out var source))
            {
                return ExportSchedulePauseReason.SourceUnavailable;
            }

            // A source with no permission is refused by the registry; refuse here too rather than read "no requirement" as "allowed".
            var allowed = !string.IsNullOrWhiteSpace(source.RequiredPermission)
                && await evaluator.IsAuthorizedAsync(siteId, schedule.OwnerUserId, ExportPermissions.Create, ct)
                && await evaluator.IsAuthorizedAsync(siteId, schedule.OwnerUserId, source.RequiredPermission, ct);
            return allowed ? ExportSchedulePauseReason.None : ExportSchedulePauseReason.OwnerNotAllowed;
        }
    }
}
