using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// A pipeline pipelineBuilder collects information about a pipeline to be built programmatically.
/// </summary>
public interface IPipelineBuilder
{
    /// <summary>
    /// The definition ID to use for the pipeline being built.
    /// </summary>
    string? DefinitionId { get; set; }
    
    /// <summary>
    /// The version ID to use for the pipeline being built.
    /// </summary>
    string? Id { get; set; }

    /// <summary>
    /// The ID of the tenant associated with the pipeline being built.
    /// </summary>
    string? TenantId { get; set; }

    /// <summary>
    /// The version of the pipeline being built.
    /// </summary>
    int Version { get; set; }
    
    /// <summary>
    /// The name of the pipeline.
    /// </summary>
    string? Name { get; set; }
    
    /// <summary>
    /// A description of the pipeline.
    /// </summary>
    string? Description { get; set; }
    
    /// <summary>
    /// PipelineDefinition is readonly.
    /// </summary>
    bool IsReadonly { get; set; }
    
    /// <summary>
    /// PipelineDefinition is readonly.
    /// </summary>
    bool IsSystem { get; set; }

    /// <summary>
    /// Options for the pipeline being built.
    /// </summary>
    PipelineOptions PipelineOptions { get; }
    
    /// <summary>
    /// The step to execute when the pipeline is run. 
    /// </summary>
    IStep? Root { get; set; }
    
    /// <summary>
    /// The pipeline variables to store with the pipeline being built.
    /// </summary>
    ICollection<Variable> Variables { get; set; }
    
    /// <summary>
    /// Gets or sets the inputs.
    /// </summary>
    ICollection<InputDefinition> Inputs { get; set; }
    
    /// <summary>
    /// Gets or sets the outputs.
    /// </summary>
    ICollection<OutputDefinition> Outputs { get; set; }
    
    /// <summary>
    /// Gets or sets the outcomes.
    /// </summary>
    ICollection<string> Outcomes { get; set; }

    /// <summary>
    /// An internal variable used to get and set the result of the pipeline.
    /// </summary>
    public Variable? Result { get; set; }

    /// <summary>
    /// A set of properties that can be used for storing application-specific information about the pipeline being built.
    /// </summary>
    [Obsolete("Use PropertyBag instead")]
    IDictionary<string, object> CustomProperties { get; }
    
    /// <summary>
    /// A fluent method for setting the <see cref="DefinitionId"/> property.
    /// </summary>
    /// <param name="definitionId">The definition ID to use for the pipeline being built.</param>
    IPipelineBuilder WithDefinitionId(string definitionId);

    /// <summary>
    /// A fluent method for setting the <see cref="Id"/> property.
    /// </summary>
    /// <param name="id">The unique identifier to use for the pipeline being built.</param>
    /// <returns>The current <see cref="IPipelineBuilder"/> instance for method chaining.</returns>
    IPipelineBuilder WithId(string id);

    /// <summary>
    /// A fluent method for setting the <see cref="TenantId"/> property.
    /// </summary>
    /// <param name="tenantId">The tenant ID to use for the pipeline being built.</param>
    IPipelineBuilder WithTenantId(string tenantId);

    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    Variable<T> WithVariable<T>();
    
    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    Variable<T> WithVariable<T>(string name, T value);
    
    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    Variable<T> WithVariable<T>(T value);
    
    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    IPipelineBuilder WithVariable<T>(Variable<T> variable);
    
    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    IPipelineBuilder WithVariable(Variable variable);
    
    /// <summary>
    /// A fluent method for adding a variable to <see cref="Variables"/>.
    /// </summary>
    IPipelineBuilder WithVariables(params Variable[] variables);
    
    /// <summary>
    /// A fluent method for adding an input to <see cref="Inputs"/>.
    /// </summary>
    InputDefinition WithInput<T>(string name, string? description = default);
    
    /// <summary>
    /// A fluent method for adding an input to <see cref="Inputs"/>.
    /// </summary>
    InputDefinition WithInput(string name, Type type, string? description = default);
    
    /// <summary>
    /// A fluent method for adding an input to <see cref="Inputs"/>.
    /// </summary>
    InputDefinition WithInput(string name, Type type, Action<InputDefinition>? setup = default);
    
    /// <summary>
    /// A fluent method for adding an input to <see cref="Inputs"/>.
    /// </summary>
    InputDefinition WithInput(Action<InputDefinition> setup);
    
    /// <summary>
    /// A fluent method for adding an input to <see cref="Inputs"/>.
    /// </summary>
    IPipelineBuilder WithInput(InputDefinition inputDefinition);
    
    /// <summary>
    /// A fluent method for adding an output to <see cref="Outputs"/>.
    /// </summary>
    OutputDefinition WithOutput<T>(string name, string? description = default);
    
    /// <summary>
    /// A fluent method for adding an output to <see cref="Outputs"/>.
    /// </summary>
    OutputDefinition WithOutput(string name, Type type, string? description = default);
    
    /// <summary>
    /// A fluent method for adding an output to <see cref="Outputs"/>.
    /// </summary>
    OutputDefinition WithOutput(string name, Type type, Action<OutputDefinition>? setup = default);
    
    /// <summary>
    /// A fluent method for adding an output to <see cref="Outputs"/>.
    /// </summary>
    OutputDefinition WithOutput(Action<OutputDefinition> setup);
    
    /// <summary>
    /// A fluent method for adding an output to <see cref="Outputs"/>.
    /// </summary>
    OutputDefinition WithOutput(OutputDefinition outputDefinition);

    /// <summary>
    /// A fluent method for adding a property to <see cref="CustomProperties"/>.
    /// </summary>
    IPipelineBuilder WithCustomProperty(string name, object value);

    /// <summary>
    /// Configure the pipeline to use the specified <see cref="IPipelineActivationStrategy"/> type.
    /// </summary>
    IPipelineBuilder WithActivationStrategyType<T>() where T : IPipelineActivationStrategy;

    /// <summary>
    /// Marks the pipeline as readonly.
    /// </summary>
    IPipelineBuilder AsReadonly();
    
    /// <summary>
    /// Marks the pipeline as a system pipeline.
    /// </summary>
    IPipelineBuilder AsSystemPipeline();

    /// <summary>
    /// Build a new <see cref="Pipeline"/> instance using the information collected in this pipelineBuilder.
    /// </summary>
    Task<Pipeline> BuildPipelineAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new <see cref="Pipeline"/> instance using the specified <see cref="IPipeline"/> definition.
    /// </summary>
    Task<Pipeline> BuildPipelineAsync(IPipeline pipelineDefinition, CancellationToken cancellationToken = default);
}