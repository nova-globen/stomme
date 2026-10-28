using Stomme.Engine.Common.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Entities;

/// <summary>
/// Represents a trigger associated with a pipeline definition.
/// </summary>
public class StoredTrigger : Entity
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
    /// The name of the trigger, if any. Typically assigned a stimulus name.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// The ID of the step associated with the trigger.
    /// </summary>
    public string StepId { get; set; } = null!;
    
    /// <summary>
    /// The hash of the trigger.
    /// </summary>
    public string? Hash { get; set; }
    
    /// <summary>
    /// The payload of the trigger.
    /// </summary>
    public object? Payload { get; set; }
}