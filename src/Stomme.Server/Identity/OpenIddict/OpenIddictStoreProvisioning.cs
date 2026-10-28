using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stomme.Infrastructure;
using Stomme.Models;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// Resolves the CURRENT tenant's dedicated OpenIddict connection string (BL-55).
    /// </summary>
    /// <remarks>
    /// An explicit <c>ConnectionStrings:{tenant.DBConnectionString}_OpenIddict</c> key always wins. When the
    /// key is ABSENT — which is every install done outside the test suite, because nothing writes it — the
    /// connection string is DERIVED from the tenant's own, into a DEDICATED store (a sibling database or
    /// sibling file suffixed <c>_OpenIddict</c>). D61's fail-closed rule was aimed at silently SHARING one
    /// authority store across tenants; a derived per-tenant sibling store preserves that isolation while
    /// removing the undocumented provisioning burden, so OpenIddict works on whatever database the install
    /// chose (storage-blind). The derivation is deterministic, so every boot resolves the same store.
    /// </remarks>
    internal static class OpenIddictConnectionStrings
    {
        public static string Resolve(Tenant tenant, IConfigurationRoot config)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(config);

            var explicitKey = tenant.DBConnectionString + TenantOpenIddictDbContext.ConnectionStringKeySuffix;
            var explicitValue = config.GetConnectionString(explicitKey);
            if (!string.IsNullOrEmpty(explicitValue))
            {
                return explicitValue;
            }

            var tenantConnectionString = config.GetConnectionString(tenant.DBConnectionString);
            if (string.IsNullOrEmpty(tenantConnectionString))
            {
                throw new InvalidOperationException(
                    $"OpenIddict is enabled but tenant {tenant.TenantId}'s own connection string " +
                    $"(ConnectionStrings:{tenant.DBConnectionString}) is missing, so no dedicated OpenIddict " +
                    $"store can be derived from it. This is a broken install, not an OpenIddict gap.");
            }

            return DeriveDedicatedStore(tenantConnectionString);
        }

        /// <summary>
        /// Derives the dedicated OpenIddict store's connection string from the tenant's own, keyed on the
        /// connection-string SHAPE rather than a provider list (storage-blind): a named database
        /// (<c>Database=</c> / <c>Initial Catalog=</c> — relational servers and RavenDB descriptors alike)
        /// gets the <c>_OpenIddict</c> suffix on the name; a file-backed store (SQLite's <c>Data Source=</c>
        /// path) gets the suffix on the file stem. Every other part is preserved verbatim. An unrecognized
        /// shape throws with the explicit-key escape hatch.
        /// </summary>
        internal static string DeriveDedicatedStore(string tenantConnectionString)
        {
            var parts = tenantConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Pass 1: a named database. Checked FIRST because SQL Server uses Data Source for the SERVER —
            // the presence of Database/Initial Catalog disambiguates.
            for (var i = 0; i < parts.Length; i++)
            {
                var (key, value) = SplitPart(parts[i]);
                if (key == null || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                {
                    parts[i] = $"{key}={value}{TenantOpenIddictDbContext.ConnectionStringKeySuffix}";
                    return string.Join(";", parts) + ";";
                }
            }

            // Pass 2: a file-backed store (SQLite). The value must LOOK like a file (an extension, not
            // :memory:) — a bare server name falls through to the throw below.
            for (var i = 0; i < parts.Length; i++)
            {
                var (key, value) = SplitPart(parts[i]);
                if (key == null || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if ((key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Filename", StringComparison.OrdinalIgnoreCase)) &&
                    !value.Equals(":memory:", StringComparison.OrdinalIgnoreCase) &&
                    LooksLikeFileExtension(Path.GetExtension(value)))
                {
                    var directory = Path.GetDirectoryName(value);
                    var derivedFile =
                        Path.GetFileNameWithoutExtension(value) +
                        TenantOpenIddictDbContext.ConnectionStringKeySuffix +
                        Path.GetExtension(value);
                    var derivedPath = string.IsNullOrEmpty(directory) ? derivedFile : Path.Combine(directory, derivedFile);
                    parts[i] = $"{key}={derivedPath}";
                    return string.Join(";", parts) + ";";
                }
            }

            throw new InvalidOperationException(
                $"OpenIddict is enabled but no dedicated OpenIddict connection string could be derived from " +
                $"the tenant connection string \"{tenantConnectionString}\" — it names neither a database " +
                $"(Database=/Initial Catalog=) nor a store file (Data Source=…/file.ext). Set the explicit " +
                $"ConnectionStrings:{{tenant}}{TenantOpenIddictDbContext.ConnectionStringKeySuffix} key to a " +
                $"DEDICATED store (never the tenant's own database — D61 isolates the authority store).");
        }

        /// <summary>
        /// A CLEAN extension (".db", ".sqlite3") marks a store file; a SQL Server network address also has a
        /// dot (<c>tcp:server.example,1433</c> yields ".example,1433" from <see cref="Path.GetExtension"/>),
        /// so anything beyond letters/digits after the dot is NOT a file and falls through to the fail-closed
        /// throw.
        /// </summary>
        private static bool LooksLikeFileExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension) || extension.Length < 2)
            {
                return false;
            }

            for (var i = 1; i < extension.Length; i++)
            {
                if (!char.IsLetterOrDigit(extension[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static (string Key, string Value) SplitPart(string part)
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                return (null, null);
            }

            return (part.Substring(0, separator).Trim(), part.Substring(separator + 1).Trim());
        }
    }

    /// <summary>
    /// Ensures the CURRENT tenant's dedicated OpenIddict store EXISTS before anything touches it (BL-55).
    /// </summary>
    /// <remarks>
    /// The RavenDB side has always had this (<see cref="Raven.RavenOpenIddictDocumentStoreCache"/> creates
    /// the database idempotently at session open); the relational side's <c>EnsureCreated</c> lived only in
    /// the TEST suite — so the suites were green while a deployed install could not issue a token (the
    /// D47/BL-52 class). This interface is the storage-blind seam: callers (the provisioning middleware, a
    /// programmatic registration path) call it without knowing the provider; the relational implementation
    /// runs <c>EnsureCreated</c> once per tenant per process, the RavenDB implementation is a no-op because
    /// its session provider already ensures.
    /// </remarks>
    public interface IOpenIddictStoreProvisioner
    {
        /// <summary>Ensures the current tenant's OpenIddict store exists. No-op when no tenant is resolved.</summary>
        Task EnsureProvisionedAsync();
    }

    /// <summary>Process-lifetime record of which tenants' relational OpenIddict schemas have been ensured.</summary>
    public sealed class OpenIddictProvisionedStoreCache
    {
        private readonly ConcurrentDictionary<int, byte> _provisioned = new();

        public bool IsProvisioned(int tenantId) => _provisioned.ContainsKey(tenantId);

        public void MarkProvisioned(int tenantId) => _provisioned.TryAdd(tenantId, 0);
    }

    /// <summary>
    /// The relational implementation: <c>EnsureCreated</c> on <see cref="TenantOpenIddictDbContext"/>, once
    /// per tenant per process. Scoped — the context routes per-alias inside the scope (D37).
    /// </summary>
    internal sealed class RelationalOpenIddictStoreProvisioner : IOpenIddictStoreProvisioner
    {
        private readonly ITenantManager _tenantManager;
        private readonly TenantOpenIddictDbContext _context;
        private readonly OpenIddictProvisionedStoreCache _cache;

        public RelationalOpenIddictStoreProvisioner(ITenantManager tenantManager, TenantOpenIddictDbContext context, OpenIddictProvisionedStoreCache cache)
        {
            _tenantManager = tenantManager;
            _context = context;
            _cache = cache;
        }

        public async Task EnsureProvisionedAsync()
        {
            Tenant tenant = _tenantManager.GetResolvedTenant();
            if (tenant == null || _cache.IsProvisioned(tenant.TenantId))
            {
                return;
            }

            // Deliberately NOT the request's cancellation token: EnsureCreated no-ops when ANY table exists,
            // so a DDL run aborted midway would leave a half-created schema that never completes (the exact
            // BL-52 trap). Success-only caching: a failed attempt (store unreachable) retries on the next
            // request rather than wedging the tenant until restart.
            await _context.Database.EnsureCreatedAsync(CancellationToken.None);
            _cache.MarkProvisioned(tenant.TenantId);
        }
    }

    /// <summary>
    /// The document-store implementation: a no-op, because <see cref="Raven.TenantRavenOpenIddictSessionProvider"/>
    /// already creates the per-tenant OpenIddict database idempotently at session open.
    /// </summary>
    internal sealed class RavenOpenIddictStoreProvisioner : IOpenIddictStoreProvisioner
    {
        public Task EnsureProvisionedAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Runs the provisioner once the tenant is resolved, BEFORE authentication — OpenIddict validates a token
    /// request against its store inside the authentication stage, so the store must exist by then. Failures
    /// are logged to the FILE/console logger (never only the site DB log) and never fail the request: a page
    /// render must not die because the authority store is unreachable, and the endpoints that genuinely need
    /// the store surface their own failure.
    /// </summary>
    internal sealed class OpenIddictStoreProvisioningMiddleware
    {
        private readonly RequestDelegate _next;

        public OpenIddictStoreProvisioningMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context, IOpenIddictStoreProvisioner provisioner, IOpenIddictConfigSeeder seeder, ILogger<OpenIddictStoreProvisioningMiddleware> logger)
        {
            try
            {
                // Order matters: the store must EXIST before the seeder's managers write config-declared clients
                // into it. Both are once-per-tenant-per-process (their own caches), so this is a cheap dictionary
                // lookup after the first request per tenant.
                await provisioner.EnsureProvisionedAsync();
                await seeder.SeedAsync();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Bootstrapping the tenant's OpenIddict store (provision + config seed) failed; OpenIddict endpoints will be unavailable until it succeeds.");
            }

            await _next(context);
        }
    }
}
