using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Filters;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Deletes bookmarks for pipeline instances being deleted.
/// </summary>
[UsedImplicitly]
public class DeleteBookmarks(IBookmarkManager bookmarkManager) : INotificationHandler<PipelineInstancesDeleting>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineInstancesDeleting notification, CancellationToken cancellationToken)
    {
        await bookmarkManager.DeleteManyAsync(new()
        {
            PipelineInstanceIds = notification.Ids
        }, cancellationToken).ConfigureAwait(false);
    }
}