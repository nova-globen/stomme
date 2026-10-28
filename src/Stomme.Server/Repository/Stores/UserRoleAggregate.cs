using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The UserRole aggregate root — the persistence-side model of the framework store's <c>UserRole</c>
    /// table, reachable only through <see cref="IUserRoleAssignmentStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The join row assigning a <see cref="RoleAggregate"/> to a <see cref="UserAggregate"/>. It is its own
    /// aggregate root rather than a child of either, because the harvested code reads, writes and deletes it
    /// independently of both — and because it is the ONLY table in the framework store that referenced two
    /// migrated clusters by navigation. <see cref="UserRole.User"/> and <see cref="UserRole.Role"/> are now
    /// <c>[NotMapped]</c>; <see cref="UserId"/>/<see cref="RoleId"/> are the persisted references, and
    /// <c>UserRoleRepository</c> populates the two objects from the seam. The database foreign keys
    /// <c>FK_UserRole_User</c> and <c>FK_UserRole_Role</c> are declared by <c>UserRoleEntityBuilder</c> in the
    /// migration set, not by the EF navigations, so the schema is unchanged.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. In particular
    /// the effective/expiry window is still evaluated by callers (<c>UserManager.GetUserRoles</c> via
    /// <c>Utilities.IsEffectiveAndNotExpired</c>), not here. Deliberate: brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class UserRoleAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// A reference to the User aggregate BY ID (D7/D19) — the severed <see cref="UserRole.User"/>
        /// navigation's persisted half.
        /// </summary>
        public int UserId { get; set; }

        /// <summary>
        /// A reference to the Role aggregate BY ID (D7/D19) — the severed <see cref="UserRole.Role"/>
        /// navigation's persisted half. This is the column the site filter is rebuilt on: see
        /// src/Stomme.Server/README.md (persistence seam).
        /// </summary>
        public int RoleId { get; set; }

        public DateTime? EffectiveDate { get; set; }

        public DateTime? ExpiryDate { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="UserRole"/> for an update or a delete. The store never calls this for an insert — the
        /// database assigns the identity.
        /// </summary>
        internal void AssignId(int userRoleId) => Id = userRoleId;
    }
}
