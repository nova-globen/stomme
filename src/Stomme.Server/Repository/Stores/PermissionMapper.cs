using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="PermissionAggregate"/> (the persisted aggregate) and
    /// <see cref="Permission"/> (the client/server wire contract) at the <see cref="PermissionRepository"/>
    /// boundary. Only the columns the harvested schema actually has are mapped.
    /// </summary>
    /// <remarks>
    /// <see cref="Permission"/>'s two <c>[NotMapped]</c> members are deliberately absent here and that is the
    /// load-bearing detail of this mapper:
    /// <list type="bullet">
    /// <item><c>RoleName</c> — view state EF never persisted. <see cref="PermissionRepository"/> correlates it
    /// in memory from <see cref="IRoleRepository"/> AFTER <see cref="ToModel"/> runs, so
    /// <see cref="ToModel"/> leaves it null and <see cref="ToAggregate"/> drops it. Round-tripping a
    /// Permission through this mapper therefore LOSES RoleName by design — every caller that needs it reads
    /// through <c>GetPermissions</c>, which repopulates it. Pinned by
    /// <c>The_mapper_does_not_round_trip_rolename</c>.</item>
    /// <item><c>Role</c> — an <c>[Obsolete]</c>, <c>[NotMapped]</c>, <c>[JsonIgnore]</c> navigation that was
    /// never in any EF model. Dropped, not severed; there was nothing to sever.</item>
    /// </list>
    /// </remarks>
    public static class PermissionMapper
    {
        public static Permission ToModel(PermissionAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Permission
            {
                PermissionId = aggregate.Id,
                SiteId = aggregate.SiteId,
                EntityName = aggregate.EntityName,
                EntityId = aggregate.EntityId,
                PermissionName = aggregate.PermissionName,
                RoleId = aggregate.RoleId,
                UserId = aggregate.UserId,
                IsAuthorized = aggregate.IsAuthorized,
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
        public static PermissionAggregate ToAggregate(Permission permission, bool assignId)
        {
            var aggregate = new PermissionAggregate
            {
                SiteId = permission.SiteId,
                EntityName = permission.EntityName,
                EntityId = permission.EntityId,
                PermissionName = permission.PermissionName,
                RoleId = permission.RoleId,
                UserId = permission.UserId,
                IsAuthorized = permission.IsAuthorized,
                CreatedBy = permission.CreatedBy,
                CreatedOn = permission.CreatedOn,
                ModifiedBy = permission.ModifiedBy,
                ModifiedOn = permission.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(permission.PermissionId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Permission"/> reflects
        /// the persisted row. The harvested repository handed back the tracked entity itself and callers rely
        /// on it — notably <c>PermissionController.Post</c>, which returns the permission straight to the
        /// client.
        /// </summary>
        public static void CopyPersistedValues(PermissionAggregate aggregate, Permission permission)
        {
            permission.PermissionId = aggregate.Id;
            permission.CreatedBy = aggregate.CreatedBy;
            permission.CreatedOn = aggregate.CreatedOn;
            permission.ModifiedBy = aggregate.ModifiedBy;
            permission.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
