using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

public record PipelineStateCommitted(PipelineExecutionContext PipelineExecutionContext, PipelineState PipelineState, PipelineInstance PipelineInstance) : INotification;