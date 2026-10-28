using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management.Entities;

/// <summary>
/// Represents a pipeline instance.
/// </summary>
public class PipelineInstance : Entity
{
    /// <summary>
    /// The ID of the pipeline definition.
    /// </summary>
    public string DefinitionId { get; set; } = null!;
    
    /// <summary>
    /// The version ID of the pipeline definition.
    /// </summary>
    public string DefinitionVersionId { get; set; } = null!;
    
    /// <summary>
    /// The version of the pipeline definition.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// The ID of the parent pipeline.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }
    
    /// <summary>
    /// The state of the pipeline instance.
    /// </summary>
    public PipelineState PipelineState { get; set; } = null!;
    
    /// <summary>
    /// The status of the pipeline instance.
    /// </summary>
    public PipelineStatus Status { get; set; }
    
    /// <summary>
    /// The sub-status of the pipeline instance.
    /// </summary>
    public PipelineSubStatus SubStatus { get; set; }
    
    /// <summary>
    /// Gets or sets a value indicating whether the pipeline instance is actively executing. 
    /// </summary>
    /// <remarks>
    /// This flag is set to <c>true</c> immediately before the pipeline begins execution 
    /// and is set to <c>false</c> once the execution is completed. 
    /// It can be used to determine if a pipeline instance was in-progress in case of unexpected 
    /// application termination, allowing the system to retry execution upon restarting. 
    /// </remarks>
    public bool IsExecuting { get; set; }
    
    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string? CorrelationId { get; set; }
    
    /// <summary>
    /// The name of the pipeline instance.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// The number of incidents that have occurred during execution, if any.
    /// </summary>
    public int IncidentCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline instance is a system pipeline.
    /// </summary>
    public bool IsSystem { get; set; }
    
    /// <summary>
    /// The timestamp when the pipeline instance was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    
    /// <summary>
    /// The timestamp when the pipeline instance was last executed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
    
    /// <summary>
    /// The timestamp when the pipeline instance was finished.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }
}