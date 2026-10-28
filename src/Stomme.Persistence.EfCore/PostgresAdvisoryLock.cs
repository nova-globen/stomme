using System.Text;
using Stomme.Persistence.Abstractions;
using Npgsql;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// Cross-node <see cref="IDistributedLock"/> backed by PostgreSQL session-level advisory locks
/// (<c>pg_try_advisory_lock</c>). The lease is held on a dedicated connection for the handle's lifetime;
/// disposing the handle (or losing the connection — e.g. a crashed holder) releases it, which satisfies
/// the auto-expiry intent. Advisory locks have no native time-to-live, so <c>ttl</c> is not enforced as a
/// hard deadline here; callers that need a strict deadline keep the held section short (as the outbox
/// processor does). The string key is mapped to the <c>bigint</c> the advisory-lock functions take.
/// </summary>
public sealed class PostgresAdvisoryLock(string connectionString) : IDistributedLock
{
    public async Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _ = ttl; // advisory locks release on dispose / connection loss, not on a timer

        var lockId = HashKey(key);
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", lockId);
            var acquired = await command.ExecuteScalarAsync(cancellationToken) as bool? ?? false;
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null; // already held by another node
            }

            return new Handle(connection, lockId, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Stable 64-bit mapping of the lock key (FNV-1a) — advisory locks key on a bigint, not a string.
    private static long HashKey(string key)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= prime;
        }

        return unchecked((long)hash);
    }

    private sealed class Handle(NpgsqlConnection connection, long lockId, string key) : ILockHandle
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
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", lockId);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                // Closing the session also releases any advisory locks it holds.
                await connection.DisposeAsync();
            }
        }
    }
}
