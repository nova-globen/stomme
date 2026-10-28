using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="PageModuleAggregate"/> (the persisted aggregate) and
    /// <see cref="PageModule"/> (the client/server wire contract) at the <see cref="PageModuleRepository"/>
    /// boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="PageModule.Page"/> and <see cref="PageModule.Module"/> are deliberately absent: both are
    /// <c>[NotMapped]</c> view state that D19 and D21 severed from EF navigations to ids, and
    /// <see cref="PageModuleRepository"/> re-populates them from the Page and Module aggregates AFTER this
    /// mapper has run. Round-tripping a PageModule through here LOSES both by design —
    /// <see cref="PageModule.PageId"/> and <see cref="PageModule.ModuleId"/> are the persisted references.
    /// Pinned by <c>The_mapper_does_not_round_trip_the_unmapped_navigations</c>.
    /// </remarks>
    public static class PageModuleMapper
    {
        public static PageModule ToModel(PageModuleAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new PageModule
            {
                PageModuleId = aggregate.Id,
                PageId = aggregate.PageId,
                ModuleId = aggregate.ModuleId,
                Title = aggregate.Title,
                Pane = aggregate.Pane,
                Order = aggregate.Order,
                ContainerType = aggregate.ContainerType,
                EffectiveDate = aggregate.EffectiveDate,
                ExpiryDate = aggregate.ExpiryDate,
                Header = aggregate.Header,
                Footer = aggregate.Footer,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn,
                DeletedBy = aggregate.DeletedBy,
                DeletedOn = aggregate.DeletedOn,
                IsDeleted = aggregate.IsDeleted
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static PageModuleAggregate ToAggregate(PageModule pageModule, bool assignId)
        {
            var aggregate = new PageModuleAggregate
            {
                PageId = pageModule.PageId,
                ModuleId = pageModule.ModuleId,
                Title = pageModule.Title,
                Pane = pageModule.Pane,
                Order = pageModule.Order,
                ContainerType = pageModule.ContainerType,
                EffectiveDate = pageModule.EffectiveDate,
                ExpiryDate = pageModule.ExpiryDate,
                Header = pageModule.Header,
                Footer = pageModule.Footer,
                CreatedBy = pageModule.CreatedBy,
                CreatedOn = pageModule.CreatedOn,
                ModifiedBy = pageModule.ModifiedBy,
                ModifiedOn = pageModule.ModifiedOn,
                DeletedBy = pageModule.DeletedBy,
                DeletedOn = pageModule.DeletedOn,
                IsDeleted = pageModule.IsDeleted
            };

            if (assignId)
            {
                aggregate.AssignId(pageModule.PageModuleId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write: the generated identity and the stamps
        /// <see cref="DbContextUtils"/> applied. The harvested repository handed back the tracked entity itself
        /// and callers rely on it — <c>ModuleController.Post</c> creates a module and its first PageModule and
        /// returns the result straight to the client, which navigates to the new instance by id.
        /// </summary>
        /// <remarks>
        /// The soft-delete stamps are copied too, not just the audit ones: <c>DbContextUtils</c> writes
        /// DeletedBy/DeletedOn on the transition into IsDeleted, and <c>PageController</c>'s page delete flips
        /// a page's modules through exactly that transition.
        /// </remarks>
        public static void CopyPersistedValues(PageModuleAggregate aggregate, PageModule pageModule)
        {
            pageModule.PageModuleId = aggregate.Id;
            pageModule.CreatedBy = aggregate.CreatedBy;
            pageModule.CreatedOn = aggregate.CreatedOn;
            pageModule.ModifiedBy = aggregate.ModifiedBy;
            pageModule.ModifiedOn = aggregate.ModifiedOn;
            pageModule.DeletedBy = aggregate.DeletedBy;
            pageModule.DeletedOn = aggregate.DeletedOn;
            pageModule.IsDeleted = aggregate.IsDeleted;
        }
    }
}
