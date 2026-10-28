namespace Stomme.Engine.Pipelines.Runtime;

public interface IBookmarkQueuePurger
{
    Task PurgeAsync(CancellationToken cancellationToken = default);
}