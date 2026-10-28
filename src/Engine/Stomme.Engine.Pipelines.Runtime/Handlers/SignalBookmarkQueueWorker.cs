using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Signals the bookmark queue worker to process any queued work.
/// </summary>
[UsedImplicitly]
public class SignalBookmarkQueueWorker(IBookmarkQueueSignaler signaler) : INotificationHandler<PipelineBookmarksIndexed>, INotificationHandler<BookmarkSaved>, INotificationHandler<PipelineInstanceSaved>
{
    public Task HandleAsync(BookmarkSaved notification, CancellationToken cancellationToken)
    {
        return Trigger();
    }

    public Task HandleAsync(PipelineBookmarksIndexed notification, CancellationToken cancellationToken)
    {
        return Trigger();
    }

    public Task HandleAsync(PipelineInstanceSaved notification, CancellationToken cancellationToken)
    {
        return Trigger();
    }

    private async Task Trigger()
    {
        await signaler.TriggerAsync().ConfigureAwait(false);
    }
}