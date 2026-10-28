using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="SiteGroupMemberAggregate"/> (the persisted aggregate) and
    /// <see cref="SiteGroupMember"/> (the client/server wire contract) at the
    /// <see cref="SiteGroupMemberRepository"/> boundary. Only the columns the harvested schema actually has are
    /// mapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SiteGroupMember"/>'s two <c>[NotMapped]</c> members — <c>SiteGroup</c> and <c>AliasName</c> —
    /// are deliberately absent, and <c>SiteGroup</c> is the load-bearing one. It is NOT a column: it was a
    /// NAVIGATION the harvested repository eager-loaded, and it is now view state that
    /// <see cref="SiteGroupMemberRepository"/> populates from a second session query. Round-tripping a member
    /// through this mapper therefore DROPS <c>SiteGroup</c> by design — the repository puts it back, the mapper
    /// does not. Pinned by <c>The_mapper_does_not_round_trip_the_severed_navigation</c>.
    /// </para>
    /// <para>
    /// That absence is also what makes the write path safe. The harvested update had to say
    /// <c>db.Entry(siteGroupMember.SiteGroup).State = Unchanged</c> to stop EF cascading a write into the
    /// group's row; with no navigation on the aggregate there is nothing for the tracker to reach, so the line
    /// has no seam equivalent and simply disappears. Pinned by
    /// <c>Updating_a_member_never_writes_the_group_it_carries</c>.
    /// </para>
    /// </remarks>
    public static class SiteGroupMemberMapper
    {
        /// <summary>
        /// Maps the persisted columns. The contract's <c>SiteGroup</c> is left null — see the class remarks;
        /// <see cref="SiteGroupMemberRepository"/> populates it.
        /// </summary>
        public static SiteGroupMember ToModel(SiteGroupMemberAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SiteGroupMember
            {
                SiteGroupMemberId = aggregate.Id,
                SiteGroupId = aggregate.SiteGroupId,
                SiteId = aggregate.SiteId,
                SynchronizedOn = aggregate.SynchronizedOn,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete. Any <c>SiteGroup</c> the caller
        /// carries is ignored — see the class remarks.
        /// </summary>
        public static SiteGroupMemberAggregate ToAggregate(SiteGroupMember siteGroupMember, bool assignId)
        {
            var aggregate = new SiteGroupMemberAggregate
            {
                SiteGroupId = siteGroupMember.SiteGroupId,
                SiteId = siteGroupMember.SiteId,
                SynchronizedOn = siteGroupMember.SynchronizedOn,
                CreatedBy = siteGroupMember.CreatedBy,
                CreatedOn = siteGroupMember.CreatedOn,
                ModifiedBy = siteGroupMember.ModifiedBy,
                ModifiedOn = siteGroupMember.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(siteGroupMember.SiteGroupMemberId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="SiteGroupMember"/>
        /// reflects the persisted row. <c>SiteGroupMemberController.Post</c> returns the member straight to the
        /// client.
        /// </summary>
        public static void CopyPersistedValues(SiteGroupMemberAggregate aggregate, SiteGroupMember siteGroupMember)
        {
            siteGroupMember.SiteGroupMemberId = aggregate.Id;
            siteGroupMember.CreatedBy = aggregate.CreatedBy;
            siteGroupMember.CreatedOn = aggregate.CreatedOn;
            siteGroupMember.ModifiedBy = aggregate.ModifiedBy;
            siteGroupMember.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
