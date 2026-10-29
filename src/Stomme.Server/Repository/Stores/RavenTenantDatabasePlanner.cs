#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Decides, per tenant, which RavenDB database its rows live in and makes it ready. Run by the install and boot
    /// path for every tenant, in the loop that migrates a relational tenant's schema.
    /// </summary>
    /// <remarks>
    /// <para>The rule, in order:</para>
    /// <list type="number">
    /// <item>The tenant's connection string must name the installation's RavenDB server; another server is refused (the host
    /// holds one document store).</item>
    /// <item>A tenant whose connection string names the installation's own database shares it - as two relational tenants
    /// sharing one connection string share one database.</item>
    /// <item>A tenant listed in <c>Database:RavenSharedTenants</c> stays in the installation's database (see 5).</item>
    /// <item>A tenant whose own database exists uses it (the search index deployed there, idempotently).</item>
    /// <item>A tenant whose own database does NOT exist but which has aliases is a tenant created BEFORE tenants had databases of their own: every
    /// tenant's rows were then written to the installation's database whatever its connection string named, so its rows are
    /// there. It stays there - listed in <c>Database:RavenSharedTenants</c> so the answer is stable, and logged - rather than
    /// being routed to an empty database, which would hide its sites.</item>
    /// <item>Otherwise the tenant is new: its database is created with the search index, before any of its rows exist.</item>
    /// </list>
    /// </remarks>
    public sealed class RavenTenantDatabasePlanner(IDocumentStore store, RavenTenantDatabases databases, Func<string> readSharedTenants, Action<string> writeSharedTenants, ILogger logger)
    {
        public const string SharedTenantsSetting = "Database:RavenSharedTenants";

        public async Task<string?> PlanAsync(Tenant tenant, string connectionString, Func<Task<bool>> hasAliases, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            var (urls, database) = RavenConnectionString.Parse(connectionString);
            if (!SameServer(urls, store.Urls))
            {
                throw new InvalidOperationException(
                    $"Tenant {tenant.Name} names a RavenDB server other than the installation's; a tenant's database must be on the installation's server.");
            }

            string? resolved;
            if (string.Equals(database, store.Database, StringComparison.OrdinalIgnoreCase) || SharedTenants().Contains(tenant.TenantId))
            {
                resolved = null;
            }
            else if (await ExistsAsync(database, cancellationToken))
            {
                await RavenFrameworkStoreServiceExtensions.EnsureTenantDatabaseAsync(store, database, cancellationToken);
                resolved = database;
            }
            else if (await hasAliases())
            {
                var shared = SharedTenants().Append(tenant.TenantId).Distinct().OrderBy(id => id);
                writeSharedTenants(string.Join(",", shared));
                logger.LogWarning(
                    "Tenant {Tenant} names the RavenDB database {Database}, which does not exist, and has sites: it was created before a tenant's rows " +
                    "followed its connection string, so they are in the installation's database. It stays there ({Setting}).",
                    tenant.Name, database, SharedTenantsSetting);
                resolved = null;
            }
            else
            {
                await RavenFrameworkStoreServiceExtensions.EnsureTenantDatabaseAsync(store, database, cancellationToken);
                logger.LogInformation("Tenant {Tenant}'s rows live in its own RavenDB database {Database}.", tenant.Name, database);
                resolved = database;
            }

            databases.Set(tenant.TenantId, resolved);
            return resolved;
        }

        private HashSet<int> SharedTenants() =>
            (readSharedTenants() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToHashSet();

        private async Task<bool> ExistsAsync(string database, CancellationToken cancellationToken) =>
            await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(database), cancellationToken) is not null;

        private static bool SameServer(IReadOnlyCollection<string> urls, IReadOnlyCollection<string> installation) =>
            urls.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase).Overlaps(installation.Select(Normalize));

        private static string Normalize(string url) => url.Trim().TrimEnd('/');
    }

    /// <summary>A RavenDB connection string: <c>Url=http://host:port;Database=name</c> (<c>Urls=</c> a comma-separated cluster).</summary>
    public static class RavenConnectionString
    {
        public static (string[] Urls, string Database) Parse(string connectionString)
        {
            string? database = null;
            var urls = new List<string>();
            foreach (var part in (connectionString ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = part[..separator].Trim();
                var value = part[(separator + 1)..].Trim();
                if (key.Equals("Url", StringComparison.OrdinalIgnoreCase) || key.Equals("Urls", StringComparison.OrdinalIgnoreCase))
                {
                    urls.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    database = value;
                }
            }

            if (urls.Count == 0 || string.IsNullOrEmpty(database))
            {
                throw new InvalidOperationException("A RavenDB connection string names both a Url and a Database, e.g. \"Url=http://127.0.0.1:8081;Database=Stomme\".");
            }

            return (urls.ToArray(), database);
        }
    }
}
