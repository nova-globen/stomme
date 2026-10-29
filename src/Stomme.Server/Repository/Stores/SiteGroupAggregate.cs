using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SiteGroup aggregate root — the persistence-side model of the framework store's <c>SiteGroup</c>
    /// table, reachable only through <see cref="ISiteGroupStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SiteGroup is a named set of <see cref="Site"/>s that are synchronized or compared with one another —
    /// <see cref="Type"/> is one of <c>SiteGroupTypes</c> (Synchronization, Localization, ChangeDetection) and
    /// <see cref="PrimarySiteId"/> names the group's source of truth. Tenant-side only, like Permission
    /// and unlike Setting, so one store and no <c>IsMaster</c> routing.
    /// </para>
    /// <para>
    /// <b>THIS AGGREGATE CANNOT LAND ALONE.</b> <c>SiteGroupMember.SiteGroup</c> was a MAPPED navigation into
    /// the <see cref="SiteGroup"/> CLR type, so mapping this aggregate onto the <c>site_group</c> table while
    /// that navigation stayed mapped made EF rediscover <see cref="SiteGroup"/> THROUGH the navigation and
    /// claim the same table — the blocker, verbatim and OBSERVED for this cluster:
    /// <code>
    ///   The entity type 'SiteGroup' cannot be added to the model because a type with the same name already
    ///   exists ... 'site_group' cannot be used for entity type 'SiteGroupAggregate' since it is being used
    ///   for entity type 'SiteGroupMember.SiteGroup#SiteGroup'.
    /// </code>
    /// That is why <see cref="SiteGroupMemberAggregate"/> lands in the same increment and why the contract's
    /// <c>SiteGroup</c> member is now <c>[NotMapped]</c>. Pinned by
    /// <c>SiteGroupsMustNotBeResurrectedTests</c>.
    /// </para>
    /// <para>
    /// <b>The columns are exactly what the EntityBuilder says, which is NOT a given</b> — Language's
    /// builder was found creating a <c>name</c> column the migrations had since dropped. VERIFIED against the migrated
    /// PostgreSQL database (<c>stomme-pg-44357</c>): <c>site_group</c> has nine columns — site_group_id,
    /// name, type, primary_site_id, synchronize and the four audit columns — and no migration after
    /// <c>10010001_AddSiteGroups</c> touches it. So this one is a clean 1:1 with its builder.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class SiteGroupAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The group's display name.</summary>
        public string Name { get; set; }

        /// <summary>
        /// The group's kind — one of <c>SiteGroupTypes</c> (Synchronization, Localization, ChangeDetection).
        /// <c>SynchronizationJob</c> and <c>ServerSiteService</c> branch on this string, and they read it off
        /// <c>SiteGroupMember.SiteGroup.Type</c> rather than from here — see
        /// <see cref="SiteGroupMemberRepository"/> for how that member is populated now the navigation is gone.
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// The <see cref="Site"/> that is the group's source of truth, by id. A reference to another
        /// aggregate BY ID — there is no navigation and no foreign key to <c>site</c> on this table.
        /// </summary>
        public int PrimarySiteId { get; set; }

        /// <summary>
        /// Whether the group is queued for synchronization. <c>SynchronizationJob</c> clears this as it starts
        /// a group, so the flag is a work queue, not configuration.
        /// </summary>
        public bool Synchronize { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="SiteGroup"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int siteGroupId) => Id = siteGroupId;
    }
}
