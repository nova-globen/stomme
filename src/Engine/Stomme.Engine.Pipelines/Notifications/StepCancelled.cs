using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A notification that is sent when a step is cancelled.
/// </summary>
/// <param name="StepExecutionContext">The step execution context.</param>
public record StepCancelled(StepExecutionContext StepExecutionContext) : INotification;