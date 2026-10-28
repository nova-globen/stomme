using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Cluster-wide <see cref="IDistributedLock"/> backed by RavenDB compare-exchange values. A lease
/// stores its expiry; acquisition succeeds only if the key is absent or its lease has expired.
/// </summary>
public sealed class RavenDistributedLock(IDocumentStore store) : IDistributedLock
{
    public async Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var exchangeKey = "locks/" + key;

        var existing = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<LockValue>(exchangeKey), token: cancellationToken);

        long index = 0;
        if (existing is not null)
        {
            if (existing.Value is not null && existing.Value.ExpiresUtcTicks > DateTime.UtcNow.Ticks)
            {
                return null; // currently held
            }

            index = existing.Index; // expired lease → overwrite at its index
        }

        var value = new LockValue(DateTime.UtcNow.Ticks + ttl.Ticks);
        var result = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<LockValue>(exchangeKey, value, index), token: cancellationToken);

        return result.Successful ? new Handle(store, exchangeKey, result.Index, key) : null;
    }

    internal sealed record LockValue(long ExpiresUtcTicks);

    private sealed class Handle(IDocumentStore store, string exchangeKey, long index, string key) : ILockHandle
    {
        private bool _released;

        public string Key => key;

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await store.Operations.SendAsync(new DeleteCompareExchangeValueOperation<LockValue>(exchangeKey, index));
        }
    }
}
