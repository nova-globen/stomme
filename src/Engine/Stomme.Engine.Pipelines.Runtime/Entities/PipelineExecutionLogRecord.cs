using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// Represents a pipeline execution log entry.
/// </summary>
public class PipelineExecutionLogRecord : Entity, ILogRecord
{
    /// <summary>
    /// The ID of the pipeline definition.
    /// </summary>
    public string PipelineDefinitionId { get; set; } = null!;
    
    /// <summary>
    /// The version ID of the pipeline definition.
    /// </summary>
    public string PipelineDefinitionVersionId { get; set; } = null!;
    
    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string PipelineInstanceId { get; set; } = null!;
    
    /// <summary>
    /// The version of the pipeline definition.
    /// </summary>
    public int PipelineVersion { get; set; }
    
    /// <summary>
    /// The ID of the step instance.
    /// </summary>
    public string StepInstanceId { get; set; } = null!;
    
    /// <summary>
    /// The ID of the parent step instance.
    /// </summary>
    public string? ParentStepInstanceId { get; set; }
    
    /// <summary>
    /// The ID of the step.
    /// </summary>
    public string StepId { get; set; } = null!;
    
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
    /// The unique ID of the node within the pipeline graph.
    /// </summary>
    public string StepNodeId { get; set; } = null!;

    /// <summary>
    /// The time stamp of the log entry.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }
    
    /// <summary>
    /// A local sequence number of the log entry within a batch of log entries. This is used to order log entries whose timestamps are (close to) identical.
    /// </summary>
    public long Sequence { get; set; }
    
    /// <summary>
    /// The name of the event.
    /// </summary>
    public string? EventName { get; set; }
    
    /// <summary>
    /// The message.
    /// </summary>
    public string? Message { get; set; }
    
    /// <summary>
    /// The source of the event.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// The state of the step at the time of the log entry.
    /// </summary>
    [Obsolete("Look at the StepExecutionRecord.StepState property instead.")]
    public IDictionary<string, object>? StepState { get; set; }
    
    /// <summary>
    /// Any additional payload associated with the log entry.
    /// </summary>
    public object? Payload { get; set; }
}