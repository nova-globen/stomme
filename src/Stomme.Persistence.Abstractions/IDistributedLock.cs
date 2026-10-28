namespace Stomme.Persistence.Abstractions;

/// <summary>
/// The single abstraction for cross-node mutual exclusion. Persistence owns it; Collaboration and
/// Pipelines consume it. Leases auto-expire after their TTL (surviving a crashed holder) and are
/// NOT re-entrant — a caller must not re-acquire the same key on the same context.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire a lease on <paramref name="key"/> for <paramref name="ttl"/>. Returns a
    /// handle to release early, or null if the key is already held.
    /// </summary>
    Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>A held lock lease. Disposing releases it early; otherwise it expires after its TTL.</summary>
public interface ILockHandle : IAsyncDisposable
{
    /// <summary>The locked key.</summary>
    string Key { get; }
}
