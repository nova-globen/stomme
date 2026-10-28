using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// An event that is published when pipeline execution log records are persisted.
/// </summary>
/// <param name="PipelineExecutionContext">The pipeline execution context.</param>
public record PipelineExecutionLogUpdated(PipelineExecutionContext PipelineExecutionContext) : INotification;