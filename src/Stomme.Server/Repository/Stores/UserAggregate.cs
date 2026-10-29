using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The User aggregate root — the persistence-side model of the framework store's <c>User</c> table,
    /// reachable only through <see cref="IUserAccountStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Stomme's OWN user record, and it is NOT ASP.NET Identity's user. Stock Identity
    /// (<c>AddIdentityCore&lt;IdentityUser&gt;().AddEntityFrameworkStores&lt;TenantDBContext&gt;()</c>) remains
    /// the live authentication path and keeps its own <c>IdentityUser</c> entity on the <c>AspNetUsers</c>
    /// table. The two models share <see cref="TenantDBContext"/> but are disjoint: different CLR types,
    /// different tables, and no navigation between them — they are correlated by <see cref="Username"/> in
    /// application code (<c>UserManager</c>, <c>UserRoleRepository.UpdateSecurityStamp</c>). Moving this table
    /// onto the seam is deliberately NOT an identity cutover.
    /// </para>
    /// <para>
    /// Much of the <see cref="User"/> wire contract is <c>[NotMapped]</c> and stays that way: SecurityStamp
    /// comes from Identity, SiteId/Roles/Password/IsAuthenticated/EmailConfirmed/SuppressNotification/Settings
    /// are populated by other layers per request. That is why the aggregate is not <see cref="User"/> —
    /// <see cref="UserMapper"/> maps between the two at the repository boundary, so the HTTP/client contract is
    /// byte-for-byte unchanged.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Deliberate:
    /// this increment moves the store onto the seam; brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class UserAggregate : AggregateRoot<int>, IAuditable, IDeletable
    {
        public string Username { get; set; }

        public string DisplayName { get; set; }

        public string Email { get; set; }

        public string TimeZoneId { get; set; }

        public string CultureCode { get; set; }

        /// <summary>
        /// A reference to a <see cref="File"/> BY ID. Cross-aggregate references are ids, never navigations;
        /// Files are their own cluster, still on the <c>IDatabase</c>/<c>TenantDBContext</c> path.
        /// </summary>
        public int? PhotoFileId { get; set; }

        public DateTime? LastLoginOn { get; set; }

        /// <summary>
        /// Note the spelling. The property is <c>LastIPAddress</c> (matching <see cref="User.LastIPAddress"/>)
        /// but <c>UserEntityBuilder</c> created the column from the string <c>"LastIpAddress"</c>. Both map to
        /// <c>last_ip_address</c> under the snake_case rewriter and both match case-insensitively on the
        /// providers without one, so the convention-mapped name is correct on every provider — but it is close
        /// enough to a trap to be pinned by a test.
        /// </summary>
        public string LastIPAddress { get; set; }

        public bool TwoFactorRequired { get; set; }

        public string TwoFactorCode { get; set; }

        public DateTime? TwoFactorExpiry { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        public string DeletedBy { get; set; }

        public DateTime? DeletedOn { get; set; }

        public bool IsDeleted { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="User"/> for
        /// an update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int userId) => Id = userId;
    }
}
