using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="UserRoleAggregate"/> (the persisted aggregate) and
    /// <see cref="UserRole"/> (the client/server wire contract) at the <see cref="UserRoleRepository"/>
    /// boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="UserRole.User"/> and <see cref="UserRole.Role"/> are deliberately NOT set here. They were EF
    /// navigations before this cluster moved onto the seam and are now <c>[NotMapped]</c> view state that
    /// <see cref="UserRoleRepository"/> populates from the User and Role aggregates — every caller
    /// dereferences them unguarded, so they must stay populated. Keeping that out of the mapper is what makes
    /// the two concerns (columns vs. composition) separable. <see cref="UserRole.IgnoreSecurityStamp"/> is
    /// likewise <c>[NotMapped]</c> caller state and was never persisted.
    /// </remarks>
    public static class UserRoleMapper
    {
        public static UserRole ToModel(UserRoleAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new UserRole
            {
                UserRoleId = aggregate.Id,
                UserId = aggregate.UserId,
                RoleId = aggregate.RoleId,
                EffectiveDate = aggregate.EffectiveDate,
                ExpiryDate = aggregate.ExpiryDate,
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
        public static UserRoleAggregate ToAggregate(UserRole userRole, bool assignId)
        {
            var aggregate = new UserRoleAggregate
            {
                UserId = userRole.UserId,
                RoleId = userRole.RoleId,
                EffectiveDate = userRole.EffectiveDate,
                ExpiryDate = userRole.ExpiryDate,
                CreatedBy = userRole.CreatedBy,
                CreatedOn = userRole.CreatedOn,
                ModifiedBy = userRole.ModifiedBy,
                ModifiedOn = userRole.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(userRole.UserRoleId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="UserRole"/> reflects the
        /// persisted row. <c>UserRoleController.Post</c> returns the written object straight to the client.
        /// </summary>
        public static void CopyPersistedValues(UserRoleAggregate aggregate, UserRole userRole)
        {
            userRole.UserRoleId = aggregate.Id;
            userRole.CreatedBy = aggregate.CreatedBy;
            userRole.CreatedOn = aggregate.CreatedOn;
            userRole.ModifiedBy = aggregate.ModifiedBy;
            userRole.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
