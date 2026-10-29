using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface IUserRoleRepository
    {
        Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId);
        Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId);
        Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId);
        Task<UserRole> AddUserRoleAsync(UserRole userRole);
        Task<UserRole> UpdateUserRoleAsync(UserRole userRole);
        Task<UserRole> GetUserRoleAsync(int userRoleId);
        Task<UserRole> GetUserRoleAsync(int userRoleId, bool tracking);
        Task<UserRole> GetUserRoleAsync(int userId, int roleId);
        Task<UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking);
        Task DeleteUserRoleAsync(int userRoleId);
        Task DeleteUserRolesAsync(int userId);
    }

    /// <summary>
    /// The UserRole cluster on the persistence seam. UserRole-table access goes through
    /// <see cref="IUserRoleAssignmentStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this
    /// class holds no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This repository carries the hardest part of the Users/Roles increment: <see cref="UserRole"/> had EF
    /// navigations to BOTH migrated aggregates (<see cref="UserRole.User"/> and <see cref="UserRole.Role"/>),
    /// and three of its queries filtered ACROSS the Role navigation in the database. Both navigations are now
    /// <c>[NotMapped]</c> and populated by <see cref="HydrateAsync"/>; the site filters are rebuilt so they stay in
    /// the database. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// It reads the User aggregate through the seam's query session rather than
    /// <see cref="IUserRepository"/> because that would be a CIRCULAR DI dependency —
    /// <c>UserRepository</c> already takes <see cref="IUserRoleRepository"/> (it assigns auto-assigned roles
    /// on registration). <see cref="IRoleRepository"/> is injected because that dependency already existed and
    /// is not circular. This is the D19/rule-8 cycle check, and it is why the two look different here.
    /// </para>
    /// <para>
    /// <c>UserManager&lt;IdentityUser&gt;</c> stays injected: <see cref="UpdateSecurityStampAsync"/> reaches into
    /// stock ASP.NET Identity, which is still the live authentication path. Role membership reaches the
    /// auth cookie as claims, so a role change must invalidate the stamp. That coupling is deliberate and
    /// unchanged — this increment is not an identity cutover.
    /// </para>
    /// </remarks>
    public class UserRoleRepository : IUserRoleRepository
    {
        private readonly IUserRoleAssignmentStore _userRoleStore;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IRoleRepository _roles;
        private readonly ITenantManager _tenantManager;
        private readonly UserManager<IdentityUser> _identityUserManager;
        private readonly ICacheManager _cache;

        public UserRoleRepository(IUserRoleAssignmentStore userRoleStore, IFrameworkStoreSessionFactory sessions, IRoleRepository roles, ITenantManager tenantManager, UserManager<IdentityUser> identityUserManager, ICacheManager cache)
        {
            _userRoleStore = userRoleStore;
            _sessions = sessions;
            _roles = roles;
            _tenantManager = tenantManager;
            _identityUserManager = identityUserManager;
            _cache = cache;
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

        /// <summary>
        /// The site's role ids, resolved through the Role cluster's own DATABASE-side SiteId filter, or null
        /// when <paramref name="siteId"/> is -1 (the harvested "every site" sentinel, which short-circuited the
        /// whole predicate to true).
        /// </summary>
        /// <remarks>
        /// THE load-bearing method of this increment. The harvested queries filtered
        /// <c>item.Role.SiteId == siteId || item.Role.SiteId == null || siteId == -1</c> ACROSS the Role
        /// navigation — a join predicate evaluated BY THE DATABASE. Severing the navigation makes the lazy
        /// translation "materialize every UserRole row in the tenant and filter in memory", which is not a
        /// compile error and not a test failure, but on a shared tenant database (many sites, one database —
        /// the site-per-alias model Stomme supports) would read every other site's role assignments on every
        /// authenticated request. Resolving the ids first and filtering <c>RoleId IN (...)</c> keeps both
        /// filters in the database. Proved from PostgreSQL's own statement log; see src/Stomme.Server/README.md (persistence seam).
        /// </remarks>
        private async Task<List<int>> SiteRoleIdsAsync(int siteId)
        {
            if (siteId == -1)
            {
                return null;
            }

            // WHERE site_id = $1 OR site_id IS NULL — evaluated by the database (RoleRepository.GetRoles).
            return (await _roles.GetRolesAsync(siteId, true)).Select(item => item.RoleId).ToList();
        }

        /// <summary>
        /// Populates the severed <see cref="UserRole.Role"/> and <see cref="UserRole.User"/> objects from the
        /// User and Role aggregates, and drops any assignment whose role or user could not be resolved.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both lookups are single <c>IN (...)</c> queries — the same number of round trips the two
        /// <c>Include</c>s cost, not N+1.
        /// </para>
        /// <para>
        /// The drop reproduces the harvested INNER JOIN semantics: <c>.Include(item =&gt; item.Role)</c> with a
        /// <c>Where</c> on <c>item.Role.SiteId</c> is an inner join, so an assignment with no matching Role row
        /// never appeared. Keeping that is not pedantry — every consumer
        /// (<c>UserManager.GetUserRoles</c>, <c>UserRoleController.Filter</c>, <c>UserPermissions</c>,
        /// <c>ClaimsPrincipalFactory</c>) dereferences <c>.Role.Name</c> and <c>.User.DisplayName</c>
        /// UNGUARDED, so a null here is an NRE on the authentication path. The database foreign keys make it
        /// unreachable in practice; this makes it unreachable by construction.
        /// </para>
        /// <para>
        /// Each UserRole gets its OWN Role and User instance. The harvested <c>Include</c> shared one instance
        /// per key via EF's identity map, and <c>GetUserRoles(userId, siteId)</c> is CACHED — so a shared
        /// instance is a mutable object living in the cache. <c>UserRoleController.Filter</c> already clones
        /// defensively ("clone object to avoid mutating cache"), which shows the hazard was known. The client
        /// already saw independent instances because JSON serialization has no identity map, so this makes the
        /// server match what the wire always did.
        /// </para>
        /// </remarks>
        private async Task<List<UserRole>> HydrateAsync(List<UserRoleAggregate> aggregates)
        {
            var userRoles = aggregates.ConvertAll(UserRoleMapper.ToModel);
            if (userRoles.Count == 0)
            {
                return userRoles;
            }

            var roleIds = userRoles.Select(item => item.RoleId).Distinct().ToList();
            var userIds = userRoles.Select(item => item.UserId).Distinct().ToList();

            // Both filtered in the database: WHERE role_id = ANY ($1) / WHERE user_id = ANY ($1).
            var roles = await QueryAsync(session => session.Query<RoleAggregate>()
                .Where(item => roleIds.Contains(item.Id))
                .ToList());
            var users = await QueryAsync(session => session.Query<UserAggregate>()
                .Where(item => userIds.Contains(item.Id))
                .ToList());

            var hydrated = new List<UserRole>(userRoles.Count);
            foreach (var userRole in userRoles)
            {
                var role = roles.Find(item => item.Id == userRole.RoleId);
                var user = users.Find(item => item.Id == userRole.UserId);
                if (role == null || user == null)
                {
                    continue; // the inner join would not have returned this row
                }

                userRole.Role = RoleMapper.ToModel(role);
                userRole.User = UserMapper.ToModel(user);
                hydrated.Add(userRole);
            }

            return hydrated;
        }

        // The `siteId == -1` sentinel is branched HERE, in C#, rather than folded into the predicate as
        // `roleIds == null || roleIds.Contains(...)`. Whether EF translates a captured-variable null check or
        // silently evaluates it client-side is exactly the kind of thing that would turn the RoleId filter
        // into an in-memory one without any signal. Branching makes each query's shape unambiguous.
        public async Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId)
        {
            var roleIds = await SiteRoleIdsAsync(siteId);

            return await HydrateAsync(await QueryAsync(session =>
            {
                var query = session.Query<UserRoleAggregate>();
                return (roleIds == null ? query : query.Where(item => roleIds.Contains(item.RoleId))).ToList();
            }));
        }

        public async Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId)
        {
            // Read on EVERY authenticated request (UserManager.GetUser -> GetUserRoles), hence the cache.
            return await _cache.GetCacheAsync(await _tenantManager.GetAliasAsync(), $"UserRoles:{userId}", async entry =>
            {
                var roleIds = await SiteRoleIdsAsync(siteId);

                return await HydrateAsync(await QueryAsync(session =>
                {
                    var query = session.Query<UserRoleAggregate>().Where(item => item.UserId == userId);
                    return (roleIds == null ? query : query.Where(item => roleIds.Contains(item.RoleId))).ToList();
                }));
            });
        }

        public async Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId)
        {
            // The harvested query filtered `item.Role.Name == roleName` across the navigation too, so the name
            // filter is resolved through the Role cluster's database-side query rather than in memory here.
            var roleIds = await QueryAsync(session => session.Query<RoleAggregate>()
                .Where(item => (item.SiteId == siteId || item.SiteId == null || siteId == -1) && item.Name == roleName)
                .ToList()
                .ConvertAll(item => item.Id));

            return await HydrateAsync(await QueryAsync(session => session.Query<UserRoleAggregate>()
                .Where(item => roleIds.Contains(item.RoleId))
                .ToList()));
        }

        public async Task<UserRole> AddUserRoleAsync(UserRole userRole)
        {
            userRole.EffectiveDate = userRole.EffectiveDate.HasValue ? DateTime.SpecifyKind(userRole.EffectiveDate.Value, DateTimeKind.Utc) : userRole.EffectiveDate;
            userRole.ExpiryDate = userRole.ExpiryDate.HasValue ? DateTime.SpecifyKind(userRole.ExpiryDate.Value, DateTimeKind.Utc) : userRole.ExpiryDate;

            var aggregate = UserRoleMapper.ToAggregate(userRole, assignId: false);
            await _userRoleStore.AddAsync(aggregate);
            UserRoleMapper.CopyPersistedValues(aggregate, userRole);

            // host roles can only exist at global level - remove any site specific user roles
            var role = await _roles.GetRoleAsync(userRole.RoleId);
            if (role.Name == RoleNames.Host)
            {
                await DeleteUserRolesAsync(userRole.UserId);
            }

            if (!userRole.IgnoreSecurityStamp)
            {
                await UpdateSecurityStampAsync(userRole.UserId);
            }

            await RefreshCacheAsync(userRole.UserId);

            return userRole;
        }

        public async Task<UserRole> UpdateUserRoleAsync(UserRole userRole)
        {
            userRole.EffectiveDate = userRole.EffectiveDate.HasValue ? DateTime.SpecifyKind(userRole.EffectiveDate.Value, DateTimeKind.Utc) : userRole.EffectiveDate;
            userRole.ExpiryDate = userRole.ExpiryDate.HasValue ? DateTime.SpecifyKind(userRole.ExpiryDate.Value, DateTimeKind.Utc) : userRole.ExpiryDate;

            var aggregate = UserRoleMapper.ToAggregate(userRole, assignId: true);
            await _userRoleStore.UpdateAsync(aggregate);
            UserRoleMapper.CopyPersistedValues(aggregate, userRole);

            if (!userRole.IgnoreSecurityStamp)
            {
                await UpdateSecurityStampAsync(userRole.UserId);
            }

            await RefreshCacheAsync(userRole.UserId);

            return userRole;
        }

        public async Task<UserRole> GetUserRoleAsync(int userRoleId)
        {
            return await GetUserRoleAsync(userRoleId, true);
        }

        public async Task<UserRole> GetUserRoleAsync(int userRoleId, bool tracking)
        {
            // note that the tracking parameter is no longer relevant: the seam hands back a detached aggregate
            // and UserRoleMapper projects it onto a fresh wire contract either way. Read through the query
            // session, never the store — D19; see the note in UserRepository.GetUser.
            return (await HydrateAsync(await QueryAsync(session => session.Query<UserRoleAggregate>()
                .Where(item => item.Id == userRoleId)
                .ToList()))).FirstOrDefault();
        }

        public async Task<UserRole> GetUserRoleAsync(int userId, int roleId)
        {
            return await GetUserRoleAsync(userId, roleId, true);
        }

        public async Task<UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking)
        {
            return (await HydrateAsync(await QueryAsync(session => session.Query<UserRoleAggregate>()
                .Where(item => item.UserId == userId && item.RoleId == roleId)
                .ToList()))).FirstOrDefault();
        }

        public async Task DeleteUserRoleAsync(int userRoleId)
        {
            var aggregate = await _userRoleStore.GetByIdAsync(userRoleId);
            if (aggregate == null)
            {
                return;
            }

            var userId = aggregate.UserId;
            await _userRoleStore.DeleteAsync(aggregate);

            await UpdateSecurityStampAsync(userId);
            await RefreshCacheAsync(userId);
        }

        // Removes a user's SITE-SPECIFIC assignments, leaving global (Host) ones. The harvested query was
        // `item.UserId == userId && item.Role.SiteId != null` — a join predicate again.
        //
        // The UserId filter stays in the DATABASE, which is what bounds the read: it returns one user's
        // handful of assignments, never another site's rows. The `Role.SiteId != null` half is then resolved
        // over exactly those roles. That half is not kept as a database predicate deliberately — doing so would
        // mean scanning the whole Role table for `site_id IS NOT NULL` ACROSS EVERY SITE (there is no site
        // filter in this method to bound it), which is strictly more rows than the user's own assignments.
        // This is not the rule-9 hazard: no site sees another site's rows, and no unfiltered table scan
        // replaces a filtered one.
        public async Task DeleteUserRolesAsync(int userId)
        {
            var assignments = await QueryAsync(session => session.Query<UserRoleAggregate>()
                .Where(item => item.UserId == userId)
                .ToList());
            if (assignments.Count == 0)
            {
                return;
            }

            var roleIds = assignments.ConvertAll(item => item.RoleId);
            var siteSpecificRoleIds = (await QueryAsync(session => session.Query<RoleAggregate>()
                .Where(item => roleIds.Contains(item.Id) && item.SiteId != null)
                .ToList()))
                .ConvertAll(item => item.Id);

            foreach (var assignment in assignments.Where(item => siteSpecificRoleIds.Contains(item.RoleId)))
            {
                await _userRoleStore.DeleteAsync(assignment);
            }

            await UpdateSecurityStampAsync(userId);
            await RefreshCacheAsync(userId);
        }

        // Reaches into stock ASP.NET Identity — still the live authentication path. The Stomme User row and
        // Identity's AspNetUsers row are correlated by USERNAME, not by a key or a navigation, which is
        // precisely why the two EF models can share TenantDBContext without colliding. The User read goes
        // through the seam's session (injecting IUserRepository here would be a circular dependency — see the
        // class remarks).
        private async Task UpdateSecurityStampAsync(int userId)
        {
            var user = await QueryAsync(session => session.Query<UserAggregate>()
                .FirstOrDefault(item => item.Id == userId));
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    await _identityUserManager.UpdateSecurityStampAsync(identityuser);
                }
            }
        }

        private async Task RefreshCacheAsync(int userId)
        {
            await _cache.RemoveCacheAsync(await _tenantManager.GetAliasAsync(), $"User:{userId}");
            await _cache.RemoveCacheAsync(await _tenantManager.GetAliasAsync(), $"UserRoles:{userId}");
        }
    }
}
