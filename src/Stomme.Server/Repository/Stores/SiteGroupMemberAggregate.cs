using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SiteGroupMember aggregate root — the persistence-side model of the framework store's
    /// <c>SiteGroupMember</c> table, reachable only through <see cref="ISiteGroupMemberStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SiteGroupMember joins one <see cref="Site"/> to one <see cref="SiteGroup"/> and records when that site
    /// was last synchronized. The table carries a UNIQUE index on (site_id, site_group_id) — added by
    /// <c>10010001_AddSiteGroups</c> and NOT visible on <c>SiteGroupMemberEntityBuilder</c>, which is why it was
    /// read off the live database rather than the builder. A site therefore appears at most once per group, and
    /// a duplicate insert raises a provider unique-violation rather than being silently absorbed.
    /// </para>
    /// <para>
    /// <b>NOTE WHAT IS ABSENT: <c>SiteGroup</c>.</b> The contract's <c>SiteGroupMember.SiteGroup</c> was a
    /// MAPPED navigation into the <see cref="SiteGroup"/> CLR type and the harvested repository eager-loaded it
    /// with <c>.Include(item =&gt; item.SiteGroup)</c>. This aggregate references the group BY ID only
    /// (D7/D19) — a navigation would resurrect <see cref="SiteGroup"/> as a second entity type on the table
    /// <see cref="SiteGroupAggregate"/> owns and break the model at startup. That failure was OBSERVED for this
    /// cluster, not assumed; see the remarks on <see cref="SiteGroupAggregate"/>.
    /// </para>
    /// <para>
    /// <b>The navigation was NOT dead weight, and severing it was not free.</b> Twenty-two production reads take
    /// <c>siteGroupMember.SiteGroup.Type</c> or <c>.PrimarySiteId</c> — eighteen in
    /// <c>SynchronizationJob</c>, two in <c>ServerSiteService.GetSitesAsync</c>, two in the Admin
    /// <c>Site/Index.razor</c>. <see cref="SiteGroupMemberRepository"/> therefore populates the contract's
    /// (now <c>[NotMapped]</c>) <c>SiteGroup</c> member by hand from a second session query, which is
    /// behaviour-equivalent because the harvested <c>.Include</c> carried NO join predicate: the filter at
    /// <c>SiteGroupMemberRepository</c> was on this table's own columns, so D22 rule 9 does not apply and no
    /// database join was lost. Pinned by <c>SeveringTheSiteGroupNavigationTests</c>.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class SiteGroupMemberAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// The <see cref="SiteGroup"/> this membership belongs to, by id — the reference that
        /// replaces the severed navigation. The <c>site_group_id</c> column still carries a cascading foreign
        /// key to <c>site_group</c> in the database; what is gone is the EF NAVIGATION, not the constraint.
        /// </summary>
        public int SiteGroupId { get; set; }

        /// <summary>The <see cref="Site"/> that is a member of the group, by id.</summary>
        public int SiteId { get; set; }

        /// <summary>
        /// When this site was last synchronized. Nullable, and <c>SynchronizationJob</c> reads null as "never"
        /// and immediately writes <see cref="DateTime.MinValue"/> to mean "initializing".
        /// </summary>
        public DateTime? SynchronizedOn { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="SiteGroupMember"/> for an update or a delete. The store never calls this for an insert —
        /// the database assigns the identity.
        /// </summary>
        internal void AssignId(int siteGroupMemberId) => Id = siteGroupMemberId;
    }
}
