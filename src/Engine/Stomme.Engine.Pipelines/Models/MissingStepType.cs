namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a missing step type.
/// </summary>
public class MissingStepType
{
    /// <summary>
    /// The type name of the missing step type.
    /// </summary>
    public string TypeName { get; set; } = default!;
    
    /// <summary>
    /// The version of the missing step type.
    /// </summary>
    public int Version { get; set; }
    
    /// <summary>
    /// The original step JSON.
    /// </summary>
    public string StepJson { get; set; } = default!;
    
    /// <summary>
    /// The original step outcomes.
    /// </summary>
    public ICollection<string> Outcomes { get; set; } = new List<string>();
}