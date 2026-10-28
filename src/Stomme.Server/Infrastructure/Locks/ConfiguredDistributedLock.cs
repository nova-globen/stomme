#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Database.PostgreSQL;
using Stomme.Database.RavenDb;
using Stomme.Database.SqlServer;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;

namespace Stomme.Infrastructure.Locks
{
    /// <summary>
    /// The host's ONE <see cref="IDistributedLock"/> (BL-154), choosing its primitive from the INSTALL's store at
    /// resolve time - the same resolve-time rule the framework store follows, because the store type and connection
    /// are written mid-install, after the container is built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Two lock abstractions shipped and neither was a lock where one was possible: the
    /// cache-backed startup mutex is a Get-then-Set, and the store-backed <see cref="IDistributedLock"/>
    /// implementations (PostgreSQL advisory, RavenDB compare-exchange) were never REGISTERED, so the metering fold and
    /// anything else asking for one ran on an in-process fallback on every provider.
    /// </para>
    /// <para>
    /// <b>The choice, per install:</b> PostgreSQL - <see cref="PostgresAdvisoryLock"/> on the install's connection;
    /// SQL Server - <see cref="SqlServerAppLock"/> (new); RavenDB - <see cref="RavenDistributedLock"/> on the install's
    /// document store; SQLite, the File store, or a host not yet installed - <see cref="InProcessDistributedLock"/>, the
    /// explicit SINGLE-NODE answer (a SQLite file is one node by construction). <see cref="IsCrossNode"/> says which.
    /// </para>
    /// <para>
    /// The startup mutex (<c>IDistributedLockManager</c>) is NOT routed through this: it runs before a store exists on
    /// a fresh install, and its limits are stated on it.
    /// </para>
    /// </remarks>
    public sealed class ConfiguredDistributedLock(IServiceProvider services, IConfigManager config) : IDistributedLock
    {
        private readonly ConcurrentDictionary<(string Type, string Connection), IDistributedLock> _chosen = new();

        /// <summary>Whether the lock in force right now coordinates across processes.</summary>
        public bool IsCrossNode => Current() is not InProcessDistributedLock;

        public Task<ILockHandle?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Current().AcquireAsync(key, ttl, cancellationToken);

        internal IDistributedLock Current()
        {
            var type = config.GetDatabaseType() ?? string.Empty;
            var connection = config.GetConnectionString() ?? string.Empty;
            return _chosen.GetOrAdd((type, connection), key => Choose(key.Type, key.Connection));
        }

        private IDistributedLock Choose(string databaseType, string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new InProcessDistributedLock(); // not installed yet: nothing to coordinate with
            }

            if (Is<PostgreSQLDatabase>(databaseType))
            {
                return new PostgresAdvisoryLock(connectionString);
            }

            if (Is<SqlServerDatabase>(databaseType))
            {
                return new SqlServerAppLock(connectionString);
            }

            if (Is<RavenDatabase>(databaseType))
            {
                return new RavenDistributedLock(services.GetRequiredService<IDocumentStore>());
            }

            return new InProcessDistributedLock();
        }

        // The configured value is the assembly-qualified type name; compare on the type's full name only, so a version
        // or a pre-6.1.5 assembly name does not change the answer.
        private static bool Is<TDatabase>(string databaseType) =>
            databaseType.Split(',')[0].Trim() == typeof(TDatabase).FullName;
    }
}
