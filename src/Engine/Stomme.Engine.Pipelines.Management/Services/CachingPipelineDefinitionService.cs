using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// Decorates an <see cref="IPipelineDefinitionService"/> with caching capabilities.
/// </summary>
[UsedImplicitly]
public class CachingPipelineDefinitionService(
    IPipelineDefinitionService decoratedService, 
    IPipelineDefinitionCacheManager cacheManager, 
    IPipelineDefinitionStore pipelineDefinitionStore,
    IMaterializerRegistry materializerRegistry) : IPipelineDefinitionService
{
    /// <inheritdoc />
    public Task<PipelineGraph> MaterializePipelineAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        return decoratedService.MaterializePipelineAsync(definition, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var cacheKey = cacheManager.CreatePipelineDefinitionVersionCacheKey(definitionId, versionOptions);

        return await FindFromCacheAsync(cacheKey,
            () => decoratedService.FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken),
            x => x.DefinitionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var cacheKey = cacheManager.CreatePipelineDefinitionVersionCacheKey(definitionVersionId);
        return await FindFromCacheAsync(cacheKey,
            () => decoratedService.FindPipelineDefinitionAsync(definitionVersionId, cancellationToken),
            x => x.DefinitionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionHandle handle, CancellationToken cancellationToken = default)
    {
        var filter = handle.ToFilter();
        return FindPipelineDefinitionAsync(filter, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = cacheManager.CreatePipelineDefinitionFilterCacheKey(filter);
        return await FindFromCacheAsync(cacheKey,
            () => decoratedService.FindPipelineDefinitionAsync(filter, cancellationToken),
            x => x.DefinitionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var cacheKey = cacheManager.CreatePipelineVersionCacheKey(definitionId, versionOptions);
        return await FindFromCacheAsync(cacheKey,
            () => decoratedService.FindPipelineGraphAsync(definitionId, versionOptions, cancellationToken),
            x => x.Pipeline.Identity.DefinitionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var cacheKey = cacheManager.CreatePipelineVersionCacheKey(definitionVersionId);
        return await FindFromCacheAsync(cacheKey,
            () => decoratedService.FindPipelineGraphAsync(definitionVersionId, cancellationToken),
            x => x.Pipeline.Identity.DefinitionId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default)
    {
        var filter = definitionHandle.ToFilter();
        return FindPipelineGraphAsync(filter, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        // Resolve the definition first (already cached by FindPipelineDefinitionAsync) so the
        // PipelineGraph ends up under the stable per-version-ID key rather than a filter-hash key
        // that can never be shared with the other FindPipelineGraphAsync overloads.
        var definition = await FindPipelineDefinitionAsync(filter, cancellationToken).ConfigureAwait(false);
        return await FindPipelineGraphForDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineGraph>> FindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitions = await pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        var pipelineGraphs = new List<PipelineGraph>();
        foreach (var pipelineDefinition in pipelineDefinitions)
        {
            var pipelineGraph = await FindPipelineGraphForDefinitionAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
            if (pipelineGraph != null)
                pipelineGraphs.Add(pipelineGraph);
        }

        return pipelineGraphs;
    }

    /// <inheritdoc />
    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken).ConfigureAwait(false);
        var pipelineGraph = await FindPipelineGraphForDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        return new(definition, pipelineGraph);
    }

    /// <inheritdoc />
    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(definitionVersionId, cancellationToken).ConfigureAwait(false);
        var pipelineGraph = await FindPipelineGraphForDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        return new(definition, pipelineGraph);
    }

    /// <inheritdoc />
    public Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default)
    {
        var filter = definitionHandle.ToFilter();
        return TryFindPipelineGraphAsync(filter, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var definition = await FindPipelineDefinitionAsync(filter, cancellationToken).ConfigureAwait(false);
        var pipelineGraph = await FindPipelineGraphForDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        return new(definition, pipelineGraph);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineGraphFindResult>> TryFindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var pipelineDefinitions = await pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        var results = new List<PipelineGraphFindResult>();
        foreach (var pipelineDefinition in pipelineDefinitions)
        {
            var pipelineGraph = await FindPipelineGraphForDefinitionAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
            results.Add(new PipelineGraphFindResult(pipelineDefinition, pipelineGraph));
        }

        return results;
    }

    private async Task<T?> FindFromCacheAsync<T>(string cacheKey, Func<Task<T?>> getObjectFunc, Func<T, string> getChangeTokenKeyFunc) where T : class
    {
        var cache = cacheManager.Cache;
        return await cache.FindOrCreateAsync(cacheKey, async entry =>
        {
            entry.SetAbsoluteExpiration(cache.CachingOptions.Value.CacheDuration);
            var obj = await getObjectFunc().ConfigureAwait(false);

            if (obj != null)
            {
                var changeTokenKey = cacheManager.CreatePipelineDefinitionChangeTokenKey(getChangeTokenKeyFunc(obj));
                entry.AddExpirationToken(cache.GetToken(changeTokenKey));
            }

            return obj;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the <see cref="PipelineGraph"/> for the given definition, using the per-version-ID cache entry
    /// that is shared with <see cref="FindPipelineGraphAsync(string,CancellationToken)"/>.
    /// Returns <c>null</c> when the definition is <c>null</c> or its materializer is unavailable.
    /// </summary>
    private async Task<PipelineGraph?> FindPipelineGraphForDefinitionAsync(PipelineDefinition? definition, CancellationToken cancellationToken)
    {
        if (definition == null)
            return null;

        if (!materializerRegistry.IsMaterializerAvailable(definition.MaterializerName))
            return null;

        var cacheKey = cacheManager.CreatePipelineVersionCacheKey(definition.Id);
        var cache = cacheManager.Cache;
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.SetAbsoluteExpiration(cache.CachingOptions.Value.CacheDuration);
            var graph = await MaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
            var changeTokenKey = cacheManager.CreatePipelineDefinitionChangeTokenKey(graph.Pipeline.Identity.DefinitionId);
            entry.AddExpirationToken(cache.GetToken(changeTokenKey));
            return graph;
        }).ConfigureAwait(false);
    }

}