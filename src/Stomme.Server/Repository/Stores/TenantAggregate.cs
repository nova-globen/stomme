using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Tenant aggregate root — the persistence-side model of the framework store's <c>Tenant</c> table (D52),
    /// reachable only through <see cref="ITenantStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads).
    /// A MASTER-database aggregate, like <see cref="AliasAggregate"/>, <see cref="ThemeAggregate"/> and the master
    /// half of Setting (D23).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MASTER, not tenant.</b> Tenant is the installation's registry of physical databases: the harvested
    /// <see cref="MasterDBContext"/> held <c>DbSet&lt;Tenant&gt;</c> and the master migration set creates the table.
    /// So <see cref="ITenantStore"/> is bound to <see cref="MasterDBContext"/> and its reads go through
    /// <see cref="IMasterStoreSessionFactory"/>, NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>.
    /// </para>
    /// <para>
    /// <b><see cref="DBType"/> is the load-bearing field.</b> It is the provider-selector string
    /// (<c>Tenant.DBType</c>) that the resolution pipeline reads on every request to choose the persistence provider
    /// for the tenant's data. It MUST round-trip intact.
    /// </para>
    /// <para>
    /// <b>Five content columns are persisted.</b> Verified column-for-column against the live migrated PostgreSQL
    /// database (stomme-pg-44357, host 45432): the <c>tenant</c> table has EXACTLY nine columns — <c>tenant_id</c>,
    /// <c>name</c> (NOT NULL, UNIQUE index <c>ix_tenant</c>), <c>db_connection_string</c> (NOT NULL), the NULLABLE
    /// <c>version</c> and <c>db_type</c>, and the four audit columns (all NOT NULL). NOTE the builder LIES:
    /// <c>TenantEntityBuilder</c> does NOT declare <c>db_type</c> (migration <c>02010001_AddDatabaseTypeColumnToTenant</c>
    /// adds it; <c>06010501_UpdateTenantDBType</c> rewrites its values) — the column exists in the live table and must
    /// be mapped.
    /// </para>
    /// <para>
    /// <b><see cref="TenantVersion"/> is the tenant table's own <c>version</c> STRING column</b>, NOT the aggregate's
    /// concurrency token. <see cref="AggregateRoot{TId}.Version"/> is a <c>uint</c> that shadows it by name — the exact
    /// <c>SiteAggregate.SiteVersion</c>/<c>ThemeAggregate.ThemeVersion</c> situation — so the string is carried on a
    /// distinct property mapped to the <c>version</c> column, and the base <c>Version</c> is ignored in
    /// <see cref="FrameworkStoreModel"/>.
    /// </para>
    /// <para>
    /// <b>IAuditable, and it must stay that way.</b> <c>TenantEntityBuilder</c> extends
    /// <c>AuditableBaseEntityBuilder</c> and calls <c>AddAuditableColumns</c>; the four audit columns are NOT NULL in
    /// the live database. <c>DbContextUtils</c> decides by RUNTIME TYPE TEST, so dropping <see cref="IAuditable"/>
    /// would compile and then FAIL every insert (created_by/modified_by are NOT NULL). It is NOT <c>IDeletable</c> —
    /// the live table has no is_deleted/deleted_by/deleted_on.
    /// </para>
    /// </remarks>
    public class TenantAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>Name of the tenant. The <c>name</c> column, NOT NULL, UNIQUE — matched IN MEMORY by the repository.</summary>
        public string Name { get; set; }

        /// <summary>Connection string (or the appsettings key naming one) for the tenant database. The <c>db_connection_string</c> column, NOT NULL.</summary>
        public string DBConnectionString { get; set; }

        /// <summary>
        /// The provider-selector — the assembly-qualified <c>IDatabase</c> type that chooses the persistence provider
        /// for this tenant's data. The NULLABLE <c>db_type</c> column (absent from the builder, added by migration
        /// <c>02010001</c>). The load-bearing field of the routing core; it must round-trip intact.
        /// </summary>
        public string DBType { get; set; }

        /// <summary>
        /// The tenant's schema version string — the <c>version</c> column, nullable. Named
        /// <see cref="TenantVersion"/> rather than <c>Version</c> because <see cref="AggregateRoot{TId}.Version"/>
        /// (a <c>uint</c> concurrency token) shadows that name; mapped to the <c>version</c> column in
        /// <see cref="FrameworkStoreModel"/>, exactly as <c>ThemeAggregate.ThemeVersion</c> is mapped to Theme's.
        /// </summary>
        public string TenantVersion { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Tenant"/> for an
        /// update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int tenantId) => Id = tenantId;
    }
}
