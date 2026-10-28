using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="SiteAggregate"/> (the persisted aggregate) and <see cref="Site"/> (the
    /// client/server wire contract) at the <see cref="SiteRepository"/> boundary. Only the columns the
    /// harvested schema actually has are mapped: <see cref="Site"/>'s <c>[NotMapped]</c> members (Settings,
    /// Pages, Languages, Themes, User, Fingerprint, TenantId, ImageFiles, UploadableFiles,
    /// SiteTemplateType) are view state that other layers populate, and were never persisted — they stay
    /// untouched here, exactly as EF left them before this cluster moved to the seam.
    /// </summary>
    public static class SiteMapper
    {
        public static Site ToModel(SiteAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Site
            {
                SiteId = aggregate.Id,
                Name = aggregate.Name,
                TimeZoneId = aggregate.TimeZoneId,
                CultureCode = aggregate.CultureCode,
                LogoFileId = aggregate.LogoFileId,
                FaviconFileId = aggregate.FaviconFileId,
                DefaultThemeType = aggregate.DefaultThemeType,
                DefaultContainerType = aggregate.DefaultContainerType,
                AdminContainerType = aggregate.AdminContainerType,
                AdminThemeType = aggregate.AdminThemeType,
                PwaIsEnabled = aggregate.PwaIsEnabled,
                PwaAppIconFileId = aggregate.PwaAppIconFileId,
                PwaSplashIconFileId = aggregate.PwaSplashIconFileId,
                AllowRegistration = aggregate.AllowRegistration,
                VisitorTracking = aggregate.VisitorTracking,
                CaptureBrokenUrls = aggregate.CaptureBrokenUrls,
                SiteGuid = aggregate.SiteGuid,
                RenderMode = aggregate.RenderMode,
                Runtime = aggregate.Runtime,
                Prerender = aggregate.Prerender,
                Hybrid = aggregate.Hybrid,
                EnhancedNavigation = aggregate.EnhancedNavigation,
                Version = aggregate.SiteVersion,
                HeadContent = aggregate.HeadContent,
                BodyContent = aggregate.BodyContent,
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
        public static SiteAggregate ToAggregate(Site site, bool assignId)
        {
            var aggregate = new SiteAggregate
            {
                Name = site.Name,
                TimeZoneId = site.TimeZoneId,
                CultureCode = site.CultureCode,
                LogoFileId = site.LogoFileId,
                FaviconFileId = site.FaviconFileId,
                DefaultThemeType = site.DefaultThemeType,
                DefaultContainerType = site.DefaultContainerType,
                AdminContainerType = site.AdminContainerType,
                AdminThemeType = site.AdminThemeType,
                PwaIsEnabled = site.PwaIsEnabled,
                PwaAppIconFileId = site.PwaAppIconFileId,
                PwaSplashIconFileId = site.PwaSplashIconFileId,
                AllowRegistration = site.AllowRegistration,
                VisitorTracking = site.VisitorTracking,
                CaptureBrokenUrls = site.CaptureBrokenUrls,
                SiteGuid = site.SiteGuid,
                RenderMode = site.RenderMode,
                Runtime = site.Runtime,
                Prerender = site.Prerender,
                Hybrid = site.Hybrid,
                EnhancedNavigation = site.EnhancedNavigation,
                SiteVersion = site.Version,
                HeadContent = site.HeadContent,
                BodyContent = site.BodyContent,
                IsDeleted = site.IsDeleted,
                DeletedBy = site.DeletedBy,
                DeletedOn = site.DeletedOn,
                CreatedBy = site.CreatedBy,
                CreatedOn = site.CreatedOn,
                ModifiedBy = site.ModifiedBy,
                ModifiedOn = site.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(site.SiteId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Site"/> reflects the
        /// persisted row — the harvested repository handed back the tracked entity itself and callers
        /// (notably the installer, which reads <c>site.SiteId</c> straight after <c>AddSite</c>) rely on it.
        /// </summary>
        public static void CopyPersistedValues(SiteAggregate aggregate, Site site)
        {
            site.SiteId = aggregate.Id;
            site.CreatedBy = aggregate.CreatedBy;
            site.CreatedOn = aggregate.CreatedOn;
            site.ModifiedBy = aggregate.ModifiedBy;
            site.ModifiedOn = aggregate.ModifiedOn;
            site.DeletedBy = aggregate.DeletedBy;
            site.DeletedOn = aggregate.DeletedOn;
        }
    }
}
