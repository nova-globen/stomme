using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

public record BackgroundStepExecutionCompleted(StepExecutionContext StepExecutionContext) : INotification;