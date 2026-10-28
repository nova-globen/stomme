using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="RoleAggregate"/> (the persisted aggregate) and <see cref="Role"/> (the
    /// client/server wire contract) at the <see cref="RoleRepository"/> boundary. <see cref="Role"/> has no
    /// <c>[NotMapped]</c> members: every property on the contract is a column.
    /// </summary>
    public static class RoleMapper
    {
        public static Role ToModel(RoleAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Role
            {
                RoleId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Name = aggregate.Name,
                Description = aggregate.Description,
                IsAutoAssigned = aggregate.IsAutoAssigned,
                IsSystem = aggregate.IsSystem,
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
        public static RoleAggregate ToAggregate(Role role, bool assignId)
        {
            var aggregate = new RoleAggregate
            {
                SiteId = role.SiteId,
                Name = role.Name,
                Description = role.Description,
                IsAutoAssigned = role.IsAutoAssigned,
                IsSystem = role.IsSystem,
                CreatedBy = role.CreatedBy,
                CreatedOn = role.CreatedOn,
                ModifiedBy = role.ModifiedBy,
                ModifiedOn = role.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(role.RoleId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Role"/> reflects the
        /// persisted row. The harvested repository handed back the tracked entity itself and callers rely on
        /// it — notably <c>SiteRepository</c>'s provisioning, which reads <c>role.RoleId</c> straight after
        /// <c>AddRole</c> to assign the host user to it.
        /// </summary>
        public static void CopyPersistedValues(RoleAggregate aggregate, Role role)
        {
            role.RoleId = aggregate.Id;
            role.CreatedBy = aggregate.CreatedBy;
            role.CreatedOn = aggregate.CreatedOn;
            role.ModifiedBy = aggregate.ModifiedBy;
            role.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
