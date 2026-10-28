using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Dispatches a request for running a task.
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>
    /// Asynchronously publishes the specified event using the pipeline dispatcher.
    /// </summary>
    Task DispatchAsync(RunTaskRequest request, CancellationToken cancellationToken = default);
}