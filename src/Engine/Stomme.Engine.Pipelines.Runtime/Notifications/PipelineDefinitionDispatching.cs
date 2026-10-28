using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A notification that is published when a pipeline definition is being dispatched.
/// </summary>
public record PipelineDefinitionDispatching(DispatchPipelineDefinitionRequest Request) : INotification;
