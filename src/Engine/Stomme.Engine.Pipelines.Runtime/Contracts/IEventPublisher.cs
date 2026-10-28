using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Publishes events using the pipeline runtime, effectively triggering all <see cref="Event"/> steps.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes the specified event.
    /// </summary>
    Task PublishAsync(string eventName, string? correlationId = null, string? pipelineInstanceId = null, string? stepInstanceId = null, object? payload = null, bool asynchronous = false, CancellationToken cancellationToken = default);
}