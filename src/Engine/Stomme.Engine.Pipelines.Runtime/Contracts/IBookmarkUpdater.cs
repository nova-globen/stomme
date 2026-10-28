using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime;

public interface IBookmarkUpdater
{
    Task UpdateBookmarksAsync(UpdateBookmarksRequest request, CancellationToken cancellationToken = default);
}