#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Triggers
{
    /// <summary>
    /// Resumes the runs whose <see cref="Wait"/> is due (BL-229, n8n's Wait node), the runs whose
    /// <see cref="AskForApproval"/> nobody decided in its time (down Expired), and those whose <see cref="WaitForCall"/> was not
    /// called in its time (down Timed out): a recurring task the ENGINE runs per site
    /// tenant, on one node at a time (<see cref="SingleNodeTaskAttribute"/>), like the schedules. Each tick lists the waiting
    /// steps' bookmarks, and resumes each one whose moment has come through the engine's resumer - under its lock, so a bookmark
    /// resumed by two ticks (or by a tick and a person's decision) runs once (the second finds it gone). A wait whose moment
    /// cannot be read is resumed rather than left waiting forever, with a warning; an approval with no time to decide waits for
    /// its decision. One that fails to resume is logged and tried again at the next tick. A wait whose run has ended is cleared
    /// rather than resumed (<see cref="ClearEndedAsync"/>).
    /// </summary>
    [SingleNodeTask]
    public sealed class DueWaitsTask(
        IBookmarkStore bookmarks,
        IBookmarkManager bookmarkManager,
        IPipelineInstanceStore instances,
        IPipelineResumer resumer,
        ISystemClock clock,
        ITenantAccessor tenantAccessor,
        ILogger<DueWaitsTask> logger) : RecurringTask
    {
        /// <summary>The waits the task watches: a Wait, an approval, a wait for a call, a wait for a form.</summary>
        public static readonly string[] Kinds = { Wait.StepTypeName, PipelineApprovals.StepType, PipelineCalls.WaitStepType, WaitForForm.StepTypeName };

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var now = clock.UtcNow;
            var stored = await ClearEndedAsync((await bookmarks.FindManyAsync(new BookmarkFilter { Names = Kinds }, cancellationToken).ConfigureAwait(false)).ToList(), cancellationToken).ConfigureAwait(false);
            var waiting = stored.Where(bookmark => bookmark.Name == Wait.StepTypeName).ToList();
            foreach (var bookmark in waiting)
            {
                var due = Wait.DueOf(bookmark.Payload);
                if (due is null)
                {
                    logger.LogWarning("A waiting run's bookmark {Bookmark} on tenant {Tenant} carries no moment the task can read; it is resumed now.", bookmark.Id, tenantAccessor.TenantId);
                }
                else if (due.Value > now)
                {
                    continue;
                }

                try
                {
                    var resumed = await resumer.ResumeAsync(new BookmarkFilter { BookmarkId = bookmark.Id }, null, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("A wait on tenant {Tenant} was due ({Due:O}); run {Run} resumed ({Count}).", tenantAccessor.TenantId, due, bookmark.PipelineInstanceId, resumed.Count());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A due wait on tenant {Tenant} (run {Run}, bookmark {Bookmark}) could not be resumed; the next tick tries again.", tenantAccessor.TenantId, bookmark.PipelineInstanceId, bookmark.Id);
                }
            }

            var approvals = stored.Where(bookmark => bookmark.Name == PipelineApprovals.StepType).ToList();
            foreach (var bookmark in approvals)
            {
                if (AskForApproval.MomentOf(AskForApproval.Of(bookmark.Payload)?.ExpiresAt) is not { } expires || expires > now)
                {
                    continue;
                }

                try
                {
                    var resumed = await resumer.ResumeAsync(
                        new BookmarkFilter { BookmarkId = bookmark.Id },
                        new ResumeBookmarkOptions { Input = AskForApproval.DecisionInput(PipelineApprovals.Expired, null, null) },
                        cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("An approval on tenant {Tenant} expired ({Expires:O}); run {Run} goes on down Expired ({Count}).", tenantAccessor.TenantId, expires, bookmark.PipelineInstanceId, resumed.Count());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "An expired approval on tenant {Tenant} (run {Run}, bookmark {Bookmark}) could not be resumed; the next tick tries again.", tenantAccessor.TenantId, bookmark.PipelineInstanceId, bookmark.Id);
                }
            }

            // BL-229: a wait for a call that timed out goes on down Timed out (its moment is in the bookmark's metadata, as its
            // stimulus must stay the token's hash alone).
            var calls = stored.Where(bookmark => bookmark.Name == PipelineCalls.WaitStepType).ToList();
            foreach (var bookmark in calls)
            {
                if (WaitForCall.ExpiresOf(bookmark.Metadata) is not { } expires || expires > now)
                {
                    continue;
                }

                try
                {
                    var resumed = await resumer.ResumeAsync(new BookmarkFilter { BookmarkId = bookmark.Id }, new ResumeBookmarkOptions { Input = WaitForCall.TimedOutInput() }, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("A wait for a call on tenant {Tenant} timed out ({Expires:O}); run {Run} goes on down Timed out ({Count}).", tenantAccessor.TenantId, expires, bookmark.PipelineInstanceId, resumed.Count());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A timed-out wait for a call on tenant {Tenant} (run {Run}, bookmark {Bookmark}) could not be resumed; the next tick tries again.", tenantAccessor.TenantId, bookmark.PipelineInstanceId, bookmark.Id);
                }
            }

            // BL-229: a wait for a form that nobody filled in in time goes on down Timed out, the same way.
            var forms = stored.Where(bookmark => bookmark.Name == WaitForForm.StepTypeName).ToList();
            foreach (var bookmark in forms)
            {
                if (WaitForCall.ExpiresOf(bookmark.Metadata) is not { } expires || expires > now)
                {
                    continue;
                }

                try
                {
                    var resumed = await resumer.ResumeAsync(new BookmarkFilter { BookmarkId = bookmark.Id }, new ResumeBookmarkOptions { Input = WaitForCall.TimedOutInput() }, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("A wait for a form on tenant {Tenant} timed out ({Expires:O}); run {Run} goes on down Timed out ({Count}).", tenantAccessor.TenantId, expires, bookmark.PipelineInstanceId, resumed.Count());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A timed-out wait for a form on tenant {Tenant} (run {Run}, bookmark {Bookmark}) could not be resumed; the next tick tries again.", tenantAccessor.TenantId, bookmark.PipelineInstanceId, bookmark.Id);
                }
            }
        }

        // BL-229: a run that has ended waits for nothing. A cancel used to leave its waits stored (the engine's client clears them
        // with the run now): an approval listed as waiting, a form page open, and a due wait resumed again at every tick, the
        // engine refusing each time (measured). Each wait whose run is found FINISHED is cleared and left out - never one whose
        // run is not found, as a run's waits may be stored a moment before the run itself.
        private async Task<List<StoredBookmark>> ClearEndedAsync(List<StoredBookmark> stored, CancellationToken cancellationToken)
        {
            if (stored.Count == 0)
            {
                return stored;
            }

            var runIds = stored.Select(bookmark => bookmark.PipelineInstanceId).Distinct(StringComparer.Ordinal).ToList();
            var ended = (await instances.FindManyIdsAsync(new PipelineInstanceFilter { Ids = runIds, PipelineStatus = PipelineStatus.Finished }, cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            var left = stored.Where(bookmark => ended.Contains(bookmark.PipelineInstanceId)).ToList();
            if (left.Count == 0)
            {
                return stored;
            }

            await bookmarkManager.DeleteManyAsync(new BookmarkFilter { BookmarkIds = left.Select(bookmark => bookmark.Id).ToList() }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Cleared {Count} wait(s) left by runs that have ended on tenant {Tenant}: {Runs}.", left.Count, tenantAccessor.TenantId, string.Join(", ", left.Select(bookmark => bookmark.PipelineInstanceId).Distinct(StringComparer.Ordinal)));
            return stored.Except(left).ToList();
        }
    }
}
