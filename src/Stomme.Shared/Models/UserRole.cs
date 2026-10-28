using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    /// <summary>
    /// Assigns a <see cref="Role"/> to a <see cref="User"/>
    /// </summary>
    public class UserRole : ModelBase
    {
        /// <summary>
        /// Id of this assignment
        /// </summary>
        public int UserRoleId { get; set; }

        /// <summary>
        /// Reference to the <see cref="User"/> who receives this <see cref="Role"/> assignment.
        /// </summary>
        public int UserId { get; set; }

        /// <summary>
        /// Reference to the <see cref="Role"/> which the <see cref="User"/> receives
        /// </summary>
        public int RoleId { get; set; }

        /// <summary>
        /// Start of when this assignment is valid. See also <see cref="ExpiryDate"/>
        /// </summary>
        public DateTime? EffectiveDate { get; set; }

        /// <summary>
        /// End of when this assignment is valid. See also <see cref="EffectiveDate"/>
        /// </summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>
        /// Indicates that the User Security Stamp should not be updated when this user role is added or updated
        /// </summary>
        [NotMapped]
        public bool IgnoreSecurityStamp { get; set; }

        /// <summary>
        /// Direct reference to the <see cref="Role"/> object. Always populated by
        /// <c>UserRoleRepository</c> — every consumer dereferences it unguarded.
        /// </summary>
        /// <remarks>
        /// NOT an EF navigation any more, and it cannot become one again. The Role cluster moved onto the
        /// persistence seam (D22): the Role table is owned by <c>RoleAggregate</c>, and an EF navigation from
        /// here would make EF rediscover <see cref="Role"/> as a second entity type on that same table, which
        /// breaks the model at startup. <see cref="RoleId"/> is the persisted reference (the database foreign
        /// key <c>FK_UserRole_Role</c> is declared by <c>UserRoleEntityBuilder</c> in the migration set, not by
        /// this property, so the schema is unchanged); <c>UserRoleRepository</c> populates this object from the
        /// seam. Pinned by <c>UserRoleMustNotResurrectUserOrRoleTests</c>.
        /// </remarks>
        [NotMapped]
        public Role Role { get; set; }

        /// <summary>
        /// Direct reference to the <see cref="User"/> object. Always populated by
        /// <c>UserRoleRepository</c> — every consumer dereferences it unguarded.
        /// </summary>
        /// <remarks>
        /// NOT an EF navigation any more, for the same reason as <see cref="Role"/> — see the remarks there.
        /// <see cref="UserId"/> is the persisted reference.
        /// </remarks>
        [NotMapped]
        public User User { get; set; }
    }
}
