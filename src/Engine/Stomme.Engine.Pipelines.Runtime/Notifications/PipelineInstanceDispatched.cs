using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A notification that is published when a pipeline instance has been dispatched.
/// </summary>
public record PipelineInstanceDispatched(DispatchPipelineInstanceRequest Request, DispatchPipelineResponse Response) : INotification;
