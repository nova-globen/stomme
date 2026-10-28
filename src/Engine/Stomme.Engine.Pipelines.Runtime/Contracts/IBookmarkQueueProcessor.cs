namespace Stomme.Engine.Pipelines.Runtime;

public interface IBookmarkQueueProcessor
{
    Task ProcessAsync(CancellationToken cancellationToken = default);
}