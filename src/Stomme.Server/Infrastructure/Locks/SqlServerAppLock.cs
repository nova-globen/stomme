#nullable enable
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Microsoft.Data.SqlClient;

namespace Stomme.Infrastructure.Locks
{
    /// <summary>
    /// Cross-node <see cref="IDistributedLock"/> on SQL Server (BL-154): an exclusive <c>sp_getapplock</c> owned by a
    /// dedicated SESSION for the handle's lifetime. Disposing the handle releases it, and so does losing the connection
    /// (a crashed holder), which is the auto-expiry the abstraction asks for; like the PostgreSQL advisory lock, there is
    /// no timer, so the TTL is not a hard deadline.
    /// </summary>
    /// <remarks>
    /// <c>@LockTimeout = 0</c> answers at once: a return code below zero means another session holds it. App locks are
    /// scoped to the connection's DATABASE, so every node connecting to the install's database contends for the same
    /// resource. A resource name is at most 255 characters.
    /// </remarks>
    public sealed class SqlServerAppLock(string connectionString) : IDistributedLock
    {
        public const int MaxResourceLength = 255;

        public async Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (key.Length > MaxResourceLength)
            {
                throw new ArgumentException($"A SQL Server app-lock resource is at most {MaxResourceLength} characters.", nameof(key));
            }

            _ = ttl; // released with the session, not on a timer (see the remarks)
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                if (await RunAsync(connection, "sp_getapplock", key, cancellationToken, ("@LockMode", "Exclusive"), ("@LockOwner", "Session"), ("@LockTimeout", 0)) < 0)
                {
                    await connection.DisposeAsync();
                    return null; // held by another session
                }

                return new Handle(connection, key);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        private static async Task<int> RunAsync(SqlConnection connection, string procedure, string key, CancellationToken ct, params (string Name, object Value)[] extra)
        {
            await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@Resource", key);
            foreach (var (name, value) in extra)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var result = command.Parameters.Add("@Result", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;
            await command.ExecuteNonQueryAsync(ct);
            return (int)result.Value;
        }

        private sealed class Handle(SqlConnection connection, string key) : ILockHandle
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
                    await RunAsync(connection, "sp_releaseapplock", key, CancellationToken.None, ("@LockOwner", "Session"));
                }
                finally
                {
                    // Closing the session releases whatever it still holds.
                    await connection.DisposeAsync();
                }
            }
        }
    }
}
