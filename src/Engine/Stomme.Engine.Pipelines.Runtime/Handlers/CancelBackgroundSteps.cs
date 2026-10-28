using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Middleware.Steps;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// A handler that cancels background steps based on removed bookmarks.
/// </summary>
[UsedImplicitly]
public class CancelBackgroundSteps(IBackgroundStepScheduler backgroundStepScheduler) : INotificationHandler<PipelineBookmarksIndexed>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineBookmarksIndexed notification, CancellationToken cancellationToken)
    {
        var removedBookmarks = notification.IndexedPipelineBookmarks.RemovedBookmarks.Where(x => x.Name == BackgroundStepInvokerMiddleware.BackgroundStepBookmarkName);

        foreach (var removedBookmark in removedBookmarks)
        {
            var payload = removedBookmark.GetPayload<BackgroundStepStimulus>();
            if (payload.JobId != null)
                await backgroundStepScheduler.UnscheduledAsync(payload.JobId, cancellationToken).ConfigureAwait(false);
        }
    }
}