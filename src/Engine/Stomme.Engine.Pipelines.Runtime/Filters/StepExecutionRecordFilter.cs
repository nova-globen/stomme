using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// A specification to use when finding step execution log records. Only non-null fields will be included in the conditional expression.
/// </summary>
public class StepExecutionRecordFilter
{
    /// <summary>
    /// The ID of the step execution record.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The IDs of the step execution records.
    /// </summary>
    public ICollection<string>? Ids { get; set; }

    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// The IDs of the pipeline instances.
    /// </summary>
    public ICollection<string>? PipelineInstanceIds { get; set; }

    /// <summary>
    /// The ID of the step.
    /// </summary>
    public string? StepId { get; set; }

    /// <summary>
    /// The IDs of the steps.
    /// </summary>
    public ICollection<string>? StepIds { get; set; }

    /// <summary>
    /// The node ID of the step.
    /// </summary>
    public string? StepNodeId { get; set; }

    /// <summary>
    /// The node IDs of the steps.
    /// </summary>
    public ICollection<string>? StepNodeIds { get; set; }

    /// <summary>
    /// The name of the step.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The names of the steps.
    /// </summary>
    public ICollection<string>? Names { get; set; }

    /// <summary>
    /// The status of the step.
    /// </summary>
    public StepStatus? Status { get; set; }

    /// <summary>
    /// The statuses of the steps.
    /// </summary>
    public ICollection<StepStatus>? Statuses { get; set; }

    /// <summary>
    /// Whether to include completed step execution records. If not specified, all step execution records will be included.
    /// </summary>
    public bool? Completed { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this step execution.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The IDs of the step execution contexts that scheduled step executions.
    /// </summary>
    public ICollection<string>? SchedulingStepExecutionIds { get; set; }

    /// <summary>
    /// The ID of the step that scheduled this step execution (denormalized).
    /// </summary>
    public string? SchedulingStepId { get; set; }

    /// <summary>
    /// The IDs of the steps that scheduled step executions (denormalized).
    /// </summary>
    public ICollection<string>? SchedulingStepIds { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this step execution.
    /// Used for cross-pipeline call stack tracking.
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }

    /// <summary>
    /// The pipeline instance IDs of the pipelines that scheduled step executions.
    /// </summary>
    public ICollection<string>? SchedulingPipelineInstanceIds { get; set; }

    /// <summary>
    /// The call stack depth of the step execution.
    /// </summary>
    public int? CallStackDepth { get; set; }

    /// <summary>
    /// Returns true if the filter is empty.
    /// </summary>
    public bool IsEmpty =>
        Id == null
        && Ids == null
        && PipelineInstanceId == null
        && PipelineInstanceIds == null
        && StepId == null
        && StepIds == null
        && StepNodeId == null
        && StepNodeIds == null
        && Name == null
        && Names == null
        && Status == null
        && Statuses == null
        && Completed == null
        && SchedulingStepExecutionId == null
        && SchedulingStepExecutionIds == null
        && SchedulingStepId == null
        && SchedulingStepIds == null
        && SchedulingPipelineInstanceId == null
        && SchedulingPipelineInstanceIds == null
        && CallStackDepth == null;

    /// <summary>
    /// Applies the filter to the specified queryable.
    /// </summary>
    public IQueryable<StepExecutionRecord> Apply(IQueryable<StepExecutionRecord> queryable)
    {
        var filter = this;
        if (filter.Id != null) queryable = queryable.Where(x => x.Id == filter.Id);
        if (filter.Ids != null) queryable = queryable.Where(x => filter.Ids.Contains(x.Id));
        if (filter.PipelineInstanceId != null) queryable = queryable.Where(x => x.PipelineInstanceId == filter.PipelineInstanceId);
        if (filter.PipelineInstanceIds != null) queryable = queryable.Where(x => filter.PipelineInstanceIds.Contains(x.PipelineInstanceId));
        if (filter.StepId != null) queryable = queryable.Where(x => x.StepId == filter.StepId);
        if (filter.StepIds != null && filter.StepIds.Any()) queryable = queryable.Where(x => filter.StepIds.Contains(x.StepId));
        if (filter.StepNodeId != null) queryable = queryable.Where(x => x.StepNodeId == filter.StepNodeId);
        if (filter.StepNodeIds != null && filter.StepNodeIds.Any()) queryable = queryable.Where(x => filter.StepNodeIds.Contains(x.StepNodeId));
        if (filter.Name != null) queryable = queryable.Where(x => x.StepName == filter.Name);
        if (filter.Names != null && filter.Names.Any()) queryable = queryable.Where(x => filter.Names.Contains(x.StepName!));
        if (filter.Status != null) queryable = queryable.Where(x => x.Status == filter.Status);
        if (filter.Statuses != null && filter.Statuses.Any()) queryable = queryable.Where(x => filter.Statuses.Contains(x.Status));
        if (filter.Completed != null) queryable = filter.Completed == true ? queryable.Where(x => x.CompletedAt != null) : queryable.Where(x => x.CompletedAt == null);
        if (filter.SchedulingStepExecutionId != null) queryable = queryable.Where(x => x.SchedulingStepExecutionId == filter.SchedulingStepExecutionId);
        if (filter.SchedulingStepExecutionIds != null && filter.SchedulingStepExecutionIds.Any()) queryable = queryable.Where(x => x.SchedulingStepExecutionId != null && filter.SchedulingStepExecutionIds.Contains(x.SchedulingStepExecutionId));
        if (filter.SchedulingStepId != null) queryable = queryable.Where(x => x.SchedulingStepId == filter.SchedulingStepId);
        if (filter.SchedulingStepIds != null && filter.SchedulingStepIds.Any()) queryable = queryable.Where(x => x.SchedulingStepId != null && filter.SchedulingStepIds.Contains(x.SchedulingStepId));
        if (filter.SchedulingPipelineInstanceId != null) queryable = queryable.Where(x => x.SchedulingPipelineInstanceId == filter.SchedulingPipelineInstanceId);
        if (filter.SchedulingPipelineInstanceIds != null && filter.SchedulingPipelineInstanceIds.Any()) queryable = queryable.Where(x => x.SchedulingPipelineInstanceId != null && filter.SchedulingPipelineInstanceIds.Contains(x.SchedulingPipelineInstanceId));
        if (filter.CallStackDepth != null) queryable = queryable.Where(x => x.CallStackDepth == filter.CallStackDepth);

        return queryable;
    }
}