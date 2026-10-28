using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Exceptions;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineDefinitionService(
    IPipelineDefinitionStore pipelineDefinitionStore,
    IPipelineGraphBuilder pipelineGraphBuilder,
    IMaterializerRegistry materializerRegistry,
    ILogger<PipelineDefinitionService> logger)
    : IPipelineDefinitionService
{
    /// <inheritdoc />
    public async Task<PipelineGraph> MaterializePipelineAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var materializer = materializerRegistry.GetMaterializer(definition.MaterializerName);

        if (materializer == null)
            throw new PipelineMaterializerNotFoundException(definition.MaterializerName);

        var pipeline = await materializer.MaterializeAsync(definition, cancellationToken).ConfigureAwait(false);
        return await pipelineGraphBuilder.BuildAsync(pipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var handle = PipelineDefinitionHandle.ByDefinitionId(definitionId, versionOptions);
        return FindPipelineDefinitionAsync(handle, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var handle = PipelineDefinitionHandle.ByDefinitionVersionId(definitionVersionId);
        return FindPipelineDefinitionAsync(handle, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionHandle handle, CancellationToken cancellationToken = default)
    {
        var filter = handle.ToFilter();
        return await pipelineDefinitionStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        return await pipelineDefinitionStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken).ConfigureAwait(false);
        return await TryMaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionVersionId, cancellationToken).ConfigureAwait(false);
        return await TryMaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionHandle, cancellationToken).ConfigureAwait(false);
        return await TryMaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(filter, cancellationToken).ConfigureAwait(false);
        return await TryMaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineGraph>> FindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitions = await pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        var pipelineGraphs = new List<PipelineGraph>();
        foreach (var pipelineDefinition in pipelineDefinitions)
        {
            var pipelineGraph = await MaterializePipelineAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
            pipelineGraphs.Add(pipelineGraph);
        }

        return pipelineGraphs;
    }

    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken).ConfigureAwait(false);
        return await MaterializePipelineGraphFindResultAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionVersionId, cancellationToken).ConfigureAwait(false);
        return await MaterializePipelineGraphFindResultAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionHandle, cancellationToken).ConfigureAwait(false);
        return await MaterializePipelineGraphFindResultAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(filter, cancellationToken).ConfigureAwait(false);
        return await MaterializePipelineGraphFindResultAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IEnumerable<PipelineGraphFindResult>> TryFindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitions = await pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        var results = new List<PipelineGraphFindResult>();
        foreach (var pipelineDefinition in pipelineDefinitions)
        {
            var result = await MaterializePipelineGraphFindResultAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Attempts to materialize a pipeline graph from the given pipeline definition if a suitable materializer is available.
    /// </summary>
    /// <param name="definition">The pipeline definition to materialize. Can be null.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="PipelineGraph"/> if materialization is successful; otherwise, null.
    /// </returns>
    private async Task<PipelineGraph?> TryMaterializePipelineAsync(PipelineDefinition? definition, CancellationToken cancellationToken)
    {
        if (definition == null)
            return null;

        if (materializerRegistry.IsMaterializerAvailable(definition.MaterializerName))
            return await MaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);

        logger.LogWarning("Materializer '{MaterializerName}' not found. The pipeline definition will not be materialized.", definition.MaterializerName);
        return null;
    }

    /// <summary>
    /// Attempts to materialize a pipeline graph from the given pipeline definition.
    /// </summary>
    /// <param name="definition">The pipeline definition to materialize the graph for. May be null.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A result containing the materialized pipeline graph and its corresponding pipeline definition, if successfully materialized; otherwise, returns the definition with a null graph.</returns>
    private async Task<PipelineGraphFindResult> MaterializePipelineGraphFindResultAsync(PipelineDefinition? definition, CancellationToken cancellationToken)
    {
        if (definition == null)
            return new(null, null);

        if (materializerRegistry.IsMaterializerAvailable(definition.MaterializerName))
        {
            var graph = await MaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
            return new(definition, graph);
        }

        return new(definition, null);
    }
}