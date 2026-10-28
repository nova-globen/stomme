using System.Collections.Concurrent;
using Stomme.FeatureManagement.Application;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Infrastructure;

/// <summary>
/// The <see cref="IFeatureFlagCache"/> singleton: a per-tenant, version-stamped snapshot cache over plain
/// BCL primitives (a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the module deliberately takes no
/// caching package; nothing here needs one).
/// <para>
/// <b>Why version stamps, not just removal.</b> The dangerous race is: an evaluation reads the store, a
/// write commits and invalidates, and only THEN does the evaluation store what it read — pre-write data
/// cached after the write, stale until the next invalidation. Versioning closes it: a load captures the
/// tenant's version when it starts; <see cref="Invalidate"/> bumps the version; a load that started before
/// the bump fails both the store-time check and (belt and braces) the serve-time
/// <c>entry.Version == current</c> check, so pre-write data is never SERVED after the write — at worst the
/// next read reloads. Serving requires a version match, so the check pair, not luck, carries the guarantee.
/// </para>
/// <para>
/// The TTL (default 30 s) only bounds how long a DIFFERENT process's write can go unseen — in-process
/// writes invalidate exactly. Expiry forces a reload, which cannot change an answer.
/// </para>
/// </summary>
internal sealed class FeatureFlagCache : IFeatureFlagCache
{
    /// <summary>
    /// How long an entry may serve without a fresh store read. In-process staleness is impossible (writes
    /// invalidate exactly); this bounds cross-process staleness on multi-node deployments. Conservative and
    /// owner-tunable in one place.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, long> _versions = new();
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeToLive;

    public FeatureFlagCache()
        : this(TimeProvider.System, DefaultTimeToLive)
    {
    }

    internal FeatureFlagCache(TimeProvider time, TimeSpan timeToLive)
    {
        _time = time;
        _timeToLive = timeToLive;
    }

    public async Task<IReadOnlyList<FeatureFlagSnapshot>> GetOrLoadAsync(
        TenantId tenantId,
        Func<CancellationToken, Task<IReadOnlyList<FeatureFlagSnapshot>>> load,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(load);

        var key = tenantId.Value;
        var version = CurrentVersion(key);
        if (_entries.TryGetValue(key, out var entry)
            && entry.Version == version
            && entry.ExpiresAt > _time.GetUtcNow())
        {
            return entry.Flags;
        }

        var flags = await load(ct);

        // Cache only when no invalidation raced the load: a bump between capture and here means this data
        // may predate a committed write. The caller still gets what it read (its read genuinely began
        // before the write), but the next evaluation reloads fresh.
        if (CurrentVersion(key) == version)
        {
            _entries[key] = new Entry(version, _time.GetUtcNow() + _timeToLive, flags);
        }

        return flags;
    }

    public void Invalidate(TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var key = tenantId.Value;

        // Bump FIRST, then drop: a load in flight captured the old version, so after the bump it can
        // neither be stored (store-time check) nor served (serve-time check) — the order matters.
        _versions.AddOrUpdate(key, 1, (_, current) => current + 1);
        _entries.TryRemove(key, out _);
    }

    private long CurrentVersion(Guid key) => _versions.TryGetValue(key, out var version) ? version : 0;

    private sealed record Entry(long Version, DateTimeOffset ExpiresAt, IReadOnlyList<FeatureFlagSnapshot> Flags);
}
