using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Visitor aggregate root — the persistence-side model of the framework store's <c>Visitor</c> table,
    /// reachable only through <see cref="IVisitorStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Visitor is one browser's history with one <see cref="Site"/>, correlated by cookie and optionally by
    /// IP address. <see cref="SiteId"/> and <see cref="UserId"/> are references BY ID — the
    /// <c>Visitor.User</c> navigation was severed when the User table moved onto the seam, so nothing
    /// about this cluster needed a navigation severed for it.
    /// </para>
    /// <para>
    /// <b>NOT <see cref="IAuditable"/> and NOT <see cref="IDeletable"/>, and both absences are load-bearing.</b>
    /// <see cref="DbContextUtils.SaveChanges"/> decides whether to stamp by RUNTIME type test
    /// (<c>item.Entity is IAuditable</c>). VERIFIED against the migrated PostgreSQL database: the
    /// <c>visitor</c> table has ELEVEN columns — visitor_id, site_id, user_id, visits, ip_address, user_agent,
    /// language, created_on, visited_on, referrer, url — and NOT ONE of created_by/modified_by/modified_on or
    /// deleted_by/deleted_on/is_deleted. <c>VisitorEntityBuilder</c> derives from <c>BaseEntityBuilder</c> and
    /// calls neither <c>AddAuditableColumns</c> nor <c>AddDeletableColumns</c>. Declare either interface here
    /// and the stamp would target columns that do not exist.
    /// </para>
    /// <para>
    /// <b>DO NOT TRUST <c>VisitorEntityBuilder</c> FOR THE COLUMN LIST.</b> It builds NINE columns and omits
    /// <see cref="Referrer"/> and <see cref="Url"/> entirely. Both are real: <c>03000105_AddVisitorReferrer</c>
    /// adds BOTH of them nullable at varchar(500) — despite its name mentioning only the referrer — and
    /// <c>03010001_ExpandVisitorAndUrlMappingUrls</c> then widens <c>Url</c> alone to varchar(2048), leaving
    /// <c>Referrer</c> at 500. VERIFIED against the migrated PostgreSQL database, which agrees: referrer
    /// varchar(500), url varchar(2048), both nullable. The live schema is the contract; the builder is a
    /// starting point that later migrations moved away from. Pinned by
    /// <c>The_aggregate_persists_exactly_the_columns_the_harvested_entity_did</c>.
    /// </para>
    /// <para>
    /// <see cref="CreatedOn"/> below is the visitor's OWN column — when the visitor was FIRST seen, set by
    /// <c>App.razor</c> — not an audit stamp, exactly as <see cref="NotificationAggregate.CreatedOn"/> is.
    /// <see cref="AuditStamp.IsAuditable"/> requires all four members and so correctly declines to stamp this
    /// type on both providers.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class VisitorAggregate : AggregateRoot<int>
    {
        /// <summary>The <see cref="Site"/> this visitor visited, by id. NOT NULL.</summary>
        public int SiteId { get; set; }

        /// <summary>
        /// The <see cref="User"/> this visitor is, by id, or null for an anonymous visitor. Nullable, and the
        /// nullability is behaviour: <c>VisitorRepository.GetVisitors</c> only populates the severed
        /// <c>Visitor.User</c> for the non-null ones, and Admin &gt; Visitors renders
        /// <c>context.User.DisplayName</c> guarded by <c>context.UserId != null</c>.
        /// </summary>
        public int? UserId { get; set; }

        /// <summary>How many times this visitor has visited the site. Incremented by <c>App.razor</c> per cookie expiry window.</summary>
        public int Visits { get; set; }

        /// <summary>The last recorded IP address. NOT NULL, varchar(50). Also the correlation key of <c>GetVisitor(siteId, IPAddress)</c>.</summary>
        public string IPAddress { get; set; }

        /// <summary>The last recorded user agent. NOT NULL, varchar(256) — <c>App.razor</c> truncates to fit.</summary>
        public string UserAgent { get; set; }

        /// <summary>The last recorded Accept-Language. NOT NULL, varchar(50).</summary>
        public string Language { get; set; }

        /// <summary>
        /// The last recorded URL. NULLABLE, varchar(2048) — added by <c>03000105_AddVisitorReferrer</c> at 500
        /// and widened by <c>03010001_ExpandVisitorAndUrlMappingUrls</c>. Absent from
        /// <c>VisitorEntityBuilder</c>; see the remarks.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// The last recorded referrer. NULLABLE, varchar(500) — a later column
        /// (<c>03000105_AddVisitorReferrer</c>), absent from <c>VisitorEntityBuilder</c>.
        /// </summary>
        public string Referrer { get; set; }

        /// <summary>
        /// When the visitor FIRST visited the site. The visitor's OWN column, set by <c>App.razor</c> — NOT an
        /// audit stamp (see the remarks).
        /// </summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// When the visitor LAST visited. The filter key of <c>GetVisitors(siteId, fromDate)</c> and the sort
        /// and retention key of <c>DeleteVisitors(siteId, age)</c>.
        /// </summary>
        public DateTime VisitedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Visitor"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int visitorId) => Id = visitorId;
    }
}
