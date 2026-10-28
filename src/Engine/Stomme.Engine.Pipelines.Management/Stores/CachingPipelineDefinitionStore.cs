using Stomme.Engine.Caching;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Stomme.Engine.Pipelines.Management.Stores;

/// <summary>
/// A decorator for <see cref="IPipelineDefinitionStore"/> that caches pipeline definitions.
/// </summary>
public class CachingPipelineDefinitionStore(IPipelineDefinitionStore decoratedStore, ICacheManager cacheManager, IHasher hasher, ITenantAccessor tenantAccessor) : IPipelineDefinitionStore
{
    private static readonly string CacheInvalidationTokenKey = typeof(CachingPipelineDefinitionStore).FullName!;

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.FindAsync(filter, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, order);
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.FindAsync(filter, order, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Page<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, pageArgs);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindManyAsync(filter, pageArgs, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<Page<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, order, pageArgs);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindManyAsync(filter, order, pageArgs, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineDefinition>> FindManyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, async () => (await decoratedStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList()).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineDefinition>> FindManyAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, order);
        return (await GetOrCreateAsync(cacheKey, async () => (await decoratedStore.FindManyAsync(filter, order, cancellationToken).ConfigureAwait(false)).ToList()).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, pageArgs);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindSummariesAsync(filter, pageArgs, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<Page<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, order, pageArgs);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindSummariesAsync(filter, order, pageArgs, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindSummariesAsync(filter, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineDefinitionSummary>> FindSummariesAsync<TOrderBy>(PipelineDefinitionFilter filter, PipelineDefinitionOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter, order);
        return (await GetOrCreateAsync(cacheKey, () => decoratedStore.FindSummariesAsync(filter, order, cancellationToken)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> FindLastVersionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken)
    {
        var cacheKey = hasher.Hash(filter);
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.FindLastVersionAsync(filter, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        await decoratedStore.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveManyAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken = default)
    {
        await decoratedStore.SaveManyAsync(definitions, cancellationToken).ConfigureAwait(false);
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var result = await decoratedStore.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(nameof(AnyAsync), filter);
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.AnyAsync(filter, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountDistinctAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(nameof(CountDistinctAsync));
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.CountDistinctAsync(cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> GetIsNameUnique(string name, string? definitionId = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(nameof(GetIsNameUnique), name, definitionId);
        return await GetOrCreateAsync(cacheKey, () => decoratedStore.GetIsNameUnique(name, definitionId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
    {
        var tenantId = tenantAccessor.TenantId;
        var tenantIdPrefix = !string.IsNullOrEmpty(tenantId) ? $"{tenantId}:" : string.Empty;
        var internalKey = $"{tenantIdPrefix}{typeof(T).Name}:{key}";
        return await cacheManager.FindOrCreateAsync(internalKey, async entry =>
        {
            var invalidationRequestToken = cacheManager.GetToken(CacheInvalidationTokenKey);
            entry.AddExpirationToken(invalidationRequestToken);
            entry.SetSlidingExpiration(cacheManager.CachingOptions.Value.CacheDuration);
            return await factory().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}