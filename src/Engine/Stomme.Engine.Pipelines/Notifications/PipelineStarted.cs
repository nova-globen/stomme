using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Notifications;

/// <summary>
/// A domain event that is published when a pipeline starts.
/// </summary>
public record PipelineStarted(Pipeline Pipeline, PipelineExecutionContext PipelineExecutionContext) : INotification;