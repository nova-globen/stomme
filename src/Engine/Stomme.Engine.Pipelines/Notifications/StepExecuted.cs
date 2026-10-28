using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A notification that is sent when a step has executed.
/// </summary>
/// <param name="StepExecutionContext">The step execution context.</param>
public record StepExecuted(StepExecutionContext StepExecutionContext) : INotification;