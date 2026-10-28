using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    /// <summary>
    /// Information about a <see cref="Module"/> instance on a <see cref="Page"/>
    /// </summary>
    public class PageModule : ModelBase, IDeletable
    {
        /// <summary>
        /// Internal ID to identify this instance.
        /// </summary>
        public int PageModuleId { get; set; }

        /// <summary>
        /// Reference to the <see cref="Page"/>.
        /// </summary>
        public int PageId { get; set; }

        /// <summary>
        /// Reference to the <see cref="Module"/>.
        /// </summary>
        public int ModuleId { get; set; }

        /// <summary>
        /// Module title. Will be shown in the Container if the container shows titles. 
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// The Pane which this module instance appears.
        /// </summary>
        public string Pane { get; set; }

        /// <summary>
        /// The sorting order / position in the Pane where this module appears. 
        /// </summary>
        public int Order { get; set; }

        /// <summary>
        /// Reference to a Razor Container which wraps this module instance.
        /// </summary>
        public string ContainerType { get; set; }

        /// <summary>
        /// Start of when this assignment is valid. See also <see cref="ExpiryDate"/>
        /// </summary>
        public DateTime? EffectiveDate { get; set; }

        /// <summary>
        /// End of when this assignment is valid. See also <see cref="EffectiveDate"/>
        /// </summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>
        /// Header content to include above the module instance in the UI
        /// </summary>
        public string Header { get; set; }

        /// <summary>
        /// Footer content to include below the module instance in the UI
        /// </summary>
        public string Footer { get; set; }

        #region IDeletable Properties

        public string DeletedBy { get; set; }
        public DateTime? DeletedOn { get; set; }
        public bool IsDeleted { get; set; }

        #endregion

        /// <summary>
        /// The <see cref="Module"/> itself. Populated by <c>PageModuleRepository</c> from the Module
        /// aggregate; <see cref="ModuleId"/> is the persisted reference.
        /// </summary>
        /// <remarks>
        /// <c>[NotMapped]</c> since the Module cluster moved onto the persistence seam (D21), for the same
        /// reason as <see cref="Page"/> below (D19). This was an EF navigation eager-loaded via
        /// <c>.Include(item => item.Module)</c> in every PageModuleRepository query. It cannot stay one: the
        /// Module table is now owned by <c>ModuleAggregate</c>, and an EF navigation to <see cref="Module"/>
        /// makes EF discover <see cref="Module"/> as a second entity type on the same table — a hard model
        /// error at startup.
        ///
        /// It is also the right shape independently: PageModule and Module are separate aggregates, and a
        /// cross-aggregate reference belongs by id, not by navigation (D7 — an aggregate is reached only
        /// through its own store). The value is unchanged for every consumer, and the database FK
        /// (FK_PageModule_Module) and its index (IX_PageModule_Module) are declared by
        /// PageModuleEntityBuilder in the migration set, not by this navigation, so the schema is untouched.
        ///
        /// The one thing that DID change shape: <c>GetPageModules(siteId)</c> used to filter
        /// <c>item.Module.SiteId == siteId</c> across the join. It now resolves the site's module ids through
        /// the Module cluster first and filters <c>ModuleId IN (...)</c> — still evaluated in the database,
        /// deliberately NOT an in-memory join. See src/Stomme.Server/README.md (persistence seam).
        /// </remarks>
        [NotMapped]
        public Module Module { get; set; }

        /// <summary>
        /// The <see cref="Page"/> itself. Populated by <c>PageModuleRepository</c> from the Page aggregate;
        /// <see cref="PageId"/> is the persisted reference.
        /// </summary>
        /// <remarks>
        /// <c>[NotMapped]</c> since the Page cluster moved onto the persistence seam (D19). This was an EF
        /// navigation, eager-loaded via <c>.Include(item => item.Page)</c>. It cannot stay one: the Page
        /// table is now owned by <c>PageAggregate</c>, and an EF navigation to <see cref="Page"/> makes EF
        /// discover <see cref="Page"/> as a second entity type on the same table — a hard model error
        /// ("The table 'page' cannot be used for entity type 'PageAggregate' since it is being used for
        /// entity type 'Page' ... there is no linking relationship").
        ///
        /// It is also the right shape independently: PageModule and Page are separate aggregates, and a
        /// cross-aggregate reference belongs by id, not by navigation (D7 — an aggregate is reached only
        /// through its own store). The value is unchanged for every consumer, and the database FK
        /// (FK_PageModule_Page) is declared by PageModuleEntityBuilder in the migration set, not by this
        /// navigation, so the schema is untouched.
        /// </remarks>
        [NotMapped]
        public Page Page { get; set; }
    }
}
