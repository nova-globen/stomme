#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// The runs waiting for a decision ("Ask for approval"), oldest first - the site's, or one run's. Anyone who may view
    /// runs sees what is asked (the run's log names it already); the details only those who may decide or author.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.View)]
    public sealed record ListApprovalsQuery(string? RunId = null) : IQuery<IReadOnlyList<PipelineApprovalView>>;

    public sealed class ListApprovalsQueryHandler(IBookmarkStore bookmarks, IPipelineInstanceStore instances, IPipelineDefinitionStore definitions, IAccessGuard guard)
        : IQueryHandler<ListApprovalsQuery, IReadOnlyList<PipelineApprovalView>>
    {
        /// <summary>The most approvals one list shows.</summary>
        public const int MaxApprovals = 200;

        public async Task<IReadOnlyList<PipelineApprovalView>> Handle(ListApprovalsQuery request, CancellationToken ct)
        {
            var waiting = (await bookmarks.FindManyAsync(new BookmarkFilter
            {
                Name = PipelineApprovals.StepType,
                PipelineInstanceId = string.IsNullOrWhiteSpace(request.RunId) ? null : request.RunId,
            }, ct)).ToList();
            if (waiting.Count == 0)
            {
                return Array.Empty<PipelineApprovalView>();
            }

            var runIds = waiting.Select(bookmark => bookmark.PipelineInstanceId).Distinct(StringComparer.Ordinal).ToList();
            var runs = await instances.SummarizeManyAsync(
                new PipelineInstanceFilter { Ids = runIds },
                PageArgs.FromRange(0, runIds.Count),
                new PipelineInstanceOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending), ct);
            var definitionOf = runs.Items.ToDictionary(run => run.Id, run => run.DefinitionId, StringComparer.Ordinal);
            var names = await PipelineRunDetails.NamesAsync(definitions, ct);
            var detailed = await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Approve, ct)
                || await guard.IsAllowedAsync(null, global::Stomme.Pipelines.PipelinesPermissions.Manage, ct);

            // Only a run still going waits for anything: an older cancel left its approval stored (DueWaitsTask clears what is
            // left at its next tick).
            var going = runs.Items.Where(run => run.Status == PipelineStatus.Running).Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
            return waiting
                .Where(bookmark => going.Contains(bookmark.PipelineInstanceId))
                .Select(bookmark => (Bookmark: bookmark, Approval: AskForApproval.Of(bookmark.Payload)))
                .Where(pair => pair.Approval is not null)
                .Select(pair =>
                {
                    var approval = pair.Approval!;
                    var definitionId = definitionOf.TryGetValue(pair.Bookmark.PipelineInstanceId, out var id) ? id : null;
                    return new PipelineApprovalView
                    {
                        Id = pair.Bookmark.Id,
                        RunId = pair.Bookmark.PipelineInstanceId,
                        DefinitionId = definitionId,
                        PipelineName = definitionId is not null && names.TryGetValue(definitionId, out var name) ? name : null,
                        StepId = string.IsNullOrEmpty(approval.StepId) ? null : approval.StepId,
                        StepName = string.IsNullOrEmpty(approval.StepName) ? null : approval.StepName,
                        Question = approval.Question,
                        Details = detailed ? approval.Details : null,
                        RequestedOnUtc = (AskForApproval.MomentOf(approval.RequestedAt) ?? pair.Bookmark.CreatedAt).UtcDateTime,
                        ExpiresOnUtc = AskForApproval.MomentOf(approval.ExpiresAt)?.UtcDateTime,
                    };
                })
                .OrderBy(view => view.RequestedOnUtc)
                .Take(MaxApprovals)
                .ToList();
        }
    }

    /// <summary>
    /// A person's decision on a run waiting for approval: the run goes on down Approved or Rejected, with who decided and their
    /// comment as the step's result and a line of its log. An approval already decided - by someone else a moment earlier - or
    /// not on this site is null (the resumer's lock lets one decision through); one whose time to decide has passed is refused,
    /// as the run goes on down Expired at the next tick of the task that watches the time; one whose run has ended is refused
    /// too (an older cancel left it stored - the engine would not go on, and the decision answered "Approved").
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Approve)]
    public sealed record DecideApprovalCommand(string Id, bool Approve, string? Comment) : ICommand<PipelineApprovalDecided?>;

    public sealed class DecideApprovalCommandHandler(IBookmarkStore bookmarks, IPipelineInstanceStore instances, IPipelineResumer resumer, ICurrentUser user, ISystemClock clock, ILogger<DecideApprovalCommandHandler> logger)
        : ICommandHandler<DecideApprovalCommand, PipelineApprovalDecided?>
    {
        public async Task<PipelineApprovalDecided?> Handle(DecideApprovalCommand request, CancellationToken ct)
        {
            var comment = request.Comment?.Trim() ?? "";
            if (comment.Length > PipelineApprovals.MaxCommentLength)
            {
                throw new ArgumentException($"A comment is at most {PipelineApprovals.MaxCommentLength} characters.", nameof(request.Comment));
            }

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return null;
            }

            var bookmark = await bookmarks.FindAsync(new BookmarkFilter { BookmarkId = request.Id, Name = PipelineApprovals.StepType }, ct);
            if (bookmark is null)
            {
                return null;
            }

            if (await instances.FindAsync(new PipelineInstanceFilter { Id = bookmark.PipelineInstanceId }, ct) is { Status: PipelineStatus.Finished } ended)
            {
                throw new InvalidOperationException($"The run has ended ({PipelineRunStates.LabelOf(ended.SubStatus.ToString()).ToLowerInvariant()}); there is nothing left to decide.");
            }

            if (AskForApproval.MomentOf(AskForApproval.Of(bookmark.Payload)?.ExpiresAt) is { } expires && expires <= clock.UtcNow)
            {
                throw new InvalidOperationException($"The time to decide ended at {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC; the run goes on down Expired.");
            }

            var outcome = request.Approve ? PipelineApprovals.Approved : PipelineApprovals.Rejected;
            var by = string.IsNullOrWhiteSpace(user.UserName) ? "user " + user.UserId : user.UserName;
            var resumed = await resumer.ResumeAsync(
                new BookmarkFilter { BookmarkId = bookmark.Id },
                new ResumeBookmarkOptions { Input = AskForApproval.DecisionInput(outcome, by, comment) }, ct);
            if (!resumed.Any())
            {
                return null;
            }

            logger.LogInformation("Run {Run}'s approval {Approval} was {Outcome} by {User}.", bookmark.PipelineInstanceId, bookmark.Id, outcome, by);
            return new PipelineApprovalDecided { RunId = bookmark.PipelineInstanceId, Outcome = outcome };
        }
    }
}
