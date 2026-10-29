using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ISiteGroupMemberRepository
    {
        Task<IEnumerable<SiteGroupMember>> GetSiteGroupMembersAsync();
        Task<IEnumerable<SiteGroupMember>> GetSiteGroupMembersAsync(int siteId, int siteGroupId);
        Task<SiteGroupMember> AddSiteGroupMemberAsync(SiteGroupMember siteGroupMember);
        Task<SiteGroupMember> UpdateSiteGroupMemberAsync(SiteGroupMember siteGroupMember);
        Task<SiteGroupMember> GetSiteGroupMemberAsync(int siteGroupMemberId);
        Task<SiteGroupMember> GetSiteGroupMemberAsync(int siteGroupMemberId, bool tracking);
        Task DeleteSiteGroupMemberAsync(int siteGroupMemberId);
    }

    /// <summary>
    /// The SiteGroupMember cluster on the persistence seam. SiteGroupMember-table access goes
    /// through <see cref="ISiteGroupMemberStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads);
    /// this class holds no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE ONE REAL HAZARD IN THIS CLUSTER, and the compiler cannot see it.</b> The harvested reads
    /// eager-loaded the group with <c>.Include(item =&gt; item.SiteGroup)</c>, and that navigation is NOT
    /// decoration: twenty-two production reads take <c>siteGroupMember.SiteGroup.Type</c> or
    /// <c>.PrimarySiteId</c> — eighteen in <c>SynchronizationJob</c> (which branches its whole
    /// synchronize/compare behaviour on it), two in <c>ServerSiteService.GetSitesAsync</c>, two in the Admin
    /// <c>Site/Index.razor</c>. The navigation had to be severed for the model to build at all (see
    /// <see cref="SiteGroupMemberAggregate"/>), so a compiler-clean port that simply dropped the
    /// <c>.Include</c> would have handed every one of those callers a NULL <c>SiteGroup</c> and thrown
    /// <see cref="NullReferenceException"/> on the first synchronization run — with nothing failing at build
    /// time. <see cref="PopulateSiteGroups"/> is what replaces it. Pinned by
    /// <c>SeveringTheSiteGroupNavigationTests</c>.
    /// </para>
    /// <para>
    /// <b>Why a second query is behaviour-equivalent and not a shortcut.</b> The harvested <c>.Include</c>
    /// carried NO join predicate — the filter was on this table's OWN columns (<c>SiteId</c>,
    /// <c>SiteGroupId</c>), so the include only ever fetched values, it never narrowed the result set. D22 rule
    /// 9 (keep filters in the database) therefore does not apply here: there is no database join to lose. The
    /// filters that DO narrow the read stay in the database below.
    /// </para>
    /// <para>
    /// COST, stated rather than hidden: the eager load is now a SECOND round trip where EF issued one joined
    /// query. Both queries run inside ONE session and the groups are fetched once for the whole result set, so
    /// a read is two round trips regardless of member count — not N+1. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// <b>What is GONE and needs no replacement:</b> the harvested update said
    /// <c>db.Entry(siteGroupMember.SiteGroup).State = Unchanged</c> to stop EF cascading a write into the
    /// group's row (the member arrived with its group attached, and marking the member Modified would
    /// otherwise have dragged the group in). That is pure change-tracker manipulation with no seam equivalent:
    /// <see cref="SiteGroupMemberAggregate"/> has no navigation, so the tracker has nothing to reach and a
    /// member write cannot touch the group's row. Verified rather than assumed, by
    /// <c>Updating_a_member_never_writes_the_group_it_carries</c>.
    /// </para>
    /// </remarks>
    public class SiteGroupMemberRepository : ISiteGroupMemberRepository
    {
        private readonly ISiteGroupMemberStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public SiteGroupMemberRepository(ISiteGroupMemberStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and a later Update would then attach a DIFFERENT instance with the same key, which EF rejects.
        // SynchronizationJob walks straight into it — it reads every member, then writes each one back
        // (SynchronizedOn) on ONE repository instance, hence one store and one context. A query session opens
        // its own context, so reads leave the store's change tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository. The LINQ
        // operators are plain LINQ rather than EF Core's async ones, which would bind these reads to the
        // relational provider.
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<SiteGroupMember>> GetSiteGroupMembersAsync()
        {
            return await GetSiteGroupMembersAsync(-1, -1);
        }

        public async Task<IEnumerable<SiteGroupMember>> GetSiteGroupMembersAsync(int siteId, int siteGroupId)
        {
            // The -1 sentinels mean "any". Both filters stay in the DATABASE (rule 9) and both are on this
            // table's own columns — see the class remarks for why the eager load is not part of the filter.
            //
            // COMPOSED IN C#, NOT INSIDE THE EXPRESSION TREE, and that is not a style choice. The harvested
            // query said:
            //
            //     .Where(item => (siteId == -1 || item.SiteId == siteId)
            //                 && (siteGroupId == -1 || item.SiteGroupId == siteGroupId))
            //
            // On EF that works: the sentinel comparison is a client-evaluable constant, so `-1 == -1` folds to
            // true and the arm disappears. RavenDB's LINQ provider does NOT fold it — it compiles the whole
            // predicate to an RQL query in which the wildcard arm matches nothing, and the read returns ZERO
            // ROWS with no exception. That was MEASURED by the conformance suite, not predicted: the harvested
            // shape passed on EF and returned 0 of 2 members on RavenDB. It is the worst possible failure for
            // this cluster — SynchronizationJob and ServerSiteService both call the (-1, -1) overload, so a
            // provider swap would have silently stopped all site synchronization with nothing in the log.
            //
            // Deciding the branch HERE, where siteId and siteGroupId are ordinary local ints, keeps the choice
            // in C# and sends the provider only the filters that actually narrow. Pinned on BOTH providers by
            // Site_group_members_are_found_by_site_and_group_and_by_the_minus_one_wildcards.
            return await QueryAsync(session =>
            {
                var query = session.Query<SiteGroupMemberAggregate>();

                if (siteId != -1)
                {
                    query = query.Where(item => item.SiteId == siteId);
                }

                if (siteGroupId != -1)
                {
                    query = query.Where(item => item.SiteGroupId == siteGroupId);
                }

                var members = query.ToList().ConvertAll(SiteGroupMemberMapper.ToModel);

                PopulateSiteGroups(session, members);
                return (IEnumerable<SiteGroupMember>)members;
            });
        }

        public async Task<SiteGroupMember> AddSiteGroupMemberAsync(SiteGroupMember siteGroupMember)
        {
            var aggregate = SiteGroupMemberMapper.ToAggregate(siteGroupMember, assignId: false);
            await _store.AddAsync(aggregate);
            SiteGroupMemberMapper.CopyPersistedValues(aggregate, siteGroupMember);
            return siteGroupMember;
        }

        public async Task<SiteGroupMember> UpdateSiteGroupMemberAsync(SiteGroupMember siteGroupMember)
        {
            // The harvested `db.Entry(siteGroupMember.SiteGroup).State = Unchanged` has NO equivalent here and
            // needs none — see the class remarks. The mapper ignores the caller's SiteGroup entirely, so a
            // member write cannot reach the group's row.
            await _store.UpdateAsync(SiteGroupMemberMapper.ToAggregate(siteGroupMember, assignId: true));
            return siteGroupMember;
        }

        public async Task<SiteGroupMember> GetSiteGroupMemberAsync(int siteGroupMemberId)
        {
            return await GetSiteGroupMemberAsync(siteGroupMemberId, true);
        }

        public async Task<SiteGroupMember> GetSiteGroupMemberAsync(int siteGroupMemberId, bool tracking)
        {
            // `tracking` is inert on the seam, for the reason documented on SiteGroupRepository: the harvested
            // context was disposed on the way out, so a tracked entity was of no use to the caller, and a
            // session read is never tracked. Both arms of the harvested if/else collapse to this read.
            _ = tracking;

            return await QueryAsync(session =>
            {
                var member = SiteGroupMemberMapper.ToModel(session.Query<SiteGroupMemberAggregate>()
                    .FirstOrDefault(item => item.Id == siteGroupMemberId));

                if (member != null)
                {
                    PopulateSiteGroups(session, [member]);
                }

                return member;
            });
        }

        public async Task DeleteSiteGroupMemberAsync(int siteGroupMemberId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(siteGroupMemberId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches the shape every migrated cluster settled on — see SiteGroupRepository.
                return;
            }

            await _store.DeleteAsync(aggregate);
        }

        /// <summary>
        /// Fills in each member's <see cref="SiteGroupMember.SiteGroup"/> — the view state that replaced the
        /// severed navigation. This is what <c>.Include(item =&gt; item.SiteGroup)</c> used to do, moved
        /// out of the query and into the repository, and it is the reason twenty-two production reads of
        /// <c>member.SiteGroup.Type</c> still work.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ONE query for the groups the members actually reference, stitched in memory — not one query per
        /// member. It runs on the SAME session as the read that produced <paramref name="members"/>, so a read
        /// is two round trips in total regardless of how many members come back.
        /// </para>
        /// <para>
        /// <b>One deliberate divergence.</b> EF's <c>.Include</c> over a REQUIRED navigation emits an INNER
        /// JOIN, so a member whose group had vanished would have been DROPPED from the result set entirely.
        /// Here it comes back with a null <c>SiteGroup</c> instead. On the relational providers the difference
        /// is unreachable — <c>FK_SiteGroupMember_SiteGroup</c> is <c>ON DELETE CASCADE</c>, so a member cannot
        /// outlive its group — but on RavenDB, which has no such constraint, it can. Returning the row is the
        /// safer of the two: a job that silently skips a member is harder to notice than one that reports a
        /// null group. Pinned by <c>A_member_whose_group_is_missing_comes_back_with_a_null_group</c>.
        /// </para>
        /// </remarks>
        private static void PopulateSiteGroups(IQuerySession session, IReadOnlyList<SiteGroupMember> members)
        {
            if (members.Count == 0)
            {
                return;
            }

            // Filtered in the DATABASE to the groups these members reference (rule 9) rather than reading every
            // group in the tenant. Distinct first: several members of one group is the normal case, and the
            // provider should be asked for that group once.
            var siteGroupIds = members.Select(item => item.SiteGroupId).Distinct().ToList();

            var siteGroups = session.Query<SiteGroupAggregate>()
                .Where(item => siteGroupIds.Contains(item.Id))
                .ToList()
                .ToDictionary(item => item.Id, SiteGroupMapper.ToModel);

            foreach (var member in members)
            {
                // A miss leaves SiteGroup null rather than dropping the member — see the remarks.
                member.SiteGroup = siteGroups.TryGetValue(member.SiteGroupId, out var siteGroup) ? siteGroup : null;
            }
        }
    }
}
