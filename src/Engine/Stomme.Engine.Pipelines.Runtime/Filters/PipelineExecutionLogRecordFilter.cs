using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// A specification to use when finding pipeline execution log records. Only non-null fields will be included in the conditional expression.
/// </summary>
public class PipelineExecutionLogRecordFilter
{
    /// <summary>
    /// The ID of the pipeline execution log record.
    /// </summary>
    public string? Id { get; set; }
    
    /// <summary>
    /// The IDs of the pipeline execution log records.
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
    /// The ID of the parent step instance.
    /// </summary>
    public string? ParentStepInstanceId { get; set; }
    
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
    /// The step type to exclude.
    /// </summary>
    public string? ExcludeStepType { get; set; }
    
    /// <summary>
    /// The step types to exclude.
    /// </summary>
    public ICollection<string>? ExcludeStepTypes { get; set; }
    
    /// <summary>
    /// The name of the event.
    /// </summary>
    public string? EventName { get; set; }
    
    /// <summary>
    /// Match all of these event names.
    /// </summary>
    public ICollection<string>? EventNames { get; set; }
    
    /// <summary>
    /// Applies the filter to the specified queryable.
    /// </summary>
    public IQueryable<PipelineExecutionLogRecord> Apply(IQueryable<PipelineExecutionLogRecord> queryable)
    {
        var filter = this;
        if(filter.Id != null) queryable = queryable.Where(x => x.Id == filter.Id);
        if (filter.Ids != null) queryable = queryable.Where(x => filter.Ids.Contains(x.Id!));
        if (filter.PipelineInstanceId != null) queryable = queryable.Where(x => x.PipelineInstanceId == filter.PipelineInstanceId);
        if (filter.PipelineInstanceIds != null) queryable = queryable.Where(x => filter.PipelineInstanceIds.Contains(x.PipelineInstanceId!));
        if (filter.ParentStepInstanceId != null) queryable = queryable.Where(x => x.ParentStepInstanceId == filter.ParentStepInstanceId);
        if (filter.StepId != null) queryable = queryable.Where(x => x.StepId == filter.StepId);
        if (filter.StepIds != null) queryable = queryable.Where(x => filter.StepIds.Contains(x.StepId));
        if (filter.StepNodeId != null) queryable = queryable.Where(x => x.StepNodeId == filter.StepNodeId);
        if (filter.StepNodeIds != null) queryable = queryable.Where(x => filter.StepNodeIds.Contains(x.StepNodeId));
        if (filter.EventName != null) queryable = queryable.Where(x => x.EventName == filter.EventName);
        if (filter.EventNames != null) queryable = queryable.Where(x => filter.EventNames.Contains(x.EventName!));
        if(filter.ExcludeStepType != null) queryable = queryable.Where(x => x.StepType != filter.ExcludeStepType);
        if(filter.ExcludeStepTypes != null) queryable = queryable.Where(x => !filter.ExcludeStepTypes.Contains(x.StepType));
        
        return queryable;
    }
}