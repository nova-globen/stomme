using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The PageModule aggregate root — the persistence-side model of the framework store's <c>PageModule</c>
    /// table, reachable only through <see cref="IPageModuleStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A PageModule is one <see cref="Module"/> instance placed on one <see cref="Page"/>: the join row that
    /// says WHERE a module appears and in what order. <b>It is the most render-critical table on the seam</b> —
    /// every page render enumerates its page's modules to decide what to draw, so a fault here is a blank page,
    /// not a degraded report.
    /// </para>
    /// <para>
    /// <b>This cluster was migrated LAST of its group and it is the only one that already had both its
    /// references severed.</b> <see cref="PageId"/> and <see cref="ModuleId"/> are references BY ID
    /// and have been since the Page and Module tables moved onto the seam — the
    /// <c>.Include(item =&gt; item.Page)</c> / <c>.Include(item =&gt; item.Module)</c> eager loads became second
    /// queries then, and <see cref="PageModuleRepository"/> has stitched the contract's Page and Module members
    /// on by hand ever since. So the navigations were not this increment's problem; only the row itself moved.
    /// </para>
    /// <para>
    /// <b>IAuditable AND IDeletable, and BOTH are load-bearing.</b>
    /// <see cref="DbContextUtils.SaveChanges"/> stamps by RUNTIME type test — <c>item.Entity is IAuditable</c>
    /// and <c>item.Entity is IDeletable</c>. Before this increment the tracked entity was the
    /// <see cref="PageModule"/> contract, which implements both; now it is this aggregate. Drop either
    /// interface here and everything still compiles, the cluster still renders, and the stamps silently stop.
    /// PageModule is the FIRST aggregate on the seam to need both at once —
    /// <see cref="NotificationAggregate"/> is IDeletable but not IAuditable, and everything else is IAuditable
    /// but not IDeletable. VERIFIED against the migrated PostgreSQL database: <c>page_module</c> carries all
    /// seven columns (created_by, created_on, modified_by, modified_on, deleted_by, deleted_on, is_deleted),
    /// which is what <c>PageModuleEntityBuilder</c>'s <c>DeletableAuditableBaseEntityBuilder</c> base produces.
    /// Mutation-checked both ways by <c>PageModuleAggregateMappingTests</c>.
    /// </para>
    /// <para>
    /// <b>DO NOT TRUST <c>PageModuleEntityBuilder</c> FOR THE COLUMN LIST</b> — the standing lesson, and true
    /// here too. It builds seven columns plus the audit/deletable sets and omits <see cref="EffectiveDate"/>,
    /// <see cref="ExpiryDate"/>, <see cref="Header"/> and <see cref="Footer"/>, all four of which are real and
    /// added by later migrations. The live schema is the contract.
    /// </para>
    /// <para>
    /// <b>The IDeletable stamp does not cross to RavenDB</b>, exactly as documented on
    /// <see cref="NotificationAggregate"/>: <see cref="AuditStamp"/> does not port the soft-delete rule,
    /// because it compares the incoming <c>IsDeleted</c> against the database's value and a document provider
    /// cannot do that without an extra load. A PageModule soft-deleted through the RavenDB store records
    /// IsDeleted but not DeletedBy/DeletedOn. Inherited, not introduced.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class PageModuleAggregate : AggregateRoot<int>, IAuditable, IDeletable
    {
        /// <summary>The <see cref="Page"/> this instance sits on, by id. NOT NULL, and FK-cascaded from Page.</summary>
        public int PageId { get; set; }

        /// <summary>
        /// The <see cref="Module"/> being instanced, by id. NOT NULL. The FK to Module is
        /// <c>NoAction</c>, not cascade — <see cref="PageModuleRepository.DeletePageModuleAsync"/> deletes the
        /// orphaned module by hand once its last instance goes.
        /// </summary>
        public int ModuleId { get; set; }

        /// <summary>The title drawn in the container chrome. NOT NULL, varchar(200).</summary>
        public string Title { get; set; }

        /// <summary>The theme pane this instance renders into. NOT NULL, varchar(50). THE render-path column.</summary>
        public string Pane { get; set; }

        /// <summary>The sort position within <see cref="Pane"/>. NOT NULL.</summary>
        public int Order { get; set; }

        /// <summary>The Razor container type wrapping the instance. NOT NULL, varchar(200).</summary>
        public string ContainerType { get; set; }

        /// <summary>Start of the validity window, or null for "always". NULLABLE — a later column, absent from the builder.</summary>
        public DateTime? EffectiveDate { get; set; }

        /// <summary>End of the validity window, or null for "never expires". NULLABLE — a later column, absent from the builder.</summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>Content rendered above the instance. NULLABLE <c>text</c> — a later column, absent from the builder.</summary>
        public string Header { get; set; }

        /// <summary>Content rendered below the instance. NULLABLE <c>text</c> — a later column, absent from the builder.</summary>
        public string Footer { get; set; }

        #region IAuditable

        /// <inheritdoc />
        public string CreatedBy { get; set; }

        /// <inheritdoc />
        public DateTime CreatedOn { get; set; }

        /// <inheritdoc />
        public string ModifiedBy { get; set; }

        /// <inheritdoc />
        public DateTime ModifiedOn { get; set; }

        #endregion

        #region IDeletable

        /// <inheritdoc />
        public string DeletedBy { get; set; }

        /// <inheritdoc />
        public DateTime? DeletedOn { get; set; }

        /// <inheritdoc />
        public bool IsDeleted { get; set; }

        #endregion

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="PageModule"/> for an update or a delete. The store never calls this for an insert — the
        /// database assigns the identity.
        /// </summary>
        internal void AssignId(int pageModuleId) => Id = pageModuleId;
    }
}
