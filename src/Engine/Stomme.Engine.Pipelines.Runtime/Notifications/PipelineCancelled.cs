using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

public record PipelineCancelled(string PipelineInstanceId) : INotification;