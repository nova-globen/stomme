#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// The seam's <see cref="ITenantConnectionResolver"/> over Stomme's tenant store. Stomme is
    /// <b>database-per-tenant</b>: each <see cref="Models.Tenant"/> row carries its own
    /// <c>DBConnectionString</c> (and <c>DBType</c>), and many Sites can share one Tenant via their Aliases.
    /// There is no schema-per-tenant mode, so <see cref="TenantConnection.Schema"/> is always null — the
    /// isolation is the database itself, not a schema within one.
    /// </summary>
    /// <remarks>
    /// <c>Tenant.DBConnectionString</c> stores a connection-string <b>NAME</b>, not a literal:
    /// <c>DatabaseManager</c> writes <c>"DefaultConnection"</c> (or the tenant's name) at install, and
    /// <c>MigrateConnectionStringAsync</c> actively rewrites legacy inline strings into names. It is resolved
    /// through configuration here exactly as <c>DBContextBase.OnConfiguring</c> resolves it — one convention,
    /// two readers.
    /// </remarks>
    public sealed class StommeTenantConnectionResolver : ITenantConnectionResolver
    {
        private readonly ITenantRepository _tenantRepository;
        private readonly IConfigurationRoot _config;

        // Optional so the convention tests can compose the resolver with no host at all. When it is absent the
        // expansion falls back to the process-wide slot - what this class did for everyone before BL-153.
        private readonly IConfigManager _configManager;

        public StommeTenantConnectionResolver(
            ITenantRepository tenantRepository, IConfigurationRoot config, IConfigManager configManager = null)
        {
            _tenantRepository = tenantRepository;
            _config = config;
            _configManager = configManager;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">
        /// The id is not a Stomme framework tenant, or names a tenant that no longer exists. Both are
        /// programming/state errors rather than "no connection": returning a null or default connection would
        /// silently point the caller at the wrong database.
        /// </exception>
        public async Task<TenantConnection> ResolveAsync(TenantId tenantId)
        {
            if (!FrameworkTenantId.TryToTenantId(tenantId, out var frameworkTenantId))
            {
                throw new InvalidOperationException(
                    $"'{tenantId}' is not a Stomme framework tenant id; it was not minted by {nameof(FrameworkTenantId)}.");
            }

            var tenant = await _tenantRepository.GetTenantAsync(frameworkTenantId)
                ?? throw new InvalidOperationException($"Tenant {frameworkTenantId} was not found in the master store.");

            // A name configuration does not carry must throw: handing back the NAME as if it were a
            // connection string would silently point the caller at a non-existent database, which is
            // exactly what this resolver's contract forbids.
            var connectionString = _config.GetConnectionString(tenant.DBConnectionString);
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException(
                    $"Tenant '{tenant.Name}' names connection string '{tenant.DBConnectionString}', which is " +
                    $"not present in configuration (ConnectionStrings:{tenant.DBConnectionString}).");
            }

            return new TenantConnection(ExpandDataDirectory(connectionString, _configManager));
        }

        /// <summary>
        /// Tenant connection strings are stored with the <c>|DataDirectory|</c> placeholder so an installation
        /// stays relocatable; the framework's own contexts expand it the same way at connection time
        /// (<c>DBContextBase</c>, <c>MasterDBContext</c>). The seam gets a real, usable connection string.
        /// </summary>
        private static string ExpandDataDirectory(string connectionString, IConfigManager configManager)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                return connectionString;
            }

            // THIS host's data directory when there is a host to ask; the process-wide slot otherwise.
            var dataDirectory = configManager?.GetDataDirectory()
                ?? AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString();

            return string.IsNullOrEmpty(dataDirectory)
                ? connectionString
                : connectionString.Replace($"|{Constants.DataDirectory}|", dataDirectory);
        }
    }
}
