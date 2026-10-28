using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A notification that is published when a pipeline definition has been dispatched.
/// </summary>
public record PipelineDefinitionDispatched(DispatchPipelineDefinitionRequest Request, DispatchPipelineResponse Response) : INotification;
