using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The ModuleDefinition aggregate root — the persistence-side model of the framework store's
    /// <c>ModuleDefinition</c> table, reachable only through <see cref="IModuleDefinitionStore"/> (writes)
    /// and <see cref="IQuerySession.Query{T}"/> (reads). A MASTER-database aggregate, like
    /// <see cref="ThemeAggregate"/>, <see cref="JobAggregate"/> and the master half of Setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MASTER, not tenant.</b> ModuleDefinition is installation-wide — the registry of installed module
    /// types, read during module discovery at startup: the harvested <see cref="MasterDBContext"/> held
    /// <c>DbSet&lt;ModuleDefinition&gt;</c> and the master migration set creates the table. So
    /// <see cref="IModuleDefinitionStore"/> is bound to <see cref="MasterDBContext"/> and its reads go through
    /// <see cref="IMasterStoreSessionFactory"/>, NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>.
    /// </para>
    /// <para>
    /// <b>Only SIX columns are persisted; the rest of <see cref="ModuleDefinition"/> is <c>[NotMapped]</c>
    /// runtime state.</b> Verified column-for-column against the live migrated PostgreSQL database
    /// (stomme-pg-44357, host 45432): the <c>module_definition</c> table has EXACTLY ten columns —
    /// <c>module_definition_id</c>, <c>module_definition_name</c> (NOT NULL), the NULLABLE
    /// <c>name</c>/<c>description</c>/<c>categories</c>/<c>version</c>, and the audit quartet (all four NOT
    /// NULL). This matches <c>ModuleDefinitionsEntityBuilder</c> exactly — the builder does NOT lie for this
    /// table. Everything else on <see cref="ModuleDefinition"/> (Permissions/Resources/Runtimes/IsEnabled/
    /// PageTemplates/…) is reconstructed from the loaded module assemblies and from Settings/Permissions at read
    /// time; it never touches this table.
    /// </para>
    /// <para>
    /// <b><see cref="ModuleDefinitionVersion"/> is the table's own <c>version</c> STRING column</b>, NOT the
    /// aggregate's concurrency token. <see cref="AggregateRoot{TId}.Version"/> is a <c>uint</c> that shadows it
    /// by name — the exact <c>SiteAggregate.SiteVersion</c>/<c>ThemeAggregate.ThemeVersion</c> situation — so
    /// the string is carried on a distinct property mapped to the <c>version</c> column, and the base
    /// <c>Version</c> is ignored in <see cref="FrameworkStoreModel"/>.
    /// </para>
    /// <para>
    /// <b>IAuditable, and it must stay that way.</b> <c>ModuleDefinitionsEntityBuilder</c> extends
    /// <c>AuditableBaseEntityBuilder</c> and calls <c>AddAuditableColumns</c>; the four audit columns are NOT
    /// NULL in the live database. <c>DbContextUtils</c> decides by RUNTIME TYPE TEST, so dropping
    /// <see cref="IAuditable"/> would compile and then FAIL every insert (created_by/modified_by are NOT NULL).
    /// It is NOT <see cref="IDeletable"/> — the live table has no is_deleted/deleted_by/deleted_on.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class ModuleDefinitionAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The full namespace / identifier of the module type — the sync key the repository matches on.</summary>
        public string ModuleDefinitionName { get; set; }

        /// <summary>The friendly, user-customizable name shown in the UI. Nullable in the database.</summary>
        public string Name { get; set; }

        /// <summary>The user-customizable module description. Nullable in the database.</summary>
        public string Description { get; set; }

        /// <summary>The user-customizable admin categories. Nullable in the database.</summary>
        public string Categories { get; set; }

        /// <summary>
        /// The module's version string — the <c>version</c> column, nullable. Named
        /// <see cref="ModuleDefinitionVersion"/> rather than <c>Version</c> because
        /// <see cref="AggregateRoot{TId}.Version"/> (a <c>uint</c> concurrency token) shadows that name; mapped
        /// to the <c>version</c> column in <see cref="FrameworkStoreModel"/>, exactly as
        /// <c>ThemeAggregate.ThemeVersion</c> is mapped to Theme's.
        /// </summary>
        public string ModuleDefinitionVersion { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="ModuleDefinition"/> for an update or a delete. The store never calls this for an insert —
        /// the database assigns the identity.
        /// </summary>
        internal void AssignId(int moduleDefinitionId) => Id = moduleDefinitionId;
    }
}
