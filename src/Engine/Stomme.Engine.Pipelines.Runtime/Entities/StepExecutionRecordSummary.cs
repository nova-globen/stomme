using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// Represents a summarized view of a single step execution of a step instance.
/// </summary>
public class StepExecutionRecordSummary : Entity
{
    /// <summary>
    /// Gets or sets the pipeline instance ID.
    /// </summary>
    public string PipelineInstanceId { get; set; } = null!;

    /// <summary>
    /// Gets or sets the step ID.
    /// </summary>
    public string StepId { get; set; } = null!;

    /// <summary>
    /// Gets or sets the step node ID.
    /// </summary>
    public string StepNodeId { get; set; } = null!;

    /// <summary>
    /// The type of the step.
    /// </summary>
    public string StepType { get; set; } = null!;

    /// <summary>
    /// The version of the step type.
    /// </summary>
    public int StepTypeVersion { get; set; }

    /// <summary>
    /// The name of the step.
    /// </summary>
    public string? StepName { get; set; }

    /// <summary>
    /// Gets or sets the time at which the step execution began.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Gets or sets whether the step has any bookmarks.
    /// </summary>
    public bool HasBookmarks { get; set; }

    /// <summary>
    /// Gets or sets the status of the step.
    /// </summary>
    public StepStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a dictionary of key-value pairs representing additional metadata for the step execution record summary.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; set; }
    
    /// <summary>
    /// Gets or sets the aggregated count of faults encountered during the execution of the step instance and its descendants.
    /// </summary>
    public int AggregateFaultCount { get; set; }

    /// <summary>
    /// Gets or sets the time at which the step execution completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Returns a summary view of the specified <see cref="StepExecutionRecord"/>.
    /// </summary>
    public static StepExecutionRecordSummary FromRecord(StepExecutionRecord record)
    {
        return new()
        {
            Id = record.Id,
            PipelineInstanceId = record.PipelineInstanceId,
            StepId = record.StepId,
            StepNodeId = record.StepNodeId,
            StepType = record.StepType,
            StepTypeVersion = record.StepTypeVersion,
            StepName = record.StepName,
            StartedAt = record.StartedAt,
            HasBookmarks = record.HasBookmarks,
            Status = record.Status,
            AggregateFaultCount = record.AggregateFaultCount,
            Metadata = record.Metadata?.Count > 0 ? record.Metadata : null,
            CompletedAt = record.CompletedAt,
        };
    }
    
    /// <summary>
    /// Returns a summary view of the specified <see cref="StepExecutionRecord"/>.
    /// </summary>
    public static Expression<Func<StepExecutionRecord, StepExecutionRecordSummary>> FromRecordExpression()
    {
        return record => new()
        {
            Id = record.Id,
            PipelineInstanceId = record.PipelineInstanceId,
            StepId = record.StepId,
            StepNodeId = record.StepNodeId,
            StepType = record.StepType,
            StepTypeVersion = record.StepTypeVersion,
            StepName = record.StepName,
            StartedAt = record.StartedAt,
            HasBookmarks = record.HasBookmarks,
            Status = record.Status,
            AggregateFaultCount = record.AggregateFaultCount,
            Metadata = record.Metadata,
            CompletedAt = record.CompletedAt
        };
    }
}