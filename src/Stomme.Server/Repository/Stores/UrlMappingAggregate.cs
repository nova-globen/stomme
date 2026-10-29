using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The UrlMapping aggregate root — the persistence-side model of the framework store's <c>UrlMapping</c>
    /// table, reachable only through <see cref="IUrlMappingStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A UrlMapping is one requested URL within one <see cref="Site"/>, optionally redirected to
    /// <see cref="MappedUrl"/>. A leaf: nothing navigates into it and it navigates nowhere.
    /// <see cref="SiteId"/> is a reference BY ID and always was — this cluster had no navigation to
    /// sever.
    /// </para>
    /// <para>
    /// <b>NOT <see cref="IAuditable"/> and NOT <see cref="IDeletable"/>.</b> VERIFIED against the migrated
    /// PostgreSQL database: the <c>url_mapping</c> table had EIGHT columns — url_mapping_id, site_id, url,
    /// mapped_url, requests, created_on, requested_on, referrer — and none of the audit or soft-delete ones.
    /// D57 adds a NINTH, <c>url_normalized</c> (nullable varchar(750)), the case-insensitive lookup key; still
    /// no audit or soft-delete column.
    /// <see cref="DbContextUtils.SaveChanges"/> decides by RUNTIME type test, so declaring either interface
    /// here would target columns that do not exist. <see cref="CreatedOn"/> is the mapping's OWN column (when
    /// the URL was first requested), set by <see cref="UrlMappingRepository"/>, not an audit stamp.
    /// </para>
    /// <para>
    /// <b>DO NOT TRUST <c>UrlMappingEntityBuilder</c>.</b> It is wrong about three things at once and every one
    /// of them is observable. It omits <see cref="Referrer"/> entirely — the column is added, nullable at
    /// varchar(2048), by <c>10000301_FixUrlMappingReferrer</c> (its predecessor <c>10000203</c> is a
    /// deliberately empty stub, because the original shipped without the nullable specification and failed on
    /// upgrade). It declares <see cref="Url"/> and <see cref="MappedUrl"/> at varchar(500), while
    /// <c>03010001_ExpandVisitorAndUrlMappingUrls</c> widens MappedUrl to 2048 and Url to 750 — 750 rather
    /// than 2048 because Url is an INDEX column and MySQL caps an index at 3072 bytes (750×4+4 = 3004). That
    /// 750 is not cosmetic: <see cref="UrlMappingRepository.GetUrlMappingAsync"/> truncates the incoming url to
    /// exactly 750 before querying. And the UNIQUE index itself — <c>ix_url_mapping (site_id, url)</c> — is
    /// declared by that same migration, not by the builder (the D45 lesson, again).
    /// </para>
    /// <para>
    /// <b>THE UNIQUE INDEX IS BEHAVIOUR, AND IT DOES NOT CROSS TO RAVENDB.</b>
    /// <see cref="UrlMappingRepository.GetUrlMappingAsync"/> wraps its insert in a bare <c>try/catch</c> whose
    /// comment reads "ignore duplicate key exception which can be caused by a race condition" — the relational
    /// unique index IS the concurrency control for two simultaneous requests to the same broken URL. RavenDB
    /// has no equivalent, so on that provider the race inserts two documents instead of raising. Inherited from
    /// the seam rather than introduced here (the same is true of every unique index on every migrated
    /// cluster); named because this cluster is the first whose PRODUCTION CODE CATCHES the exception the index
    /// raises. See src/Stomme.Persistence.RavenDb/README.md and src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class UrlMappingAggregate : AggregateRoot<int>
    {
        /// <summary>The <see cref="Site"/> this mapping belongs to, by id. NOT NULL.</summary>
        public int SiteId { get; set; }

        /// <summary>
        /// The requested URL, relative and without a leading slash. NOT NULL, varchar(750) — the width is
        /// bounded by the unique index, not by the builder's 500. See the remarks. The STORED CASE: this is
        /// what the admin sees and edits.
        /// </summary>
        /// <remarks>
        /// Setting it maintains <see cref="UrlNormalized"/>. See the remarks there for why the derivation
        /// lives HERE and not in <see cref="UrlMappingMapper"/> — the same placement as
        /// <see cref="FolderAggregate.Path"/> / <see cref="FileAggregate.Name"/>.
        /// </remarks>
        public string Url
        {
            get => _url;
            set
            {
                _url = value;
                UrlNormalized = NormalizedText.Normalize(value);
            }
        }

        private string _url;

        /// <summary>
        /// The lowercase lookup key for <see cref="Url"/> — Identity's <c>NormalizedUserName</c> to
        /// <see cref="Url"/>'s <c>UserName</c>. Column <c>url_normalized</c>, nullable varchar(750), added
        /// and backfilled by <c>10020004_AddUrlMappingNormalizedColumn</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This column is why the two providers agree on the URL lookup at all.</b> DRIVEN on the real
        /// hosts: a row stored <c>Foo/Bar</c> looked up as <c>foo/bar</c> MISSED on PostgreSQL (whose
        /// <c>==</c> is case-SENSITIVE under the column collation) and HIT on RavenDB (whose <c>==</c> is
        /// case-INSENSITIVE). <c>App.razor</c>'s 404 handler runs that lookup on every unresolved request. The
        /// harvested read filtered <see cref="Url"/> raw; <see cref="UrlMappingRepository.GetUrlMappingAsync(int, string, string)"/>
        /// now normalizes its argument and filters THIS column with plain <c>==</c>, which says the same thing
        /// on both providers (for two lowercase strings ordinal and case-insensitive equality coincide). This
        /// matches the existing Folder/File case-INSENSITIVE precedent (<see cref="NormalizedText"/>).
        /// </para>
        /// <para>
        /// <b>Derived in <see cref="Url"/>'s setter, not in the mapper</b>, for the reason
        /// <see cref="FolderAggregate.PathNormalized"/> spells out: the mapper is not the only way in (the
        /// conformance suite writes aggregates straight through <see cref="IUrlMappingStore"/>), and a write
        /// that bypassed it would persist NULL here, which reads as "this URL is unknown" on both providers,
        /// silently. Unrepresentable beats discouraged. Never mapped back onto the <see cref="UrlMapping"/>
        /// contract — a storage concern, not wire state.
        /// </para>
        /// <para>
        /// <b>The relational UNIQUE index stays on <see cref="Url"/>, NOT this column</b> — see
        /// <c>10020004_AddUrlMappingNormalizedColumn</c>. It is case-SENSITIVE, so <c>Foo/Bar</c> and
        /// <c>foo/bar</c> may coexist within a site exactly as before; the normalized column is a LOOKUP key
        /// only, mirroring D49's choice for Folder/File.
        /// </para>
        /// </remarks>
        public string UrlNormalized { get; private set; }

        /// <summary>
        /// The URL to redirect to, or EMPTY STRING for a broken URL that has merely been captured. NOT NULL,
        /// varchar(2048).
        /// <para>
        /// <b>Empty-versus-null is the cluster's whole read predicate.</b> <c>GetUrlMappings(siteId, isMapped)</c>
        /// splits the table on <c>string.IsNullOrEmpty(MappedUrl)</c>: mapped rows are the redirects the admin
        /// manages, unmapped rows are the broken-link report. The column is NOT NULL, so in practice only the
        /// empty half of that predicate can match — but the predicate tests both, and a provider that
        /// translates only one arm silently serves the wrong half of the table. See src/Stomme.Server/README.md (persistence seam).
        /// </para>
        /// </summary>
        public string MappedUrl { get; set; }

        /// <summary>How many times the URL has been requested, all time. Incremented on every request that resolves through it.</summary>
        public int Requests { get; set; }

        /// <summary>
        /// The last referrer, set only when the link originated externally. NULLABLE, varchar(2048) — a later
        /// column, absent from <c>UrlMappingEntityBuilder</c>. See the remarks.
        /// </summary>
        public string Referrer { get; set; }

        /// <summary>
        /// When the URL was FIRST requested for the site. The mapping's OWN column — NOT an audit stamp (see
        /// the remarks).
        /// </summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// When the URL was LAST requested. The sort and retention key of <c>DeleteUrlMappings(siteId, age)</c>.
        /// </summary>
        public DateTime RequestedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="UrlMapping"/> for an update or a delete. The store never calls this for an insert — the
        /// database assigns the identity.
        /// </summary>
        internal void AssignId(int urlMappingId) => Id = urlMappingId;
    }
}
