using Stomme.Engine.Caching;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Microsoft.Extensions.Caching.Memory;

namespace Stomme.Engine.Pipelines.Runtime.Stores;

/// <summary>
/// A decorator for <see cref="ITriggerStore"/> that caches trigger records.
/// </summary>
public class CachingTriggerStore(ITriggerStore decoratedStore, ICacheManager cacheManager, IHasher hasher, ITenantAccessor tenantAccessor) : ITriggerStore
{
    /// <summary>
    /// The token key to evict all cached triggers.
    /// </summary>
    public static readonly string CacheInvalidationTokenKey = typeof(CachingTriggerStore).FullName!;

    /// <inheritdoc />
    public async ValueTask SaveAsync(StoredTrigger record, CancellationToken cancellationToken = default)
    {
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
        await decoratedStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SaveManyAsync(IEnumerable<StoredTrigger> records, CancellationToken cancellationToken = default)
    {
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
        await decoratedStore.SaveManyAsync(records, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StoredTrigger?> FindAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, async () => await decoratedStore.FindAsync(filter, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<StoredTrigger>> FindManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, async () => await decoratedStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))!;
    }

    public async ValueTask<Page<StoredTrigger>> FindManyAsync(TriggerFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, async () => await decoratedStore.FindManyAsync(filter, pageArgs, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))!;
    }

    public async ValueTask<Page<StoredTrigger>> FindManyAsync<TProp>(TriggerFilter filter, PageArgs pageArgs, StoredTriggerOrder<TProp> order, CancellationToken cancellationToken = default)
    {
        var cacheKey = hasher.Hash(filter);
        return (await GetOrCreateAsync(cacheKey, async () => await decoratedStore.FindManyAsync(filter, pageArgs, order, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask ReplaceAsync(IEnumerable<StoredTrigger> removed, IEnumerable<StoredTrigger> added, CancellationToken cancellationToken = default)
    {
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
        await decoratedStore.ReplaceAsync(removed, added, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<long> DeleteManyAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
    {
        await cacheManager.TriggerTokenAsync(CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
        return await decoratedStore.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
    {
        var tenantId = tenantAccessor.Tenant?.Id;
        var tenantIdPrefix = !string.IsNullOrEmpty(tenantId) ? $"{tenantId}:" : string.Empty;
        var internalKey = $"{tenantIdPrefix}{typeof(T).Name}:{key}";
        return await cacheManager.FindOrCreateAsync(internalKey, async entry =>
        {
            var invalidationRequestToken = cacheManager.GetToken(CacheInvalidationTokenKey);
            entry.AddExpirationToken(invalidationRequestToken);
            entry.SetAbsoluteExpiration(cacheManager.CachingOptions.Value.CacheDuration);
            return await factory().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}