using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

/// <summary>
/// Takes care of loading and persisting bookmarks.
/// </summary>
[Obsolete("This middleware is no longer used and will be removed in a future version. Bookmarks are now persisted through the commit state handler")]
public class PersistBookmarkMiddleware(PipelineMiddlewareDelegate next) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        await Next(context).ConfigureAwait(false);
        
        // Not used anymore.
        // var bookmarkRequest = new UpdateBookmarksRequest(context, context.BookmarksDiff, context.CorrelationId);
        // await _bookmarksPersister.PersistBookmarksAsync(bookmarkRequest);
    }
}