using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// A best-effort, cache-backed startup mutex for the install/upgrade path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the limits before relying on this.</b> It is not a general distributed lock and does not claim to
    /// be one:
    /// </para>
    /// <para>
    /// <b>1. On a single node it GRANTS unconditionally.</b> With <c>Caching:ScaleOut</c> off, or no
    /// distributed-cache connection string, there is nothing to coordinate with and every acquire succeeds. That
    /// is the intended behaviour for the overwhelmingly common deployment, and it is stated here because a
    /// caller reading "lock" is entitled to know it is a no-op on their install.
    /// </para>
    /// <para>
    /// <b>2. Even with scale-out, the acquire is NOT ATOMIC</b> (measured by reading, BL-108). It does a
    /// <c>Get</c> and then a <c>Set</c>, and <see cref="IDistributedCache"/> has no conditional write:
    /// <c>Set</c> OVERWRITES. Two instances that miss the same instant therefore both proceed, and the
    /// <c>try/catch</c> around the write cannot save it — the old comment claimed a failure there meant another
    /// instance had won, which is not how <c>Set</c> behaves. It narrows the window; it does not close it. A
    /// real mutex needs a store primitive with a conditional write, which is what the unused
    /// <c>IDistributedLock</c> abstraction (<c>PostgresAdvisoryLock</c>, <c>RavenDistributedLock</c>) exists
    /// for; converging the two is booked as BL-154.
    /// </para>
    /// <para>
    /// <b>3. The jitter is now actually taken.</b> It used to call <c>Task.Delay(...)</c> without awaiting it,
    /// so the anti-thundering-herd delay did nothing whatever. Awaiting it is why this API is asynchronous.
    /// </para>
    /// </remarks>
    public interface IDistributedLockManager
    {
        /// <summary>
        /// Tries to take the lock. Returns <c>true</c> on a single-node install ALWAYS — see the remarks on
        /// <see cref="DistributedLockManager"/> before treating a <c>true</c> as exclusion.
        /// </summary>
        Task<bool> TryAcquireLockAsync(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default);

        Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default);
    }

    /// <inheritdoc cref="IDistributedLockManager"/>
    public class DistributedLockManager : IDistributedLockManager
    {
        /// <summary>The widest jitter before an acquire attempt, to desynchronise instances starting together.</summary>
        internal static readonly TimeSpan MaximumJitter = TimeSpan.FromSeconds(2);

        private readonly IDistributedCache _cache;
        private readonly IConfigManager _config;

        public DistributedLockManager(IDistributedCache cache, IConfigManager config)
        {
            _cache = cache;
            _config = config;
        }

        public async Task<bool> TryAcquireLockAsync(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default)
        {
            // Nothing to coordinate with: no scale-out, or no distributed cache behind it. Granting is correct -
            // and it happens BEFORE the jitter, so a single-node install pays nothing for a lock it does not need.
            if (!ScaleOutConfigured())
            {
                return true;
            }

            // Random delay so instances starting together do not all check-and-set on the same tick. This is
            // AWAITED now; it used to be a discarded Task.Delay, which is to say it did nothing (BL-108).
            await Task.Delay(Random.Shared.Next(0, (int)MaximumJitter.TotalMilliseconds), cancellationToken).ConfigureAwait(false);

            var bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                return false; // somebody holds it
            }

            // NOT atomic - see limit 2 in the remarks. Two instances can both reach here.
            await _cache.SetAsync(
                key,
                Encoding.UTF8.GetBytes(value),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration },
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        public async Task ReleaseLockAsync(string key, CancellationToken cancellationToken = default)
        {
            if (ScaleOutConfigured())
            {
                await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether there is anything to coordinate WITH. Release used to test only the connection string while
        /// acquire tested the flag as well, so with the string set and the flag off a release removed a key no
        /// acquire had ever written. Both now ask the same question.
        /// </summary>
        private bool ScaleOutConfigured() =>
            _config.GetSetting("Caching:ScaleOut", "false") == "true"
            && !string.IsNullOrEmpty(_config.GetConnectionString(SettingKeys.DistributedCacheKey));
    }
}
