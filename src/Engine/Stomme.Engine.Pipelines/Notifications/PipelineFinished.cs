using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A domain event that is published when a pipeline finishes.
/// </summary>
public record PipelineFinished(Pipeline Pipeline, PipelineState PipelineState, PipelineExecutionContext PipelineExecutionContext) : INotification;