using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface IRoleRepository
    {
        Task<IEnumerable<Role>> GetRolesAsync(int siteId);
        Task<IEnumerable<Role>> GetRolesAsync(int siteId, bool includeGlobalRoles);
        Task<Role> AddRoleAsync(Role role);
        Task<Role> UpdateRoleAsync(Role role);
        Task<Role> GetRoleAsync(int roleId);
        Task<Role> GetRoleAsync(int roleId, bool tracking);
        Task DeleteRoleAsync(int roleId);
    }

    /// <summary>
    /// The Role cluster on the persistence seam. Role-table access goes through
    /// <see cref="ISecurityRoleStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class
    /// holds no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately OUT of this cluster, mirroring the Sites/Pages/Modules precedent: a role's settings are
    /// deleted through <see cref="ISettingRepository"/>. Settings are a different aggregate with their own
    /// root and their own brief, still on the <c>IDatabase</c>/<c>TenantDBContext</c> path. Coexistence is the
    /// D3-safe plan.
    /// </para>
    /// <para>
    /// The <c>IDbContextFactory</c> IS GONE. It survived the Users/Roles increment for one reason —
    /// <c>db.Permission</c> in <see cref="DeleteRoleAsync"/> — and the Permission cluster is now on the seam, so
    /// the cleanup goes through <see cref="IPermissionStore"/> and this class holds no <c>DbContext</c> at
    /// all. It takes the STORE, not <see cref="IPermissionRepository"/>: the repository would be a circular DI
    /// dependency (<c>PermissionRepository</c> takes <see cref="IRoleRepository"/>), whereas a store has no
    /// repository edges and cannot close a loop. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// </remarks>
    public class RoleRepository : IRoleRepository
    {
        private readonly ISecurityRoleStore _roleStore;
        private readonly IUserRoleAssignmentStore _userRoleStore;
        private readonly IPermissionStore _permissionStore;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly ISettingRepository _settings;

        public RoleRepository(ISecurityRoleStore roleStore, IUserRoleAssignmentStore userRoleStore, IPermissionStore permissionStore, IFrameworkStoreSessionFactory sessions, ISettingRepository settings)
        {
            _roleStore = roleStore;
            _userRoleStore = userRoleStore;
            _permissionStore = permissionStore;
            _sessions = sessions;
            _settings = settings;
        }

        // The read path on the seam: the IQueryable is materialized inside this method, so it never leaves the
        // repository. ToList/Where/FirstOrDefault are plain LINQ rather than EF Core's async operators,
        // which would bind these reads to the relational provider.
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<Role>> GetRolesAsync(int siteId)
        {
            return await GetRolesAsync(siteId, false);
        }

        // Filtered in the DATABASE on the role's own SiteId column, exactly as db.Role.Where did. This is the
        // load-bearing read of the whole cluster: UserRoleRepository's site filter and PermissionRepository's
        // RoleName denormalization are BOTH rebuilt on it, so if this filter ever moved in-memory, both would
        // silently read every other site's roles on a shared tenant database. See src/Stomme.Server/README.md (persistence seam).
        // Note SiteId == null means a GLOBAL role (Host), visible to every site.
        public async Task<IEnumerable<Role>> GetRolesAsync(int siteId, bool includeGlobalRoles)
        {
            if (includeGlobalRoles)
            {
                return await QueryAsync(session => session.Query<RoleAggregate>()
                    .Where(item => item.SiteId == siteId || item.SiteId == null)
                    .ToList()
                    .ConvertAll(RoleMapper.ToModel));
            }
            else
            {
                return await QueryAsync(session => session.Query<RoleAggregate>()
                    .Where(item => item.SiteId == siteId)
                    .ToList()
                    .ConvertAll(RoleMapper.ToModel));
            }
        }

        public async Task<Role> AddRoleAsync(Role role)
        {
            role.Description = Truncate(role.Description);
            var aggregate = RoleMapper.ToAggregate(role, assignId: false);
            await _roleStore.AddAsync(aggregate);
            RoleMapper.CopyPersistedValues(aggregate, role);
            return role;
        }

        public async Task<Role> UpdateRoleAsync(Role role)
        {
            role.Description = Truncate(role.Description);
            var aggregate = RoleMapper.ToAggregate(role, assignId: true);
            await _roleStore.UpdateAsync(aggregate);
            RoleMapper.CopyPersistedValues(aggregate, role);
            return role;
        }

        // Harvested behaviour, kept at parity: the Description column is 256 wide and the repository silently
        // truncates rather than letting the database reject the write. It also threw NullReferenceException on
        // a null Description; that is preserved by the null-conditional below returning null and the write
        // failing the same way, without this method being the thing that throws.
        private static string Truncate(string description)
        {
            return description?.Substring(0, (description.Length > 256) ? 256 : description.Length);
        }

        public async Task<Role> GetRoleAsync(int roleId)
        {
            return await GetRoleAsync(roleId, true);
        }

        public async Task<Role> GetRoleAsync(int roleId, bool tracking)
        {
            // note that the tracking parameter is no longer relevant: the seam hands back a detached aggregate
            // and RoleMapper projects it onto a fresh wire contract either way.
            //
            // Read through the query session, NOT _roleStore.GetByIdAsync — that is load-bearing, not a style
            // choice. The store's IRepository.GetByIdAsync TRACKS the aggregate in the store's DbContext,
            // and UpdateAsync then attaches a DIFFERENT instance rebuilt from the wire contract with the same
            // key, which EF rejects. RoleController.Put walks straight into it: GetRole(role.RoleId, false) to
            // authorize, then UpdateRole(role) — both on this same repository instance, hence the same store
            // and the same context. Pinned by RoleStoreBehaviourTests.A_session_read_then_a_store_write_persists.
            return await QueryAsync(session => RoleMapper.ToModel(session.Query<RoleAggregate>()
                .FirstOrDefault(item => item.Id == roleId)));
        }

        public async Task DeleteRoleAsync(int roleId)
        {
            var aggregate = await _roleStore.GetByIdAsync(roleId);
            if (aggregate == null)
            {
                return;
            }

            // Remove the role's user assignments. UserRole is in THIS cluster (on the seam), so this goes
            // through the assignment store directly rather than IUserRoleRepository — which would be a
            // circular DI dependency anyway, since UserRoleRepository already takes IRoleRepository.
            // Filtered in the database on the UserRole's own RoleId column, as db.UserRole.Where was.
            var assignments = await QueryAsync(session => session.Query<UserRoleAggregate>()
                .Where(item => item.RoleId == roleId)
                .ToList());
            foreach (var assignment in assignments)
            {
                await _userRoleStore.DeleteAsync(assignment);
            }

            // remove permissions for role — now on the seam, through IPermissionStore rather than a raw
            // db.Permission (see the remarks on this class for why it is the store and not the repository).
            // The RoleId filter stays in the DATABASE, as db.Permission.Where did: the store's only read is
            // GetAllAsync, so reading through it would scan every permission in the tenant. Read filtered
            // through the session, delete each detached aggregate through the store.
            var permissions = await QueryAsync(session => session.Query<PermissionAggregate>()
                .Where(item => item.RoleId == roleId)
                .ToList());
            foreach (var permission in permissions)
            {
                await _permissionStore.DeleteAsync(permission);
            }

            //remove settings for role
            await _settings.DeleteSettingsAsync(EntityNames.Role, roleId);

            await _roleStore.DeleteAsync(aggregate);
        }
    }
}
