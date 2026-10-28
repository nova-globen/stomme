using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A notification that is published when a pipeline instance is being dispatched.
/// </summary>
public record PipelineInstanceDispatching(DispatchPipelineInstanceRequest Request) : INotification;
