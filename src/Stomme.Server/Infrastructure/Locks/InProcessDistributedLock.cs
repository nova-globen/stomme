#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;

namespace Stomme.Infrastructure.Locks
{
    /// <summary>
    /// The SINGLE-NODE <see cref="IDistributedLock"/>: mutual exclusion within this process, and none across
    /// processes - stated, not implied. It is what an install gets whose store cannot coordinate nodes at all: a SQLite
    /// file (one node by construction), the File store, or a host that has not been installed yet.
    /// </summary>
    /// <remarks>
    /// A lease expires after its TTL like the store-backed locks' do, so a holder that never releases (a crashed scope)
    /// cannot wedge the key. The acquire is ONE atomic operation on the dictionary - <c>TryAdd</c> for a free key,
    /// <c>TryUpdate</c> against the exact expired lease for a stale one - so two acquires cannot both win, which is the
    /// property <c>DistributedLockConcurrencyTests</c> drives. Keyed statically: every host in a process shares the one
    /// table, as nodes would share a store.
    /// </remarks>
    public sealed class InProcessDistributedLock : IDistributedLock
    {
        private static readonly ConcurrentDictionary<string, Lease> Leases = new(StringComparer.Ordinal);

        public Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new Lease(Guid.NewGuid(), DateTime.UtcNow.Add(ttl));
            if (Leases.TryAdd(key, lease))
            {
                return Task.FromResult<ILockHandle?>(new Handle(key, lease));
            }

            // Held - unless the holder's lease has run out, in which case exactly one acquirer replaces THAT lease.
            if (Leases.TryGetValue(key, out var current) && current.ExpiresUtc <= DateTime.UtcNow && Leases.TryUpdate(key, lease, current))
            {
                return Task.FromResult<ILockHandle?>(new Handle(key, lease));
            }

            return Task.FromResult<ILockHandle?>(null);
        }

        private sealed record Lease(Guid Owner, DateTime ExpiresUtc);

        private sealed class Handle(string key, Lease lease) : ILockHandle
        {
            public string Key => key;

            // Removes the entry only if it is still THIS lease: a holder whose lease expired and was taken over must not
            // release its successor's.
            public ValueTask DisposeAsync()
            {
                Leases.TryRemove(new System.Collections.Generic.KeyValuePair<string, Lease>(key, lease));
                return ValueTask.CompletedTask;
            }
        }
    }
}
