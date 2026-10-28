using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="PageAggregate"/> (the persisted aggregate) and <see cref="Page"/> (the
    /// client/server wire contract) at the <see cref="PageRepository"/> boundary. Only the columns the
    /// harvested schema actually has are mapped: <see cref="Page"/>'s <c>[NotMapped]</c> members (Level,
    /// HasChildren, UpdateModulePermissions, PermissionList, Settings, Panes, Resources) are view state that
    /// other layers populate, and were never persisted — they stay untouched here, exactly as EF left them
    /// before this cluster moved to the seam.
    /// </summary>
    public static class PageMapper
    {
        public static Page ToModel(PageAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Page
            {
                PageId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Path = aggregate.Path,
                ParentId = aggregate.ParentId,
                Name = aggregate.Name,
                Title = aggregate.Title,
                Order = aggregate.Order,
                Url = aggregate.Url,
                ThemeType = aggregate.ThemeType,
                DefaultContainerType = aggregate.DefaultContainerType,
                HeadContent = aggregate.HeadContent,
                BodyContent = aggregate.BodyContent,
                Icon = aggregate.Icon,
                IsNavigation = aggregate.IsNavigation,
                IsClickable = aggregate.IsClickable,
                IsPersonalizable = aggregate.IsPersonalizable,
                UserId = aggregate.UserId,
                EffectiveDate = aggregate.EffectiveDate,
                ExpiryDate = aggregate.ExpiryDate,
                IsDeleted = aggregate.IsDeleted,
                DeletedBy = aggregate.DeletedBy,
                DeletedOn = aggregate.DeletedOn,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static PageAggregate ToAggregate(Page page, bool assignId)
        {
            var aggregate = new PageAggregate
            {
                SiteId = page.SiteId,
                Path = page.Path,
                ParentId = page.ParentId,
                Name = page.Name,
                Title = page.Title,
                Order = page.Order,
                Url = page.Url,
                ThemeType = page.ThemeType,
                DefaultContainerType = page.DefaultContainerType,
                HeadContent = page.HeadContent,
                BodyContent = page.BodyContent,
                Icon = page.Icon,
                IsNavigation = page.IsNavigation,
                IsClickable = page.IsClickable,
                IsPersonalizable = page.IsPersonalizable,
                UserId = page.UserId,
                EffectiveDate = page.EffectiveDate,
                ExpiryDate = page.ExpiryDate,
                IsDeleted = page.IsDeleted,
                DeletedBy = page.DeletedBy,
                DeletedOn = page.DeletedOn,
                CreatedBy = page.CreatedBy,
                CreatedOn = page.CreatedOn,
                ModifiedBy = page.ModifiedBy,
                ModifiedOn = page.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(page.PageId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Page"/> reflects the
        /// persisted row — the harvested repository handed back the tracked entity itself and callers
        /// (notably <c>SiteRepository.CreatePages</c>, which reads <c>page.PageId</c> straight after
        /// <c>AddPage</c> to attach page modules) rely on it.
        /// </summary>
        public static void CopyPersistedValues(PageAggregate aggregate, Page page)
        {
            page.PageId = aggregate.Id;
            page.CreatedBy = aggregate.CreatedBy;
            page.CreatedOn = aggregate.CreatedOn;
            page.ModifiedBy = aggregate.ModifiedBy;
            page.ModifiedOn = aggregate.ModifiedOn;
            page.DeletedBy = aggregate.DeletedBy;
            page.DeletedOn = aggregate.DeletedOn;
        }
    }
}
