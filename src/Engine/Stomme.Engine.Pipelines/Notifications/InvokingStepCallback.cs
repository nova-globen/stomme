using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Notifications;

public record InvokingStepCallback(StepExecutionContext Parent, StepExecutionContext Child) : INotification;