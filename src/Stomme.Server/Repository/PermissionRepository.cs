using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Security;

namespace Stomme.Repository
{
    public interface IPermissionRepository
    {
        Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName);
        Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName);
        Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId);
        Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName);
        Task<Permission> AddPermissionAsync(Permission permission);
        Task<Permission> UpdatePermissionAsync(Permission permission);
        /// <summary>
        /// Replaces the permission rows of one entity. A row naming a DIFFERENT entity is refused - the
        /// sink would write it as a SITE-WIDE row - unless <paramref name="allowDeclaredKeyRows"/> is set, and
        /// even then only for a lifted permission KEY, never a framework entity.
        /// </summary>
        /// <exception cref="InvalidOperationException">A foreign row was submitted; nothing was written.</exception>
        Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false);
        Task<Permission> GetPermissionAsync(int permissionId);
        Task DeletePermissionAsync(int permissionId);
        Task DeletePermissionsAsync(int siteId, string entityName, int entityId);
    }

    /// <summary>
    /// The Permission cluster on the persistence seam. Permission-table access goes through
    /// <see cref="IPermissionStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class
    /// holds no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE database, unlike the Setting cluster: Permission is mapped in <see cref="TenantDBContext"/>
    /// only, so there is no <c>IsMaster</c> routing here and one store suffices. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// THE OUTBOUND EDGE. This repository takes <see cref="IRoleRepository"/> and always has. That edge is
    /// what makes it unsafe for any other repository to take <see cref="IPermissionRepository"/> in return:
    /// <c>RoleRepository</c> or <c>UserRepository</c> doing so would close a container cycle. Both therefore
    /// take <see cref="IPermissionStore"/> instead — a store has no repository edges, so it cannot close a
    /// loop. Cycle-checked per edge (D22 rule 8); see src/Stomme.Server/README.md (persistence seam). Do not add another repository
    /// dependency here without re-running that check.
    /// </para>
    /// <para>
    /// WHAT <see cref="IRoleRepository"/> IS FOR, and what it is NOT. It supplies role NAMES to correlate onto
    /// <c>Permission.RoleName</c>, which is <c>[NotMapped]</c> view state with no column. That correlation was
    /// ALREADY in memory before this increment (a <c>List.Find</c> over <c>GetRoles</c>, not an
    /// <c>Include</c>), so rule 9 does not apply: there is no database join here to lose. Unchanged.
    /// </para>
    /// </remarks>
    public class PermissionRepository : IPermissionRepository
    {
        private readonly IPermissionStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IRoleRepository _roles;
        private readonly ITenantManager _tenantManager;
        private readonly ICacheManager _cache;

        public PermissionRepository(IPermissionStore store, IFrameworkStoreSessionFactory sessions, IRoleRepository roles, ITenantManager tenantManager, ICacheManager cache)
        {
            _store = store;
            _sessions = sessions;
            _roles = roles;
            _tenantManager = tenantManager;
            _cache = cache;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and UpdatePermission then attaches a DIFFERENT instance with the same key, which EF rejects:
        //   "The instance of entity type 'PermissionAggregate' cannot be tracked because another instance with
        //    the same key value for {'Id'} is already being tracked."
        // UpdatePermissions walks straight into it (GetPermissions to diff, then add/update/delete on the same
        // repository instance, hence one store and one context) — and so does PermissionController.Put, which
        // reads to authorize before writing. A query session opens its own context, so reads leave the store's
        // change tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository. The LINQ
        // operators are plain LINQ rather than EF Core's async ones, which would bind these reads to the
        // relational provider.
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

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName)
        {
            // The siteId is part of the key: the ambient alias only scopes the key to the CURRENT site, and
            // this method is called for other sites too (SynchronizationJob resolves the secondary site's
            // permissions while the alias still points at the primary). Keyed on the entity name alone, the
            // first site to warm the entry answered for every site on the tenant.
            return await _cache.GetCacheAsync(await _tenantManager.GetAliasAsync(), $"Permissions:{siteId}:{entityName}", async entry =>
            {
                var roles = (await _roles.GetRolesAsync(siteId, true)).ToList();

                // Filtered in the DATABASE on (SiteId, EntityName), exactly as db.Permission.Where did. This
                // is the render path's hot read — every page and every module authorizes through it — so an
                // in-memory filter here would read every permission in the database per request (rule 9).
                var permissions = await QueryAsync(session => session.Query<PermissionAggregate>()
                    .Where(item => item.SiteId == siteId && item.EntityName == entityName)
                    .ToList()
                    .ConvertAll(PermissionMapper.ToModel));

                // The RoleName correlation. IN MEMORY, and it always was — RoleName is [NotMapped] view state
                // with no column, and the harvested code did exactly this List.Find over GetRoles. There was
                // never an Include and never a SQL join, so nothing is traded away here (D21/rule 9 checked
                // and found not to apply). See src/Stomme.Server/README.md (persistence seam).
                foreach (var permission in permissions)
                {
                    if (permission.RoleId != null && string.IsNullOrEmpty(permission.RoleName))
                    {
                        permission.RoleName = roles.Find(item => item.RoleId == permission.RoleId)?.Name;
                    }
                }

                // Drop permissions whose role no longer exists — a role permission that cannot be resolved to
                // a name would authorize nobody and is treated as absent. Unchanged harvested behaviour.
                permissions = permissions.Where(item => item.UserId != null || item.RoleName != null).ToList();
                return permissions;
            });
        }

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName)
        {
            var permissions = await GetPermissionsAsync(siteId, entityName);
            return permissions.Where(item => item.PermissionName == permissionName);
        }

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId)
        {
            var permissions = await GetPermissionsAsync(siteId, entityName);
            return permissions.Where(item => item.EntityId == entityId);
        }

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName)
        {
            var permissions = await GetPermissionsAsync(siteId, entityName);
            return permissions.Where(item => item.EntityId == entityId)
                .Where(item => item.PermissionName == permissionName);
        }

        public async Task<Permission> AddPermissionAsync(Permission permission)
        {
            var aggregate = PermissionMapper.ToAggregate(permission, assignId: false);
            await _store.AddAsync(aggregate);
            PermissionMapper.CopyPersistedValues(aggregate, permission);
            await ClearCacheAsync(permission.SiteId, permission.EntityName);
            return permission;
        }

        public async Task<Permission> UpdatePermissionAsync(Permission permission)
        {
            var aggregate = PermissionMapper.ToAggregate(permission, assignId: true);
            await _store.UpdateAsync(aggregate);
            PermissionMapper.CopyPersistedValues(aggregate, permission);
            await ClearCacheAsync(permission.SiteId, permission.EntityName);
            return permission;
        }

        public async Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false)
        {
            // BEFORE any read or write. The populate loop below stamps EntityId = -1 on every row whose
            // EntityName differs from entityName, i.e. writes it as a SITE-WIDE row for another entity, so Edit
            // on one module, page or folder was enough to grant the framework's own Host-only User:Write policy
            // to All Users (driven: DELETE api/User/{id} then answered 200 anonymously). The controllers answer
            // 400 before writing the entity; this is the defence in depth for every other caller. Only the
            // module-definition paths opt in, and only for LIFTED keys: a framework entity is refused even then,
            // because those policies are deliberately row-less and a row flips them from role-decided to
            // row-decided. Deliberately inline rather than a helper: PerWriteStoreInvariantTests allowlists THIS
            // method by name.
            var rejected = PermissionRowScope.ForeignRows(permissions, entityName)
                .Where(row => !allowDeclaredKeyRows || PermissionKeys.IsFrameworkEntity(row.EntityName))
                .ToList();
            if (rejected.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Refused to write permission rows for another entity through {entityName} {entityId}: " +
                    string.Join(",", rejected.Select(row => $"{row.EntityName}:{row.PermissionName}")) +
                    (allowDeclaredKeyRows ? " (framework entities are never writable this way)" : "") +
                    ". A foreign row would be written as a site-wide (EntityId -1) row. Nothing was written.");
            }
            // ensure permissions are fully populated
            var roles = (await _roles.GetRolesAsync(siteId, true)).ToList();
            foreach (var permission in permissions)
            {
                permission.SiteId = siteId;
                permission.EntityName = (string.IsNullOrEmpty(permission.EntityName)) ? entityName : permission.EntityName;
                permission.EntityId = (permission.EntityName == entityName) ? entityId : -1;
                if (permission.UserId == null && permission.RoleId == null && !string.IsNullOrEmpty(permission.RoleName))
                {
                    permission.RoleId = roles.FirstOrDefault(item => item.Name == permission.RoleName)?.RoleId;
                }
            }

            // add or update permissions
            bool modified = false;
            var existing = new List<Permission>();
            foreach (var permission in permissions)
            {
                if (!existing.Any(item => item.EntityName == permission.EntityName && item.PermissionName == permission.PermissionName))
                {
                    existing.AddRange((await GetPermissionsAsync(siteId, permission.EntityName, permission.PermissionName))
                        .Where(item => item.EntityId == entityId || item.EntityId == -1));
                }

                var current = existing.FirstOrDefault(item => item.EntityName == permission.EntityName && item.EntityId == permission.EntityId
                    && item.PermissionName == permission.PermissionName && item.RoleId == permission.RoleId && item.UserId == permission.UserId);
                if (current != null)
                {
                    if (current.IsAuthorized != permission.IsAuthorized)
                    {
                        current.IsAuthorized = permission.IsAuthorized;

                        // The harvested code did `db.Entry(current).State = EntityState.Modified` — attaching
                        // an instance that came from GetPermissions, i.e. from ANOTHER context AND from the
                        // cache. Through the seam this is a plain keyed write of a detached aggregate, which
                        // is what the store's UpdateAsync does; the store's tracker is clean because the read
                        // above went through a query session.
                        await _store.UpdateAsync(PermissionMapper.ToAggregate(current, assignId: true));
                        modified = true;
                    }
                }
                else
                {
                    // One SaveChanges per row where the harvested code batched all of them into one at the end:
                    // the seam has no unit of work spanning aggregates. Parity in effect, not in cost — and it
                    // means a failure part-way now leaves earlier rows written. See src/Stomme.Server/README.md (persistence seam).
                    var aggregate = PermissionMapper.ToAggregate(permission, assignId: false);
                    await _store.AddAsync(aggregate);
                    PermissionMapper.CopyPersistedValues(aggregate, permission);
                    modified = true;
                }
            }

            // delete permissions
            foreach (var permission in existing)
            {
                if (!permissions.Any(item => item.EntityName == permission.EntityName && item.PermissionName == permission.PermissionName
                    && item.EntityId == permission.EntityId && item.RoleId == permission.RoleId && item.UserId == permission.UserId))
                {
                    await _store.DeleteAsync(PermissionMapper.ToAggregate(permission, assignId: true));
                    modified = true;
                }
            }

            if (modified)
            {
                foreach (var entityname in permissions.Select(item => item.EntityName).Distinct())
                {
                    // Every permission in the list was stamped with this method's siteId above.
                    await ClearCacheAsync(siteId, entityname);
                }
            }
        }

        public async Task<Permission> GetPermissionAsync(int permissionId)
        {
            return await QueryAsync(session => PermissionMapper.ToModel(session.Query<PermissionAggregate>()
                .FirstOrDefault(item => item.Id == permissionId)));
        }

        public async Task DeletePermissionAsync(int permissionId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(permissionId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches DeleteSetting/DeleteModule/DeleteRole/DeleteUser's shape — a deliberate,
                // strictly-safer divergence, consistent across the migrated clusters.
                return;
            }

            var siteId = aggregate.SiteId;
            var entityName = aggregate.EntityName;
            await _store.DeleteAsync(aggregate);
            await ClearCacheAsync(siteId, entityName);
        }

        public async Task DeletePermissionsAsync(int siteId, string entityName, int entityId)
        {
            // The (SiteId, EntityName, EntityId) filter stays in the DATABASE (rule 9). Reading through the
            // store instead would mean GetAllAsync — the seam has no filtered read — i.e. an unfiltered scan
            // of every permission in the database, and this method is called from every DeleteX in the
            // framework. So: one filtered read through the session, then delete each detached aggregate
            // through the store (Remove attaches a keyed detached entity as Deleted). The store's tracker is
            // clean because nothing was read through it, so there is no identity conflict.
            var aggregates = await QueryAsync(session => session.Query<PermissionAggregate>()
                .Where(item => item.EntityName == entityName)
                .Where(item => item.EntityId == entityId)
                .Where(item => item.SiteId == siteId)
                .ToList());

            foreach (var aggregate in aggregates)
            {
                // One SaveChanges per permission: the seam has no bulk delete, so this is a round trip per row
                // where the harvested code did one. Parity in effect, not in cost — see src/Stomme.Server/README.md (persistence seam).
                await _store.DeleteAsync(aggregate);
            }

            await ClearCacheAsync(siteId, entityName);
        }

        private async Task ClearCacheAsync(int siteId, string entityName)
        {
            await _cache.RemoveCacheAsync(await _tenantManager.GetAliasAsync(), $"Permissions:{siteId}:{entityName}");
        }
    }
}
