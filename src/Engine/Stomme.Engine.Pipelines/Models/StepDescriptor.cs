using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// A descriptor of a step type. It also provides a constructor to create instances of this type.
/// </summary>
[DebuggerDisplay("{TypeName}")]
public class StepDescriptor
{
    public string? TenantId { get; set; } // Null means tenant-agnostic.
    
    /// <summary>
    /// The fully qualified name of the step type.
    /// </summary>
    public string TypeName { get; set; } = null!;

    /// <summary>
    /// The .NET type of the step type.
    /// </summary>
    public Type ClrType { get; set; } = null!;

    /// <summary>
    /// The namespace of the step type.
    /// </summary>
    public string Namespace { get; set; } = null!;
    
    /// <summary>
    /// The name of the step type.
    /// </summary>
    public string Name { get; set; } = null!;
    
    /// <summary>
    /// The version of the step type.
    /// </summary>
    public int Version { get; set; }
    
    /// <summary>
    /// The category of the step type.
    /// </summary>
    public string Category { get; set; } = null!;
    
    /// <summary>
    /// The display name of the step type.
    /// </summary>
    public string? DisplayName { get; set; }
    
    /// <summary>
    /// The description of the step type.
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// The input properties of the step type.
    /// </summary>
    public ICollection<InputDescriptor> Inputs { get; init; } = new List<InputDescriptor>();
    
    /// <summary>
    /// The output properties of the step type.
    /// </summary>
    public ICollection<OutputDescriptor> Outputs { get; init; } = new List<OutputDescriptor>();
    
    /// <summary>
    /// The attributes of the step type.
    /// </summary>
    [JsonIgnore] public ICollection<Attribute> Attributes { get; set; } = new List<Attribute>();

    /// <summary>
    /// Instantiates a concrete instance of an <see cref="IStep"/>.
    /// </summary>
    [JsonIgnore]
    public Func<StepConstructorContext, StepConstructionResult> Constructor { get; set; } = null!;

    /// <summary>
    /// The kind of step.
    /// </summary>
    public StepKind Kind { get; set; } = StepKind.Action;
    
    /// <summary>
    /// Whether the step should be executed asynchronously. Applies only when the Kind is set to Task and as a default when not set on the step itself.
    /// </summary>
    public bool RunAsynchronously { get; set; }
    
    /// <summary>
    /// The ports of the step type.
    /// </summary>
    public ICollection<Port> Ports { get; set; } = new List<Port>();
    
    /// <summary>
    /// The custom properties of the step type.
    /// </summary>
    public IDictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();
    
    /// <summary>
    /// The properties to set when constructing a step in the designer.
    /// </summary>
    public IDictionary<string, object> ConstructionProperties { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// A value indicating whether this step is a container of child steps.
    /// </summary>
    public bool IsContainer { get; set; }

    /// <summary>
    /// Whether this step type is selectable from step pickers.
    /// </summary>
    public bool IsBrowsable { get; set; } = true;

    /// <summary>
    /// Whether this step type is a start step.
    /// </summary>
    public bool IsStart { get; set; }
    
    /// <summary>
    /// Whether this step type is a terminal step.
    /// </summary>
    public bool IsTerminal { get; set; }

    /// <summary>
    /// Gets or sets a function that allows configuring the JsonSerializerOptions for the step during serialization.
    /// </summary>
    /// <remarks>
    /// This function can be used to customize the serialization options for a step. It receives a JsonSerializerOptions
    /// object as an argument and should return the modified JsonSerializerOptions.
    /// <para>Example:</para>
    /// <code>
    /// stepDescriptor.ConfigureSerializerOptions = options =>
    /// {
    /// options.Converters.Add(new JsonIgnoreCompositeRootConverterFactory());
    /// return options;
    /// };
    /// </code>
    /// </remarks>
    [JsonIgnore]
    public Func<JsonSerializerOptions, JsonSerializerOptions>? ConfigureSerializerOptions { get; set; }
}