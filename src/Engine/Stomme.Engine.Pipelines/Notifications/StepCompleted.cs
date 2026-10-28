using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A notification that is sent when a step has completed.
/// </summary>
/// <param name="StepExecutionContext">The step execution context.</param>
public record StepCompleted(StepExecutionContext StepExecutionContext) : INotification;