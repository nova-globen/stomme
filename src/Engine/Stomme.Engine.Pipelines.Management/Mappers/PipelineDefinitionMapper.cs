using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Materializers;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Mappers;

/// <summary>
/// Maps <see cref="PipelineDefinition"/> to and from <see cref="Pipeline"/> and <see cref="PipelineDefinitionModel"/>.
/// </summary>
public class PipelineDefinitionMapper
{
    private readonly IStepSerializer _stepSerializer;
    private readonly IPipelineDefinitionService _pipelineDefinitionService;
    private readonly VariableDefinitionMapper _variableDefinitionMapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineDefinitionMapper"/> class.
    /// </summary>
    public PipelineDefinitionMapper(IStepSerializer stepSerializer, IPipelineDefinitionService pipelineDefinitionService, VariableDefinitionMapper variableDefinitionMapper)
    {
        _stepSerializer = stepSerializer;
        _pipelineDefinitionService = pipelineDefinitionService;
        _variableDefinitionMapper = variableDefinitionMapper;
    }

    /// <summary>
    /// Maps a <see cref="PipelineDefinition"/> to a <see cref="Pipeline"/>.
    /// </summary>
    /// <param name="source">The source <see cref="PipelineDefinition"/>.</param>
    /// <returns>The mapped <see cref="Pipeline"/>.</returns>
    public Pipeline Map(PipelineDefinition source)
    {
        // NEW WAY: If OriginalSource is present, deserialize the full PipelineDefinitionModel
        // This enables symmetric round-trip without re-serialization
        if (!string.IsNullOrEmpty(source.OriginalSource))
        {
            var model = _stepSerializer.Deserialize<PipelineDefinitionModel>(source.OriginalSource);
            return MapFromModelWithIdentity(model, source);
        }

        // OLD WAY: Deserialize from StringData (backwards compatibility)
        var root = _stepSerializer.Deserialize(source.StringData!);

        return new(
            new(source.DefinitionId, source.Version, source.Id, source.TenantId),
            new(source.IsLatest, source.IsPublished),
            new(source.Name, source.Description, source.CreatedAt, source.ToolVersion),
            source.Options,
            root,
            source.Variables,
            source.Inputs,
            source.Outputs,
            source.Outcomes,
            source.CustomProperties,
            source.IsReadonly,
            source.IsSystem);
    }

    /// <summary>
    /// Maps a <see cref="PipelineDefinitionModel"/> to a <see cref="Pipeline"/>, using identity from stored definition.
    /// </summary>
    private Pipeline MapFromModelWithIdentity(PipelineDefinitionModel model, PipelineDefinition storedDefinition)
    {
        var root = model.Root!;
        var variables = _variableDefinitionMapper.Map(model.Variables).ToList();
        var options = model.Options ?? new PipelineOptions();

        return new(
            new(storedDefinition.DefinitionId, storedDefinition.Version, storedDefinition.Id, storedDefinition.TenantId),
            new(storedDefinition.IsLatest, storedDefinition.IsPublished),
            new(model.Name, model.Description, model.CreatedAt, model.ToolVersion),
            options,
            root,
            variables,
            model.Inputs ?? new List<InputDefinition>(),
            model.Outputs ?? new List<OutputDefinition>(),
            model.Outcomes ?? new List<string>(),
            model.CustomProperties ?? new Dictionary<string, object>(),
            storedDefinition.IsReadonly,
            storedDefinition.IsSystem);
    }

    /// <summary>
    /// Maps a <see cref="PipelineDefinitionModel"/> to a <see cref="Pipeline"/>.
    /// </summary>
    /// <param name="source">The source <see cref="PipelineDefinitionModel"/>.</param>
    /// <returns>The mapped <see cref="Pipeline"/>.</returns>
    public Pipeline Map(PipelineDefinitionModel source)
    {
        var root = source.Root!;
        var variables = _variableDefinitionMapper.Map(source.Variables).ToList();
        var options = source.Options ?? new PipelineOptions();

        // TODO: Remove this in the future when users have migrated pipelines to use the new UsableAsStep options property.

#pragma warning disable CS0618
        options.UsableAsStep ??= source.UsableAsStep ?? false;
#pragma warning restore CS0618

        return new(
            new(source.DefinitionId, source.Version, source.Id, source.TenantId),
            new(source.IsLatest, source.IsPublished),
            new(source.Name, source.Description, source.CreatedAt, source.ToolVersion),
            options,
            root,
            variables,
            source.Inputs ?? new List<InputDefinition>(),
            source.Outputs ?? new List<OutputDefinition>(),
            source.Outcomes ?? new List<string>(),
            source.CustomProperties ?? new Dictionary<string, object>(),
            source.IsReadonly,
            source.IsSystem);
    }
    
