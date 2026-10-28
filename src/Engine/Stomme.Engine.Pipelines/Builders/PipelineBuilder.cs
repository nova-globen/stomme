using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Builders;

/// <inheritdoc />
public class PipelineBuilder(IStepVisitor stepVisitor, IIdentityGraphService identityGraphService, IStepRegistry stepRegistry)
    : IPipelineBuilder
{
    /// <inheritdoc />
    public string? Id { get; set; }

    /// <inheritdoc />
    public string? DefinitionId { get; set; }

    /// <inheritdoc />
    public string? TenantId { get; set; }

    /// <inheritdoc />
    public int Version { get; set; } = 1;

    /// <inheritdoc />
    public string? Name { get; set; }

    /// <inheritdoc />
    public string? Description { get; set; }

    /// <inheritdoc />
    public bool IsReadonly { get; set; }

    /// <inheritdoc />
    public bool IsSystem { get; set; }

    /// <inheritdoc />
    public IStep? Root { get; set; }

    /// <inheritdoc />
    public ICollection<Variable> Variables { get; set; } = new List<Variable>();

    /// <inheritdoc />
    public ICollection<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();

    /// <inheritdoc />
    public ICollection<OutputDefinition> Outputs { get; set; } = new List<OutputDefinition>();

    /// <inheritdoc />
    public ICollection<string> Outcomes { get; set; } = new List<string>();

    /// <inheritdoc />
    public Variable? Result { get; set; } = new();

    /// <inheritdoc />
    public IDictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();

    /// <inheritdoc />
    public PipelineOptions PipelineOptions { get; } = new();

    /// <inheritdoc />
    public IPipelineBuilder WithDefinitionId(string definitionId)
    {
        DefinitionId = definitionId;
        return this;
    }
    
    public IPipelineBuilder WithId(string id)
    {
        Id = id;
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithTenantId(string tenantId)
    {
        TenantId = tenantId;
        return this;
    }

    /// <inheritdoc />
    [Obsolete("Use the overload that takes a name instead. This overload will be removed in a future version.")]
    public Variable<T> WithVariable<T>()
    {
        var variable = new Variable<T>(null!, default!);
        Variables.Add(variable);
        variable.WithPipelineStorage();
        variable.Id = null!; // This ensures that a deterministic ID is assigned by the builder.  
        return variable;
    }

    /// <inheritdoc />
    public Variable<T> WithVariable<T>(string name, T value)
    {
        var variable = new Variable<T>(name, value);
        Variables.Add(variable);
        return variable;
    }

    /// <inheritdoc />
    [Obsolete("Use the overload that takes a name instead. This overload will be removed in a future version.")]
    public Variable<T> WithVariable<T>(T value)
    {
        var variable = WithVariable<T>();
        variable.Value = value;
        return variable;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithVariable<T>(Variable<T> variable)
    {
        Variables.Add(variable);
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithVariable(Variable variable)
    {
        Variables.Add(variable);
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithVariables(params Variable[] variables)
    {
        foreach (var variable in variables)
            Variables.Add(variable);
        return this;
    }

    /// <inheritdoc />
    public InputDefinition WithInput<T>(string name, string? description = default)
    {
        return WithInput(name, typeof(T), description);
    }

    /// <inheritdoc />
    public InputDefinition WithInput(string name, Type type, string? description = default)
    {
        return WithInput(inputDefinition =>
        {
            inputDefinition.Name = name;
            inputDefinition.Type = type;

            if (description != null)
                inputDefinition.Description = description;
        });
    }

    /// <inheritdoc />
    public InputDefinition WithInput(string name, Type type, Action<InputDefinition>? setup = default)
    {
        return WithInput(inputDefinition =>
        {
            inputDefinition.Name = name;
            inputDefinition.Type = type;
            setup?.Invoke(inputDefinition);
        });
    }

    /// <inheritdoc />
    public InputDefinition WithInput(Action<InputDefinition> setup)
    {
        var inputDefinition = new InputDefinition();
        setup(inputDefinition);
        Inputs.Add(inputDefinition);
        return inputDefinition;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithInput(InputDefinition inputDefinition)
    {
        Inputs.Add(inputDefinition);
        return this;
    }

    public OutputDefinition WithOutput<T>(string name, string? description = default)
    {
        return WithOutput(name, typeof(T), description);
    }

    public OutputDefinition WithOutput(string name, Type type, string? description = default)
    {
        return WithOutput(outputDefinition =>
        {
            outputDefinition.Name = name;
            outputDefinition.Type = type;

            if (description != null)
                outputDefinition.Description = description;
        });
    }

    public OutputDefinition WithOutput(string name, Type type, Action<OutputDefinition>? setup = default)
    {
        return WithOutput(outputDefinition =>
        {
            outputDefinition.Name = name;
            outputDefinition.Type = type;
            setup?.Invoke(outputDefinition);
        });
    }

    public OutputDefinition WithOutput(Action<OutputDefinition> setup)
    {
        var outputDefinition = new OutputDefinition();
        setup(outputDefinition);
        return WithOutput(outputDefinition);
    }

    public OutputDefinition WithOutput(OutputDefinition outputDefinition)
    {
        Outputs.Add(outputDefinition);
        return outputDefinition;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithCustomProperty(string name, object value)
    {
        CustomProperties[name] = value;
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithActivationStrategyType<T>() where T : IPipelineActivationStrategy
    {
        PipelineOptions.ActivationStrategyType = typeof(T);
        return this;
    }

    /// <summary>
    /// Marks the pipeline as readonly.
    /// </summary>
    public IPipelineBuilder AsReadonly()
    {
        IsReadonly = true;
        return this;
    }

    /// <summary>
    ///   Marks the pipeline as a system pipeline.
    ///   System pipelines are not visible in the UI by default and are not meant to be modified by users.
    /// </summary>
    public IPipelineBuilder AsSystemPipeline()
    {
        IsSystem = true;
        return this;
    }

    /// <inheritdoc />
    public async Task<Pipeline> BuildPipelineAsync(CancellationToken cancellationToken = default)
    {
        var definitionId = string.IsNullOrEmpty(DefinitionId) ? string.Empty : DefinitionId;
        var id = string.IsNullOrEmpty(Id) ? string.Empty : Id;
        var tenantId = string.IsNullOrEmpty(TenantId) ? null : TenantId;
        var root = Root ?? new Sequence();
        var identity = new PipelineIdentity(definitionId, Version, id, tenantId);
        var publication = PipelinePublication.LatestAndPublished;
        var name = string.IsNullOrEmpty(Name) ? definitionId : Name;
        var pipelineMetadata = new PipelineMetadata(name, Description);
        var pipeline = new Pipeline(identity, publication, pipelineMetadata, PipelineOptions, root, Variables, Inputs, Outputs, Outcomes, CustomProperties, IsReadonly, IsSystem);

        // If a Result variable is defined, install it into the pipeline, so we can capture the output into it.
        if (Result != null)
        {
            pipeline.ResultVariable = Result;
            pipeline.Result = new Output<object>(Result);
        }

        var graph = await stepVisitor.VisitAsync(pipeline, cancellationToken).ConfigureAwait(false);
        var nodes = graph.Flatten().ToList();

        // Register all step types first. The identity graph service will need to know about all step types.
        var distinctStepTypes = nodes.Select(x => x.Step.GetType()).Distinct().ToList();
        await stepRegistry.RegisterAsync(distinctStepTypes, cancellationToken).ConfigureAwait(false);

        // Assign identities to all steps.
        await identityGraphService.AssignIdentitiesAsync(nodes).ConfigureAwait(false);

        // Give unnamed variables in each variable container a predictable name.
        var variableContainers = nodes.Where(x => x.Step is IVariableContainer).Select(x => (IVariableContainer)x.Step).ToList();

        foreach (var container in variableContainers)
        {
            var index = 0;
            var unnamedVariables = container.Variables.Where(x => string.IsNullOrWhiteSpace(x.Name)).ToList();

            foreach (var unnamedVariable in unnamedVariables)
                unnamedVariable.Name = $"Variable_{index++}";
        }

        return pipeline;
    }

    /// <inheritdoc />
    public async Task<Pipeline> BuildPipelineAsync(IPipeline pipelineDefinition, CancellationToken cancellationToken = default)
    {
        DefinitionId = pipelineDefinition.GetType().Name;
        await pipelineDefinition.BuildAsync(this, cancellationToken).ConfigureAwait(false);
        return await BuildPipelineAsync(cancellationToken).ConfigureAwait(false);
    }
}