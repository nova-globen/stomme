using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Page aggregate root — the persistence-side model of the framework store's <c>Page</c> table,
    /// reachable only through <see cref="IPageStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="Page"/>, for the same reason <see cref="SiteAggregate"/> is not
    /// <see cref="Site"/>: <see cref="Page"/> is the client/server wire contract, carrying <c>[NotMapped]</c>
    /// view state (Level, HasChildren, PermissionList, Settings, Panes, Resources) that other layers
    /// populate. <see cref="PageMapper"/> maps between the two at the repository boundary, so the HTTP/client
    /// contract is byte-for-byte unchanged.
    /// </para>
    /// <para>
    /// Unlike Site, Page has no <c>Version</c> column, so nothing collides with
    /// <see cref="AggregateRoot{TId}.Version"/> by name — but the seam's token still has no column in the
    /// harvested schema and is ignored (see <see cref="FrameworkStoreModel"/>).
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Behaviour that
    /// belongs on it (the parent/child hierarchy invariants, soft-delete as a domain operation rather than a
    /// settable flag, the effective/expiry visibility window) still lives in <see cref="PageRepository"/>,
    /// <c>PageController</c> and the client. Deliberate: this increment moves the store onto the seam; brief
    /// 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class PageAggregate : AggregateRoot<int>, IAuditable, IDeletable
    {
        public int SiteId { get; set; }

        /// <summary>
        /// The page's path within its site, e.g. <c>about/contact</c>. The STORED CASE — what the admin sees
        /// and edits. Setting it maintains <see cref="PathNormalized"/>; see the remarks there for why the
        /// derivation lives HERE and not in <see cref="PageMapper"/> — the same placement as
        /// <see cref="FolderAggregate.Path"/> / <see cref="UrlMappingAggregate.Url"/>.
        /// </summary>
        public string Path
        {
            get => _path;
            set
            {
                _path = value;
                PathNormalized = NormalizedText.Normalize(value);
            }
        }

        private string _path;

        /// <summary>
        /// The lowercase lookup key for <see cref="Path"/> — Identity's <c>NormalizedUserName</c> to
        /// <see cref="Path"/>'s <c>UserName</c>. Column <c>path_normalized</c>, nullable varchar(256), added
        /// and backfilled by <c>10020005_AddPageNormalizedColumn</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This column is why the two providers agree on the page-path fallback lookup.</b>
        /// <see cref="PageRepository.GetPageAsync(string, int)"/> filtered <see cref="Path"/> raw with
        /// <c>==</c>, which means DIFFERENT things behind the seam — case-SENSITIVE on PostgreSQL,
        /// case-INSENSITIVE on RavenDB. That read is the render RESOLVE fallback
        /// (<c>SiteRouter.razor</c>, reached when the in-memory <c>OrdinalIgnoreCase</c> match at line 227
        /// misses) and the API page lookup (<c>PageController.Get</c>). The repository now normalizes its
        /// argument and filters THIS column with plain <c>==</c>, which says the same thing on both providers.
        /// Case-INSENSITIVE was chosen to MATCH <c>SiteRouter</c>'s own primary
        /// <c>StringComparison.OrdinalIgnoreCase</c> match and the Folder/File/UrlMapping precedent.
        /// </para>
        /// <para>
        /// <b>Derived in <see cref="Path"/>'s setter, not in the mapper</b>: the mapper is not the only way in
        /// (the conformance suite writes aggregates straight through <see cref="IPageStore"/>), and a write
        /// that bypassed it would persist NULL here, which reads as "this page is unknown" on both providers,
        /// silently. Never mapped back onto the <see cref="Page"/> contract — a storage concern, not wire
        /// state.
        /// </para>
        /// </remarks>
        public string PathNormalized { get; private set; }

        public int? ParentId { get; set; }

        public string Name { get; set; }

        public string Title { get; set; }

        public int Order { get; set; }

        public string Url { get; set; }

        public string ThemeType { get; set; }

        public string DefaultContainerType { get; set; }

        public string HeadContent { get; set; }

        public string BodyContent { get; set; }

        public string Icon { get; set; }

        public bool IsNavigation { get; set; }

        public bool IsClickable { get; set; }

        public bool IsPersonalizable { get; set; }

        /// <summary>
        /// The owner of a personalized page. A reference to the User aggregate BY ID — that cluster is not
        /// on the seam yet, and a cross-aggregate navigation would be wrong even once it is.
        /// </summary>
        public int? UserId { get; set; }

        public DateTime? EffectiveDate { get; set; }

        public DateTime? ExpiryDate { get; set; }

        public bool IsDeleted { get; set; }

        public string DeletedBy { get; set; }

        public DateTime? DeletedOn { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Page"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int pageId) => Id = pageId;
    }
}