    public PipelineDefinition MapToPipelineDefinition(PipelineDefinitionModel source)
    {
        var root = source.Root!;
        var variables = _variableDefinitionMapper.Map(source.Variables).ToList();
        var options = source.Options ?? new PipelineOptions();
        var stringData = _stepSerializer.Serialize(root);

        return new()
        {
            IsPublished = source.IsPublished,
            Description = source.Description,
            Id = source.Id,
            Inputs = source.Inputs ?? [],
            Name = source.Name,
            Options = options,
            Outcomes = source.Outcomes ?? [],
            Outputs = source.Outputs ?? [],
            Variables = variables,
            Version = source.Version,
            CreatedAt = source.CreatedAt,
            CustomProperties = source.CustomProperties ?? new Dictionary<string, object>(),
            DefinitionId = source.DefinitionId,
            IsLatest = source.IsLatest,
            IsReadonly = source.IsReadonly,
            IsSystem = source.IsSystem,
            TenantId = source.TenantId,
            ToolVersion = source.ToolVersion,
            StringData = stringData,
            MaterializerName = JsonPipelineMaterializer.MaterializerName
        };
    }

    /// <summary>
    /// Maps many <see cref="PipelineDefinition"/>s to many <see cref="PipelineDefinitionModel"/>s.
    /// </summary>
    /// <param name="source">The source <see cref="PipelineDefinition"/>s.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The mapped <see cref="PipelineDefinitionModel"/>s.</returns>
    public async Task<IEnumerable<PipelineDefinitionModel>> MapAsync(IEnumerable<PipelineDefinition> source, CancellationToken cancellationToken = default)
    {
        return await Task.WhenAll(source.Select(async x => await MapAsync(x, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a <see cref="PipelineDefinition"/> to a <see cref="Pipeline"/>.
    /// </summary>
    /// <param name="pipelineDefinition">The source <see cref="PipelineDefinition"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mapped <see cref="Pipeline"/>.</returns>
    public async Task<PipelineDefinitionModel> MapAsync(PipelineDefinition pipelineDefinition, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await _pipelineDefinitionService.MaterializePipelineAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
        var pipeline = pipelineGraph.Pipeline;
        var variables = _variableDefinitionMapper.Map(pipeline.Variables).ToList();

        return new(
            pipelineDefinition.Id,
            pipelineDefinition.DefinitionId,
            pipelineDefinition.TenantId,
            pipelineDefinition.Name,
            pipelineDefinition.Description,
            pipelineDefinition.CreatedAt,
            pipelineDefinition.Version,
            pipelineDefinition.ToolVersion,
            variables,
            pipelineDefinition.Inputs,
            pipelineDefinition.Outputs,
            pipelineDefinition.Outcomes,
            pipelineDefinition.CustomProperties,
            pipelineDefinition.IsReadonly,
            pipelineDefinition.IsSystem,
            pipelineDefinition.IsLatest,
            pipelineDefinition.IsPublished,
            pipeline.Options,
            null,
            pipeline.Root);
    }

    /// <summary>
    /// Maps a <see cref="Pipeline"/> to a <see cref="PipelineDefinitionModel"/>.
    /// </summary>
    /// <param name="pipeline">The source <see cref="PipelineDefinition"/>.</param>
    /// <returns>The mapped <see cref="PipelineDefinitionModel"/>.</returns>
    public PipelineDefinitionModel Map(Pipeline pipeline)
    {
        var variables = _variableDefinitionMapper.Map(pipeline.Variables).ToList();

        return new(
            pipeline.Identity.Id,
            pipeline.Identity.DefinitionId,
            pipeline.Identity.TenantId,
            pipeline.PipelineMetadata.Name,
            pipeline.PipelineMetadata.Description,
            pipeline.PipelineMetadata.CreatedAt,
            pipeline.Identity.Version,
            pipeline.PipelineMetadata.ToolVersion,
            variables,
            pipeline.Inputs,
            pipeline.Outputs,
            pipeline.Outcomes,
            pipeline.CustomProperties,
            pipeline.IsReadonly,
            pipeline.IsSystem,
            pipeline.Publication.IsLatest,
            pipeline.Publication.IsPublished,
            pipeline.Options,
            null,
            pipeline.Root);
    }
}