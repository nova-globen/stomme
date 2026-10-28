using System.ComponentModel;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Steps;

/// <summary>
/// Represents an executable process.
/// </summary>
[Browsable(false)]
[Step("Stomme", "Pipelines", "A pipeline is a step that executes its Root step.")]
[PublicAPI]
public class Pipeline : Composite<object>, ICloneable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Pipeline"/> class.
    /// </summary>
    public Pipeline(
        PipelineIdentity identity,
        PipelinePublication publication,
        PipelineMetadata pipelineMetadata,
        PipelineOptions options,
        IStep root,
        ICollection<Variable> variables,
        ICollection<InputDefinition> inputs,
        ICollection<OutputDefinition> outputs,
        ICollection<string> outcomes,
        IDictionary<string, object> customProperties,
        bool isReadonly,
        bool isSystem)
    {
        Identity = identity;
        Publication = publication;
        Inputs = inputs;
        Outputs = outputs;
        Outcomes = outcomes;
        PipelineMetadata = pipelineMetadata;
        Options = options;
        Variables = variables;
        CustomProperties = customProperties;
        Root = root;
        IsReadonly = isReadonly;
        IsSystem = isSystem;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Pipeline"/> class.
    /// </summary>
    public Pipeline(IStep root) : this()
    {
        Root = root;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Pipeline"/> class.
    /// </summary>
    public Pipeline()
    {
    }

    /// <summary>
    /// Gets or sets the pipeline identity.
    /// </summary>
    public PipelineIdentity Identity { get; set; } = PipelineIdentity.VersionOne;
    
    /// <summary>
    /// Gets or sets the publication status of the pipeline.
    /// </summary>
    public PipelinePublication Publication { get; set; } = PipelinePublication.LatestAndPublished;
    
    /// <summary>
    /// Gets or sets input definitions.
    /// </summary>
    public ICollection<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();
    
    /// <summary>
    /// Gets or sets output definitions.
    /// </summary>
    public ICollection<OutputDefinition> Outputs { get; set; } = new List<OutputDefinition>();
    
    /// <summary>
    /// Gets or sets possible outcomes for this pipeline.
    /// </summary>
    public ICollection<string> Outcomes { get; set; } = new List<string>();
    
    /// <summary>
    /// Gets or sets metadata about the pipeline.
    /// </summary>
    public PipelineMetadata PipelineMetadata { get; set; } = new();
    
    /// <summary>
    /// Gets or sets options for the pipeline.
    /// </summary>
    public PipelineOptions Options { get; set; } = new();
    
    /// <summary>
    /// Make pipeline definition readonly.
    /// </summary>
    public bool IsReadonly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline is a system pipeline.
    /// </summary>
    public bool IsSystem { get; }
    
    /// <summary>
    /// Returns the pipeline definition handle.
    /// </summary>
    public PipelineDefinitionHandle DefinitionHandle => PipelineDefinitionHandle.ByDefinitionVersionId(Identity.Id);

    /// <summary>
    /// Constructs a new <see cref="Pipeline"/> from the specified <see cref="IStep"/>.
    /// </summary>
    public static Pipeline FromStep(IStep root) => root as Pipeline ?? new(root);

    /// <summary>
    /// Creates a new memory register initialized with this pipeline's variables.
    /// </summary>
    public MemoryRegister CreateRegister()
    {
        return new MemoryRegister();
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        if(ResultVariable != null)
            context.PipelineExecutionContext.MemoryRegister.Declare(ResultVariable);

        await base.ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Create a shallow copy of this pipeline.
    /// </summary>
    public Pipeline Clone() => (Pipeline)((ICloneable)this).Clone();
    
    object ICloneable.Clone() => MemberwiseClone();
}