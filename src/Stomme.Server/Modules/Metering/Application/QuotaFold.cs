#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Serialises the read-modify-write of one quota counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cross-node lock already existed.</b> The design first proposed a new keyed in-process gate; the
    /// review pointed at <see cref="IDistributedLock"/>, which ships with a PostgreSQL advisory-lock and a
    /// RavenDB compare-exchange implementation and is consumed optionally elsewhere. Inventing a second mechanism
    /// beside it is exactly the "parallel, never extend" failure this absorption keeps guarding against.
    /// </para>
    /// <para>
    /// <b>What the lock is, per install.</b> The host registers ONE <see cref="IDistributedLock"/> that
    /// picks the install's primitive - PostgreSQL advisory, SQL Server app lock, RavenDB compare-exchange - and, on
    /// SQLite and the File store, the explicit single-node in-process lock. Where no lock is registered the fold
    /// runs on its in-process fallback. <see cref="IsCrossNode"/> says which is in
    /// force; where it is false the protection is within one host only - a RECORDED divergence, not a claim of safety,
    /// and the repair pass in the rollup job is what makes a missed update converge.
    /// </para>
    /// </remarks>
    public sealed class QuotaFold
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessGates = new(StringComparer.Ordinal);

        private readonly IDistributedLock? _distributed;

        public QuotaFold(IDistributedLock? distributed = null)
        {
            _distributed = distributed;
        }

        /// <summary>Whether a cross-node lock is in force (false = the in-process fallback, single host only).</summary>
        public bool IsCrossNode => _distributed switch
        {
            null => false,
            Stomme.Infrastructure.Locks.ConfiguredDistributedLock configured => configured.IsCrossNode,
            Stomme.Infrastructure.Locks.InProcessDistributedLock => false,
            _ => true,
        };

        /// <summary>How long a fold waits for the cross-node lock before running under the in-process gate alone.</summary>
        public static readonly TimeSpan DistributedWait = TimeSpan.FromSeconds(5);

        /// <summary>Runs <paramref name="fold"/> holding the gate for (site, metric).</summary>
        /// <remarks>
        /// <b>The in-process gate is taken FIRST, always, and the cross-node lock inside it.</b> The fallback
        /// used to be an ALTERNATIVE: a recorder that found the distributed lock held took the in-process gate instead
        /// and folded - while the recorder holding the distributed lock folded too, because the two paths excluded
        /// nothing from each other. Measured: a RavenDB gate run failed with a quota conflict out of this. Now every
        /// fold on this host passes the one local gate, and then waits a bounded time for the cross-node lock; only a
        /// lock held ELSEWHERE for longer than <see cref="DistributedWait"/> lets a fold run under the local gate alone,
        /// where the fold's own retry and the rollup job's repair pass make a missed update converge.
        /// </remarks>
        public async Task<T> HoldAsync<T>(int siteId, string metric, Func<Task<T>> fold, CancellationToken ct = default)
        {
            var key = $"metering:{siteId}:{metric}";
            var gate = InProcessGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (_distributed is null)
                {
                    return await fold();
                }

                ILockHandle? handle = null;
                var deadline = DateTime.UtcNow + DistributedWait;
                while (true)
                {
                    handle = await _distributed.AcquireAsync(key, TimeSpan.FromSeconds(30), ct);
                    if (handle is not null || DateTime.UtcNow >= deadline)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                }

                await using (handle)
                {
                    return await fold();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
