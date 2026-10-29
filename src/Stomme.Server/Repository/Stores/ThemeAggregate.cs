using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Theme aggregate root — the persistence-side model of the framework store's <c>Theme</c> table,
    /// reachable only through <see cref="IThemeStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads).
    /// A MASTER-database aggregate, like <see cref="JobAggregate"/> and the master half of Setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MASTER, not tenant.</b> Theme is installation-wide: the harvested <see cref="MasterDBContext"/> held
    /// <c>DbSet&lt;Theme&gt;</c> and the master migration set creates the table. So <see cref="IThemeStore"/> is
    /// bound to <see cref="MasterDBContext"/> and its reads go through <see cref="IMasterStoreSessionFactory"/>,
    /// NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>.
    /// </para>
    /// <para>
    /// <b>Only FOUR columns are persisted; the rest of <see cref="Theme"/> is <c>[NotMapped]</c> runtime state.</b>
    /// Verified column-for-column against the live migrated PostgreSQL database (stomme-pg-44357, host 45432):
    /// the <c>theme</c> table has exactly EIGHT columns — <c>theme_id</c>, <c>theme_name</c>, <c>name</c>,
    /// <c>version</c>, and the audit quartet (all four NOT NULL). Everything else on <see cref="Theme"/>
    /// (Themes/Containers/Resources/PermissionList/IsEnabled/…) is reconstructed from the loaded theme assemblies
    /// and from Settings/Permissions at read time; it never touches this table.
    /// </para>
    /// <para>
    /// <b><see cref="ThemeVersion"/> is the Theme table's own <c>version</c> STRING column</b> (migration
    /// <c>06010001</c>), NOT the aggregate's concurrency token. <see cref="AggregateRoot{TId}.Version"/> is a
    /// <c>uint</c> that shadows it by name — the exact <c>SiteAggregate.SiteVersion</c> situation — so the string
    /// is carried on a distinct property mapped to the <c>version</c> column, and the base <c>Version</c> is
    /// ignored in <see cref="FrameworkStoreModel"/>.
    /// </para>
    /// <para>
    /// <b>IAuditable, and it must stay that way.</b> <c>ThemeEntityBuilder</c> extends
    /// <c>AuditableBaseEntityBuilder</c> and calls <c>AddAuditableColumns</c>; the four audit columns are NOT NULL
    /// in the live database. <c>DbContextUtils</c> decides by RUNTIME TYPE TEST, so dropping <see cref="IAuditable"/>
    /// would compile and then FAIL every insert (created_by/modified_by are NOT NULL). It is NOT <c>IDeletable</c>
    /// — the live table has no is_deleted/deleted_by/deleted_on.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class ThemeAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The full namespace / identifier of the theme — the sync key the repository matches on.</summary>
        public string ThemeName { get; set; }

        /// <summary>The friendly, user-customizable name shown in the UI. Nullable in the database.</summary>
        public string Name { get; set; }

        /// <summary>
        /// The theme's assembly version string — the <c>version</c> column (migration <c>06010001</c>), nullable.
        /// Named <see cref="ThemeVersion"/> rather than <c>Version</c> because <see cref="AggregateRoot{TId}.Version"/>
        /// (a <c>uint</c> concurrency token) shadows that name; mapped to the <c>version</c> column in
        /// <see cref="FrameworkStoreModel"/>, exactly as <c>SiteAggregate.SiteVersion</c> is mapped to Site's.
        /// </summary>
        public string ThemeVersion { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Theme"/> for an
        /// update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int themeId) => Id = themeId;
    }
}
