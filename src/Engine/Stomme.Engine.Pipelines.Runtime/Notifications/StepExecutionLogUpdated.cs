using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// An event that is published when step executions are persisted.
/// </summary>
/// <param name="PipelineExecutionContext">The pipeline execution context.</param>
/// <param name="Records">The step execution records.</param>
public record StepExecutionLogUpdated(PipelineExecutionContext PipelineExecutionContext, ICollection<StepExecutionRecord> Records) : INotification;