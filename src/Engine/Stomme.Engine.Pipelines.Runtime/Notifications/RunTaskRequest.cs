using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A domain event that applications can subscribe to in order to handle tasks requested by a pipeline.
/// </summary>
/// <param name="StepExecutionContext">The context of the step that requested the task to run.</param>
/// <param name="TaskId">A unique identifier for an individual task request.</param>
/// <param name="TaskName">The name of the task requested to run.</param>
/// <param name="TaskPayload">Any additional parameters to send to the task.</param>
public record RunTaskRequest(StepExecutionContext StepExecutionContext, string TaskId, string TaskName, IDictionary<string, object>? TaskPayload) : INotification;