using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Role aggregate root — the persistence-side model of the framework store's <c>Role</c> table,
    /// reachable only through <see cref="ISecurityRoleStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stomme's own security role — unrelated to ASP.NET Identity's <c>IdentityRole</c>, which this host
    /// does not even map (<see cref="TenantDBContext"/> derives from <c>IdentityUserContext</c>, not
    /// <c>IdentityDbContext</c>: Identity has no role store here). Role membership reaches the authentication
    /// pipeline as claims, via <c>ClaimsPrincipalFactory</c> reading <see cref="UserRoleAggregate"/>.
    /// </para>
    /// <para>
    /// <see cref="SiteId"/> is nullable and that nullability is load-bearing: a NULL SiteId is a GLOBAL role
    /// (Host), which every site sees. <c>RoleRepository.GetRoles(siteId, includeGlobalRoles: true)</c> is the
    /// database-side filter the UserRole site filter and <c>PermissionRepository</c>'s RoleName denormalization
    /// are both rebuilt on — see src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Deliberate:
    /// this increment moves the store onto the seam; brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class RoleAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// A reference to the Site aggregate BY ID, or NULL for a global role. Cross-aggregate references are
        /// ids, never navigations (D7/D19).
        /// </summary>
        public int? SiteId { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public bool IsAutoAssigned { get; set; }

        public bool IsSystem { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Role"/> for
        /// an update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int roleId) => Id = roleId;
    }
}
