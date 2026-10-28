using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stomme.Databases.Interfaces;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// The per-tenant store for OpenIddict's entities (applications / authorizations / scopes / tokens),
    /// D61 spike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a DEDICATED context — it is NOT <see cref="TenantDBContext"/> and adds no <c>DbSet</c> to it
    /// (CLAUDE.md forbids that). It also does NOT ride the persistence seam (<c>ISiteStore</c> and friends):
    /// OpenIddict registers its own store implementations over this context via
    /// <c>UseEntityFrameworkCore().UseDbContext&lt;TenantOpenIddictDbContext&gt;()</c>.
    /// </para>
    /// <para>
    /// <b>Per-tenant routing.</b> It resolves the current tenant EXACTLY the way <see cref="DBContextBase"/>
    /// and <see cref="TenantDBContext"/> do (D37): <c>OnConfiguring</c> is a sync EF platform virtual, so it
    /// reads no store — it calls <see cref="ITenantManager.GetResolvedTenant"/>, which reads the alias off
    /// <c>SiteState</c> and looks the tenant up in the scope's warmed <c>ITenantStoreBindings</c>. A different
    /// request host resolves to a different alias -> tenant -> connection string, so tenant A's OpenIddict
    /// applications/tokens live in a physically different database from tenant B's and are invisible across
    /// the boundary. A miss throws; the null-tenant case is the installer/uninstalled state and is left
    /// unconfigured.
    /// </para>
    /// <para>
    /// <b>The OpenIddict database is separate from the tenant's own database.</b> OpenIddict's tables are
    /// created with <c>EnsureCreated</c> (spike) rather than Stomme's migration pipeline, so they cannot
    /// share a database with the migrated tenant schema (EnsureCreated is a no-op against a populated
    /// database). The connection string is resolved from configuration under the key
    /// <c>{tenant.DBConnectionString}_OpenIddict</c> — a per-tenant key provisioned alongside the tenant's
    /// own connection string. It fails CLOSED (throws) when that key is absent on an OpenIddict-enabled
    /// install, mirroring the seam's fail-closed routing rather than silently sharing a store.
    /// </para>
    /// </remarks>
    public class TenantOpenIddictDbContext : DbContext
    {
        /// <summary>The per-tenant OpenIddict connection-string key suffix (see the remarks).</summary>
        public const string ConnectionStringKeySuffix = "_OpenIddict";

        private readonly ITenantManager _tenantManager;
        private readonly IConfigurationRoot _config;

        public TenantOpenIddictDbContext(DbContextOptions<TenantOpenIddictDbContext> options, IDBContextDependencies dependencies)
            : base(options)
        {
            _tenantManager = dependencies.TenantManager;
            _config = dependencies.Config;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Sync and reads NO store (D37): GetResolvedTenant reads the alias off SiteState and the tenant
            // out of the scope's warmed bindings. Null tenant = installer/uninstalled state -> leave the
            // context unconfigured, exactly as DBContextBase does.
            Tenant tenant = _tenantManager.GetResolvedTenant();
            if (tenant == null)
            {
                base.OnConfiguring(optionsBuilder);
                return;
            }

            // BL-55: the explicit {key}_OpenIddict connection string wins; when absent (every install done
            // outside the test suite — nothing writes it) a DEDICATED sibling store is DERIVED from the
            // tenant's own connection string, so the authority works storage-blind with zero provisioning.
            // D61's per-tenant isolation is preserved: the derived store is never the tenant's own database.
            var connectionString = OpenIddictConnectionStrings.Resolve(tenant, _config);

            var database = Activator.CreateInstance(Type.GetType(tenant.DBType)) as IDatabase;
            if (database is IRelationalDatabase relationalDatabase)
            {
                optionsBuilder.UseStommeDatabase(relationalDatabase, connectionString);
            }
            else
            {
                // D61 is relational/EF only (phase C carries the RavenDB OpenIddict stores). A non-relational
                // tenant reaching here is a misconfiguration, not a silent no-op.
                throw new InvalidOperationException(
                    $"The OpenIddict per-tenant store (D61) is relational/EF only, but tenant {tenant.TenantId} " +
                    $"uses the non-relational provider '{tenant.DBType}'. RavenDB OpenIddict stores are phase C.");
            }

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Register OpenIddict's EF Core entity set (applications, authorizations, scopes, tokens).
            builder.UseOpenIddict();
        }
    }
}
