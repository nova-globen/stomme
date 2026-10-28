#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.RavenDb;
using Stomme.Tenancy.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The RavenDB database each tenant's rows live in (BL-176): the database its connection string names, as on a relational
    /// install. Null is the store's default database - the installation's own, where the master tenant lives and where every
    /// tenant lived before BL-176. Filled by the boot and install path (<see cref="RavenTenantDatabasePlanner"/>) for every
    /// tenant before a request is served; the master tenant is known from the start.
    /// </summary>
    public sealed class RavenTenantDatabases
    {
        public const int MasterTenantId = 1;

        private readonly ConcurrentDictionary<int, string?> _byTenant = new();

        public RavenTenantDatabases() => _byTenant[MasterTenantId] = null;

        /// <summary>Records where <paramref name="tenantId"/>'s rows live; null for the installation's own database.</summary>
        public void Set(int tenantId, string? database)
        {
            if (tenantId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tenantId), "A framework tenant id starts at 1.");
            }

            _byTenant[tenantId] = string.IsNullOrWhiteSpace(database) ? null : database.Trim();
        }

        public bool TryGet(int tenantId, out string? database) => _byTenant.TryGetValue(tenantId, out database);

        /// <summary>The databases of their own that tenants have, distinct - what the outbox drains and a backup carries besides the installation's.</summary>
        public IReadOnlyCollection<string> OwnDatabases =>
            _byTenant.Values.Where(value => value is not null).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.Ordinal).ToList();

        /// <summary>The tenants whose rows live in <paramref name="database"/> (null: the installation's own).</summary>
        public IReadOnlyList<int> TenantsIn(string? database) =>
            _byTenant.Where(entry => string.Equals(entry.Value, database, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).OrderBy(id => id).ToList();
    }

    /// <summary>
    /// The host's <see cref="IRavenTenantDatabaseResolver"/> (BL-176): a tenant's sessions open on the database its connection
    /// string names. FAIL-CLOSED: a tenant this host has not resolved is refused, never routed to the installation's database,
    /// which would put its rows beside another tenant's; system scope (no tenant) is the installation's database.
    /// </summary>
    public sealed class TenantRavenDatabaseResolver(RavenTenantDatabases databases) : IRavenTenantDatabaseResolver
    {
        public string? Resolve(TenantId? tenantId)
        {
            if (tenantId is null)
            {
                return null;
            }

            if (!FrameworkTenantId.TryToTenantId(tenantId, out var id))
            {
                throw new InvalidOperationException($"'{tenantId}' is not a tenant of this installation, so it has no RavenDB database.");
            }

            if (databases.TryGet(id, out var database))
            {
                return database;
            }

            throw new InvalidOperationException(
                $"The RavenDB database of tenant {id} has not been resolved on this host. A tenant's database is resolved when the " +
                "host starts and when the tenant is created; restart the host if the tenant was added by another node.");
        }

        public bool RoutesPerTenant => databases.OwnDatabases.Count > 0;

        public IReadOnlyCollection<string> RoutedDatabases => databases.OwnDatabases;
    }
}
