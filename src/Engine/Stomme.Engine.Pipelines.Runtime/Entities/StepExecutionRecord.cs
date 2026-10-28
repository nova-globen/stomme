using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// Represents a single step execution of a step instance.
/// </summary>
public partial class StepExecutionRecord : Entity, ILogRecord
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
    /// The state of the step at the time this record is created or last updated.
    /// </summary>
    public IDictionary<string, object?>? StepState { get; set; }

    /// <summary>
    /// Any additional payload associated with the log record.
    /// </summary>
    public IDictionary<string, object>? Payload { get; set; }

    /// <summary>
    /// Any outputs provided by the step.
    /// </summary>
    public IDictionary<string, object?>? Outputs { get; set; }

    /// <summary>
    /// Any properties provided by the step.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// Lightweight metadata associated with the step execution.
    /// This information will be retained as part of the step execution summary record.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the exception that occurred during the step execution.
    /// </summary>
    public ExceptionState? Exception { get; set; }

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
    /// Gets or sets the aggregated count of faults encountered during the execution of the step instance and its descendants.
    /// </summary>
    public int AggregateFaultCount { get; set; }

    /// <summary>
    /// Gets or sets the time at which the step execution completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this step execution.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The ID of the step that scheduled this step execution (denormalized for convenience).
    /// </summary>
    public string? SchedulingStepId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this step execution.
    /// This is set when crossing pipeline boundaries (e.g., via ExecutePipeline or DispatchPipeline).
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }

    /// <summary>
    /// The depth of this step in the call stack (0 for root steps).
    /// Calculated by traversing the SchedulingStepExecutionId chain until reaching null.
    /// </summary>
    public int? CallStackDepth { get; set; }
}

public partial class StepExecutionRecord
{
    [NotMapped] [JsonIgnore] public StepExecutionRecordSnapshot? SerializedSnapshot { get; set; }
}