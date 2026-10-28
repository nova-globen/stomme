using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Site aggregate root — the persistence-side model of the framework store's <c>Site</c> table,
    /// reachable only through <see cref="ISiteStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately NOT <see cref="Site"/>. <see cref="Site"/> is the client/server wire contract
    /// (it carries <c>[NotMapped]</c> view state — Pages, Languages, Themes, User, Fingerprint — and is
    /// serialized to the Blazor client), so it cannot also be the aggregate: <see cref="AggregateRoot{TId}"/>
    /// contributes <c>Id</c>, <c>Version</c> and <c>DomainEvents</c>, and its <c>Version</c> collides
    /// head-on with Site's own <c>Version</c> column (the <c>ISiteMigration</c> configuration version, a
    /// string). <see cref="SiteMapper"/> maps between the two at the repository boundary.
    /// </para>
    /// <para>
    /// The model is anemic — a faithful port of the harvested entity, not a re-modelling. Behaviour that
    /// belongs on the aggregate (site provisioning invariants, the render-mode/runtime combination rules,
    /// soft-delete as a domain operation rather than a settable flag) still lives in
    /// <see cref="SiteRepository"/> and <c>SiteService</c>. Deliberate: the goal of this increment is the
    /// store on the seam, not a perfect domain model. Raising domain events from here is the natural next
    /// step and would let <c>ISyncManager</c>'s cache-invalidation events become domain events.
    /// </para>
    /// </remarks>
    public class SiteAggregate : AggregateRoot<int>, IAuditable, IDeletable
    {
        public string Name { get; set; }

        public string TimeZoneId { get; set; }

        public string CultureCode { get; set; }

        public int? LogoFileId { get; set; }

        public int? FaviconFileId { get; set; }

        public string DefaultThemeType { get; set; }

        public string DefaultContainerType { get; set; }

        public string AdminContainerType { get; set; }

        public string AdminThemeType { get; set; }

        public bool PwaIsEnabled { get; set; }

        public int? PwaAppIconFileId { get; set; }

        public int? PwaSplashIconFileId { get; set; }

        public bool AllowRegistration { get; set; }

        public bool VisitorTracking { get; set; }

        public bool CaptureBrokenUrls { get; set; }

        public string SiteGuid { get; set; }

        public string RenderMode { get; set; }

        public string Runtime { get; set; }

        public bool Prerender { get; set; }

        public bool Hybrid { get; set; }

        public bool EnhancedNavigation { get; set; }

        /// <summary>
        /// The site's configuration version, tracked by <c>ISiteMigration</c>. Mapped to the <c>Version</c>
        /// column; named differently here because <see cref="AggregateRoot{TId}.Version"/> already owns that
        /// member name (see <see cref="FrameworkStoreModel"/>).
        /// </summary>
        public string SiteVersion { get; set; }

        public string HeadContent { get; set; }

        public string BodyContent { get; set; }

        public bool IsDeleted { get; set; }

        public string DeletedBy { get; set; }

        public DateTime? DeletedOn { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Site"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int siteId) => Id = siteId;
    }
}
