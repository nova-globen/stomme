using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Alias aggregate root — the persistence-side model of the framework store's <c>Alias</c> table,
    /// reachable only through <see cref="IAliasStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads).
    /// A MASTER-database aggregate, like <see cref="TenantAggregate"/>, <see cref="ThemeAggregate"/> and the master
    /// half of Setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MASTER, not tenant.</b> Alias is installation-wide routing state: the harvested <see cref="MasterDBContext"/>
    /// held <c>DbSet&lt;Alias&gt;</c> and the master migration set creates the table. So <see cref="IAliasStore"/> is
    /// bound to <see cref="MasterDBContext"/> and its reads go through <see cref="IMasterStoreSessionFactory"/>, NOT
    /// the tenant <see cref="IFrameworkStoreSessionFactory"/>.
    /// </para>
    /// <para>
    /// <b>The routing core.</b> <c>TenantResolver</c>/<c>AliasAccessor</c> read Alias on every request to map an
    /// incoming host+path to a Site and a Tenant. The lookup that does this — <c>AliasRepository.GetAliasAsync(url)</c>
    /// — matches the request URL against the cached list ENTIRELY IN MEMORY with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>, never a translated query, so the EF-vs-RavenDB string-equality
    /// divergence does not reach it. The stored <see cref="Name"/> is already lower-cased on write by the
    /// repository, exactly as the harvested code did.
    /// </para>
    /// <para>
    /// <b>Only FIVE content columns are persisted.</b> Verified column-for-column against the live migrated
    /// PostgreSQL database (stomme-pg-44357, host 45432): the <c>alias</c> table has EXACTLY nine columns —
    /// <c>alias_id</c>, <c>name</c> (NOT NULL, UNIQUE index <c>ix_alias</c>), <c>tenant_id</c>/<c>site_id</c> (NOT
    /// NULL), the NULLABLE <c>is_default</c>, and the four audit columns (all NOT NULL). NOTE the builder LIES:
    /// <c>AliasEntityBuilder</c> does NOT declare <c>is_default</c> (migration <c>03000201_AddAliasIsDefault</c> adds
    /// it later) — the column exists in the live table and must be mapped. <see cref="Alias.Path"/>/
    /// <see cref="Alias.SiteKey"/>/<see cref="Alias.Protocol"/>/<see cref="Alias.BaseUrl"/> are <c>[NotMapped]</c>
    /// runtime state with no column.
    /// </para>
    /// <para>
    /// <b>No <c>version</c> column, so no concurrency-token shadow to carry.</b> Unlike <see cref="TenantAggregate"/>,
    /// Theme and ModuleDefinition, the alias table has no <c>version</c> column at all, so there is nothing for
    /// <see cref="AggregateRoot{TId}.Version"/> (the uint token) to collide with; the base token is simply ignored in
    /// <see cref="FrameworkStoreModel"/> as on every other cluster.
    /// </para>
    /// <para>
    /// <b>No navigation to sever.</b> <see cref="TenantId"/>/<see cref="SiteId"/> are plain ints — the Alias contract
    /// carries no <c>Tenant</c> or <c>Site</c> navigation property, and Site is tenant-side (a different database)
    /// besides. The relational <c>FK_Alias_Tenant</c> stays in the DB (the migration created it, nothing here drops
    /// it) but the MODEL references the tenant by id only. So unlike the SiteGroup or Folder/File
    /// (D48/D49) pairs there was no mapped navigation to break.
    /// </para>
    /// <para>
    /// <b>IAuditable, and it must stay that way.</b> <c>AliasEntityBuilder</c> extends
    /// <c>AuditableBaseEntityBuilder</c> and calls <c>AddAuditableColumns</c>; the four audit columns are NOT NULL in
    /// the live database. <c>DbContextUtils</c> decides by RUNTIME TYPE TEST, so dropping <see cref="IAuditable"/>
    /// would compile and then FAIL every insert (created_by/modified_by are NOT NULL). It is NOT <c>IDeletable</c> —
    /// the live table has no is_deleted/deleted_by/deleted_on.
    /// </para>
    /// </remarks>
    public class AliasAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The alias URL (host + optional path), stored lower-cased. The <c>name</c> column, NOT NULL, UNIQUE.</summary>
        public string Name { get; set; }

        /// <summary>The tenant this alias resolves to. The <c>tenant_id</c> column; a plain id, not a navigation.</summary>
        public int TenantId { get; set; }

        /// <summary>The site this alias resolves to. The <c>site_id</c> column; a plain id (Site is tenant-side).</summary>
        public int SiteId { get; set; }

        /// <summary>Whether this is the default alias for the tenant/site. The NULLABLE <c>is_default</c> column.</summary>
        public bool IsDefault { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from an <see cref="Alias"/> for an
        /// update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int aliasId) => Id = aliasId;
    }
}
