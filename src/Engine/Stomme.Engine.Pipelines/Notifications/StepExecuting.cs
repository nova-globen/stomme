using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A notification that is sent when a step is about to execute.
/// </summary>
/// <param name="StepExecutionContext">The step execution context.</param>
public record StepExecuting(StepExecutionContext StepExecutionContext) : INotification;