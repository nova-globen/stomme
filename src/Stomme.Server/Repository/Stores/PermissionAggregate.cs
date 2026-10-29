using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Permission aggregate root — the persistence-side model of the framework store's <c>Permission</c>
    /// table, reachable only through <see cref="IPermissionStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Permission is <c>(SiteId, EntityName, EntityId, PermissionName)</c>-keyed authorization state
    /// granted to EITHER a <see cref="RoleId"/> or a <see cref="UserId"/> — never both. Like
    /// <see cref="SettingAggregate"/> it is polymorphic state hanging off *some* other entity (a Page, a
    /// Module, a Folder, a ModuleDefinition, the Site itself) rather than a child of any one aggregate, so it
    /// is its own aggregate root. Unlike Setting, its <see cref="EntityId"/> has a sentinel: <c>-1</c> means
    /// "every entity of this EntityName", which is how API-level permissions are expressed.
    /// </para>
    /// <para>
    /// ONE DATABASE, unlike <see cref="SettingAggregate"/>. Permission is mapped in
    /// <see cref="TenantDBContext"/> only — <see cref="MasterDBContext"/> never had a
    /// <c>DbSet&lt;Permission&gt;</c> and <c>PermissionEntityBuilder</c> appears only in the TENANT migration
    /// set (<c>01000000_InitializeTenant</c>). There is no <c>IsMaster</c> routing here and none is wanted:
    /// permissions are scoped by <see cref="SiteId"/>, and a Site is per-tenant by construction. See
    /// src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// NOTE what is absent: <c>Permission.RoleName</c> is <c>[NotMapped]</c> on the wire contract and has no
    /// column, so it is NOT on this aggregate. It is correlated in memory from
    /// <see cref="IRoleRepository"/> by <c>PermissionRepository</c>, exactly as the harvested code did — that
    /// was never a SQL join. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling. Deliberate:
    /// this increment moves the store onto the seam; brief 08 owns the re-modelling.
    /// </para>
    /// </remarks>
    public class PermissionAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// The <see cref="Site"/> this permission belongs to. A reference to another aggregate BY ID
        /// — permissions never navigate to the site. The harvested schema does carry an
        /// <c>FK_Permission_Site</c> with a CASCADE delete, which is unchanged: this is a mapping change, not
        /// a schema change.
        /// </summary>
        public int SiteId { get; set; }

        /// <summary>
        /// What kind of entity this permission applies to ("Page", "Module", "Folder", "ModuleDefinition"…).
        /// Half of the polymorphic reference.
        /// </summary>
        public string EntityName { get; set; }

        /// <summary>
        /// The id of the entity this permission applies to, or <c>-1</c> to mean "all entities of this
        /// EntityName regardless of id" (API permissions). By ID, never by navigation.
        /// </summary>
        public int EntityId { get; set; }

        /// <summary>
        /// The permission being granted or denied ("View", "Edit", "Utilize"…).
        /// </summary>
        public string PermissionName { get; set; }

        /// <summary>
        /// The <see cref="Role"/> this permission applies to, or null if it is a user permission. By ID: the
        /// harvested <c>Permission.Role</c> navigation is <c>[NotMapped]</c> and <c>[Obsolete]</c>, so this
        /// cluster had no live navigation to sever.
        /// </summary>
        public int? RoleId { get; set; }

        /// <summary>
        /// The <see cref="User"/> this permission applies to, or null if it is a role permission.
        /// </summary>
        public int? UserId { get; set; }

        /// <summary>
        /// Grant (true) or deny (false). A deny outranks a grant — see <c>UserSecurity.IsAuthorized</c>.
        /// </summary>
        public bool IsAuthorized { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="Permission"/> for an update or a delete. The store never calls this for an insert — the
        /// database assigns the identity.
        /// </summary>
        internal void AssignId(int permissionId) => Id = permissionId;
    }
}
