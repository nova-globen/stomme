#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>
    /// The licence read path's state: a singleton that caches the
    /// PARSE and SIGNATURE result of a key per <c>SHA-256(blob) + '|' + InstallationId</c>, and re-checks <c>nbf</c>/<c>exp</c>
    /// against <see cref="TimeProvider"/> on EVERY call — so a key expires mid-process with no job, no persistence and no
    /// restart. The signed blob IS the state; editing it only produces a different cache key that fails verification.
    /// </summary>
    /// <remarks>
    /// The cache is bounded: past <see cref="MaxCachedEntries"/> it is cleared, so configuration churn (or a caller
    /// feeding arbitrary strings) cannot grow it without bound. An entry also remembers the ring instance it was verified
    /// against, so a ring substitution is never answered from a stale entry. Keys and blobs are never logged.
    /// </remarks>
    internal sealed class LicenceState
    {
        internal const int MaxCachedEntries = 1024;

        private readonly TimeProvider _time;
        private readonly Func<IReadOnlyDictionary<string, byte[]>> _ring;
        private readonly Func<string?, string?, IReadOnlyDictionary<string, byte[]>, LicenceVerification> _verifyUntimed;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        public LicenceState(TimeProvider time)
            : this(time, () => LicenceKeyRing.Current, LicenceKeyVerifier.VerifyUntimed)
        {
        }

        /// <summary>The seam <c>LicenceStateTests</c> counts verifier calls through.</summary>
        internal LicenceState(
            TimeProvider time,
            Func<IReadOnlyDictionary<string, byte[]>> ring,
            Func<string?, string?, IReadOnlyDictionary<string, byte[]>, LicenceVerification> verifyUntimed)
        {
            ArgumentNullException.ThrowIfNull(time);
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(verifyUntimed);
            _time = time;
            _ring = ring;
            _verifyUntimed = verifyUntimed;
        }

        internal int CachedEntryCount => _cache.Count;

        public LicenceVerification Evaluate(string blob, string installationId)
        {
            var ring = _ring();

            if (string.IsNullOrEmpty(blob) || blob.Length > LicencePayload.MaxBlobBytes)
            {
                // Rejected by the envelope check before any hashing: nothing worth caching.
                return LicenceKeyVerifier.ApplyValidityWindow(_verifyUntimed(blob, installationId, ring), _time.GetUtcNow());
            }

            var key = CacheKey(blob, installationId);
            LicenceVerification untimed;
            if (_cache.TryGetValue(key, out var entry) && ReferenceEquals(entry.Ring, ring))
            {
                untimed = entry.Result;
            }
            else
            {
                untimed = _verifyUntimed(blob, installationId, ring);
                if (_cache.Count >= MaxCachedEntries)
                {
                    _cache.Clear();
                }

                _cache[key] = new CacheEntry(ring, untimed);
            }

            return LicenceKeyVerifier.ApplyValidityWindow(untimed, _time.GetUtcNow());
        }

        private static string CacheKey(string blob, string? installationId) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blob))) + "|" + (installationId ?? string.Empty);

        private sealed record CacheEntry(IReadOnlyDictionary<string, byte[]> Ring, LicenceVerification Result);
    }
}