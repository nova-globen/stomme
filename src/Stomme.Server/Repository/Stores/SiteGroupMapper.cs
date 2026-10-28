using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="SiteGroupAggregate"/> (the persisted aggregate) and
    /// <see cref="SiteGroup"/> (the client/server wire contract) at the <see cref="SiteGroupRepository"/>
    /// boundary. Only the columns the harvested schema actually has are mapped.
    /// </summary>
    /// <remarks>
    /// <see cref="SiteGroup"/> has no <c>[NotMapped]</c> members, so unlike <see cref="LanguageMapper"/> this
    /// mapper round-trips the contract WHOLE — every public property is a column. Pinned by
    /// <c>Every_persisted_sitegroup_value_survives_a_round_trip_through_the_mapper</c>.
    /// </remarks>
    public static class SiteGroupMapper
    {
        public static SiteGroup ToModel(SiteGroupAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SiteGroup
            {
                SiteGroupId = aggregate.Id,
                Name = aggregate.Name,
                Type = aggregate.Type,
                PrimarySiteId = aggregate.PrimarySiteId,
                Synchronize = aggregate.Synchronize,
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
        public static SiteGroupAggregate ToAggregate(SiteGroup siteGroup, bool assignId)
        {
            var aggregate = new SiteGroupAggregate
            {
                Name = siteGroup.Name,
                Type = siteGroup.Type,
                PrimarySiteId = siteGroup.PrimarySiteId,
                Synchronize = siteGroup.Synchronize,
                CreatedBy = siteGroup.CreatedBy,
                CreatedOn = siteGroup.CreatedOn,
                ModifiedBy = siteGroup.ModifiedBy,
                ModifiedOn = siteGroup.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(siteGroup.SiteGroupId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="SiteGroup"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on it
        /// — <c>SiteGroupController.Post</c> returns the group straight to the client.
        /// </summary>
        public static void CopyPersistedValues(SiteGroupAggregate aggregate, SiteGroup siteGroup)
        {
            siteGroup.SiteGroupId = aggregate.Id;
            siteGroup.CreatedBy = aggregate.CreatedBy;
            siteGroup.CreatedOn = aggregate.CreatedOn;
            siteGroup.ModifiedBy = aggregate.ModifiedBy;
            siteGroup.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
