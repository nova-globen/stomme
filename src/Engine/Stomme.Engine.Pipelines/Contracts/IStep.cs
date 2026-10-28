using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a step, which is an atomic unit of operation within a pipeline.
/// </summary>
public interface IStep
{
    /// <summary>
    /// An identifier that is unique within a collection of steps. 
    /// </summary>
    string Id { get; set; }
    
    /// <summary>
    /// An identifier that is unique within a the entire pipeline graph. 
    /// </summary>
    string NodeId { get; set; }
    
    /// <summary>
    /// An optional name by which the step can be referenced.
    /// </summary>
    string? Name { get; set; }
    
    /// <summary>
    /// The logical type name of the step.
    /// </summary>
    string Type { get; set; }
    
    /// <summary>
    /// The version of the step type.
    /// </summary>
    int Version { get; set; }

    /// <summary>
    /// A bag of properties that can be used by custom steps and other code such as middleware components to store additional values with the step.
    /// </summary>
    IDictionary<string, object> CustomProperties { get; set; }

    /// <summary>
    /// Synthetic properties are dynamic properties not found on the step class itself.
    /// </summary>
    [JsonIgnore]
    IDictionary<string, object> SyntheticProperties { get; set; }
    
    /// <summary>
    /// Metadata associated with the step. This is typically used by the pipeline designer to store information about the step.
    /// </summary>
    IDictionary<string, object> Metadata { get; set; }
    
    /// <summary>
    /// Returns a value indicating whether the step can execute.
    /// </summary>
    ValueTask<bool> CanExecuteAsync(StepExecutionContext context);
    
    /// <summary>
    /// Invoked when the step executes.
    /// </summary>
    ValueTask ExecuteAsync(StepExecutionContext context);
}