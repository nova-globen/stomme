using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Entities;

/// <summary>
/// Represents a versioned pipeline definition.
/// </summary>
public class PipelineDefinition : VersionedEntity
{
    /// <summary>
    /// The logical ID of the pipeline. This ID is the same across versions. 
    /// </summary>
    public string DefinitionId { get; set; } = null!;
    
    /// <summary>
    /// The name of the pipeline.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// A short description of what the pipeline is about.  
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The version of the tool that created this pipeline.
    /// </summary>
    public Version? ToolVersion { get; set; }

    /// <summary>
    /// A set of options for the pipeline.
    /// </summary>
    public PipelineOptions Options { get; set; } = new();
    
    /// <summary>
    /// A set of pipeline variables that are accessible throughout the pipeline.
    /// </summary>
    public ICollection<Variable> Variables { get; set; } = new List<Variable>();

    /// <summary>
    /// A set of input definitions.
    /// </summary>
    public ICollection<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();
    
    /// <summary>
    /// A set of output definitions.
    /// </summary>
    public ICollection<OutputDefinition> Outputs { get; set; } = new List<OutputDefinition>();
    
    /// <summary>
    /// A set of possible outcomes for this pipeline.
    /// </summary>
    public ICollection<string> Outcomes { get; set; } = new List<string>();
    
    /// <summary>
    /// Stores custom information about the pipeline. Can be used to store application-specific properties to associate with the pipeline.
    /// </summary>
    public IDictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// The name of the pipeline provider that created this pipeline, if any.
    /// </summary>
    public string? ProviderName { get; set; }
    
    /// <summary>
    /// The name of the pipeline materializer to interpret the <see cref="StringData"/> or <see cref="BinaryData"/>.
    /// </summary>
    public string MaterializerName { get; set; } = null!;

    /// <summary>
    /// Provider-specific data.
    /// </summary>
    public string? MaterializerContext { get; set; }

    /// <summary>
    /// A textual representation of the pipeline. The data is to be interpreted by the configured materializer.
    /// </summary>
    public string? StringData { get; set; }

    /// <summary>
    /// The original source representation of the pipeline (JSON, EngineScript, YAML, etc.).
    /// When present, materializers should prefer this over StringData for full round-trip fidelity.
    /// This field enables symmetric materialization without requiring serialization round-trips.
    /// </summary>
    public string? OriginalSource { get; set; }

    /// <summary>
    /// A binary representation of the pipeline. The data is to be interpreted by the configured materializer.
    /// </summary>
    public byte[]? BinaryData { get; set; }

    /// <summary>
    /// An option to use the pipeline as a readonly pipeline
    /// </summary>
    public bool IsReadonly { get; set; }
    
    /// <summary>
    /// Specifies whether the pipeline is a system pipeline.
    /// System pipelines are provided by modules and are not meant to be modified by users.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Creates and returns a shallow copy of the pipeline definition.
    /// </summary>
    public PipelineDefinition ShallowClone() => (PipelineDefinition)MemberwiseClone();
}