using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.RavenDb;
using Stomme.Repository;
using Microsoft.Extensions.Configuration;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Stomme.Identity.OpenIddict.Raven
{
    /// <summary>
    /// Opens a RavenDB async session bound to the CURRENT tenant's dedicated OpenIddict database (D62).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the RavenDB counterpart of <see cref="TenantOpenIddictDbContext"/>'s per-alias routing. the predecessor platform's
    /// Raven OpenIddict stores were single-authority (one shared <see cref="IDocumentStore"/>, one database);
    /// Stomme is PER-TENANT, so isolation has to be built the way B1/EF builds it — a physically separate
    /// OpenIddict database per tenant, addressed by the SAME connection-string convention
    /// (<c>{tenant.DBConnectionString}_OpenIddict</c>). There is no "per-tenant IDocumentStore keyed on
    /// SiteState.Alias" in this codebase: the framework store's <see cref="IDocumentStore"/> is a single host-wide
    /// singleton and isolates tenants by a query filter, not by a second store. OpenIddict's store calls carry no
    /// tenant filter, so a shared database would leak clients/tokens across tenants; a separate database per
    /// tenant reproduces B1's invisibility property exactly.
    /// </para>
    /// <para>
    /// The tenant is read fresh per request through <see cref="ITenantManager.GetResolvedTenant"/> — the alias off
    /// <c>SiteState</c>, resolved by the tenant middleware before the token endpoint runs — so tenant A's host and
    /// tenant B's host resolve to different OpenIddict databases and cannot see each other's documents. A miss
    /// (no tenant, or no per-tenant OpenIddict connection string) fails CLOSED with a throw, mirroring the EF
    /// context, rather than silently sharing one authority store.
    /// </para>
    /// </remarks>
    internal sealed class TenantRavenOpenIddictSessionProvider : IRavenOpenIddictSessionProvider
    {
        private readonly ITenantManager _tenantManager;
        private readonly IConfigurationRoot _config;
        private readonly RavenOpenIddictDocumentStoreCache _cache;

        private IDocumentStore _store;
        private string _database;
        private bool _resolved;

        public TenantRavenOpenIddictSessionProvider(IDBContextDependencies dependencies, RavenOpenIddictDocumentStoreCache cache)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            _tenantManager = dependencies.TenantManager;
            _config = dependencies.Config;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async ValueTask<IAsyncDocumentSession> OpenSessionAsync(CancellationToken cancellationToken)
        {
            if (!_resolved)
            {
                Resolve();
                _resolved = true;
            }

            // Idempotent, cached per (server, database): the dedicated OpenIddict database is created on first
            // use, the document-store equivalent of the EF path's EnsureCreated at client-registration time.
            await _cache.EnsureDatabaseAsync(_store, _database, cancellationToken).ConfigureAwait(false);
            return _store.OpenAsyncSession(new SessionOptions { Database = _database });
        }

        private void Resolve()
        {
            Tenant tenant = _tenantManager.GetResolvedTenant();
            if (tenant == null)
            {
                throw new InvalidOperationException(
                    "The RavenDB OpenIddict store (D62) was reached with no resolved tenant. The token/authority " +
                    "endpoints run after tenant resolution, so a null tenant here is a misconfiguration, not the " +
                    "installer state.");
            }

            // BL-55: the explicit {key}_OpenIddict connection string wins; when absent, a DEDICATED sibling
            // database (Database={tenant-db}_OpenIddict on the same server) is DERIVED from the tenant's own
            // descriptor — storage-blind zero provisioning, isolation preserved (never the tenant's own DB).
            var connectionString = OpenIddictConnectionStrings.Resolve(tenant, _config);

            var (urls, database) = ParseRavenConnectionString(connectionString);
            _store = _cache.GetStore(urls);
            _database = database;
        }

        // The RavenDB descriptor parse, matching StommeServiceCollectionExtensions.ParseRavenConnectionString:
        // "Url=http://host:port;Database=name" (Urls= with a comma-separated list is accepted for a cluster).
        private static (string[] Urls, string Database) ParseRavenConnectionString(string connectionString)
        {
            string database = null;
            var urls = new List<string>();
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = part.Substring(0, separator).Trim();
                var value = part.Substring(separator + 1).Trim();
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
                throw new InvalidOperationException(
                    $"The RavenDB OpenIddict connection string \"{connectionString}\" must specify both a Url and a " +
                    "Database, e.g. \"Url=http://127.0.0.1:7000;Database=Stomme_OpenIddict\".");
            }

            return (urls.ToArray(), database);
        }
    }

    /// <summary>
    /// A host-wide cache of the RavenDB document stores the OpenIddict stores address, keyed by URL-set, plus
    /// idempotent per-database creation. Kept SEPARATE from the framework store's <see cref="IDocumentStore"/>
    /// (which is built from <c>DefaultConnection</c>) so the OpenIddict authority is decoupled from tenant data
    /// exactly as the EF <see cref="TenantOpenIddictDbContext"/> is — a dedicated database, its own connection.
    /// Singleton: a <see cref="IDocumentStore"/> is a long-lived, thread-safe connection factory.
    /// </summary>
    public sealed class RavenOpenIddictDocumentStoreCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, IDocumentStore> _stores = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _ensuredDatabases = new(StringComparer.Ordinal);

        public IDocumentStore GetStore(string[] urls)
        {
            ArgumentNullException.ThrowIfNull(urls);
            var key = string.Join(",", urls);
            return _stores.GetOrAdd(key, _ =>
                ((DocumentStore)new DocumentStore { Urls = urls }.InitializeStommeStore()));
        }

        public async ValueTask EnsureDatabaseAsync(IDocumentStore store, string database, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(store);
            var key = string.Join(",", store.Urls) + "|" + database;
            if (_ensuredDatabases.ContainsKey(key))
            {
                return;
            }

            await RavenDatabaseBootstrap.EnsureDatabaseAsync(store, database, cancellationToken).ConfigureAwait(false);
            _ensuredDatabases.TryAdd(key, 0);
        }

        public void Dispose()
        {
            foreach (var store in _stores.Values)
            {
                try
                {
                    store.Dispose();
                }
                catch
                {
                    // best-effort teardown
                }
            }

            _stores.Clear();
        }
    }
}
