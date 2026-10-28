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
    public interface IUserRepository
    {
        Task<IEnumerable<User>> GetUsersAsync();
        Task<User> AddUserAsync(User user);
        Task<User> UpdateUserAsync(User user);
        Task<User> GetUserAsync(int userId);
        Task<User> GetUserAsync(int userId, bool tracking);
        Task<User> GetUserAsync(string username);
        Task<User> GetUserAsync(string username, string email);
        Task DeleteUserAsync(int userId);
    }

    /// <summary>
    /// The User cluster on the persistence seam (D3/D7/D22). User-table access goes through
    /// <see cref="IUserAccountStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class
    /// holds no <c>DbContext</c> for its own table and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Stomme's OWN user record, NOT ASP.NET Identity's. Stock Identity remains the live
    /// authentication path with its own <c>IdentityUser</c> on the <c>AspNetUsers</c> table; the two are
    /// correlated by username in <c>UserManager</c>, never by a navigation, and this increment deliberately
    /// does not touch that. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// Deliberately OUT of this cluster, mirroring the Sites/Pages/Modules precedent: the user's personal
    /// folder is created through <see cref="IFolderRepository"/> and auto-assigned roles through
    /// <see cref="IRoleRepository"/>/<see cref="IUserRoleRepository"/>. The <c>IDbContextFactory</c> IS GONE
    /// (D24): it survived the Users/Roles increment only for <c>db.Permission</c> in <see cref="DeleteUserAsync"/>,
    /// and the Permission cluster is now on the seam, so this class holds no <c>DbContext</c> at all. It takes
    /// <see cref="IPermissionStore"/> rather than <see cref="IPermissionRepository"/> — the repository would
    /// close a container cycle through <c>PermissionRepository -&gt; IRoleRepository -&gt; ...</c>; a store has
    /// no repository edges. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// </remarks>
    public class UserRepository : IUserRepository
    {
        private readonly IUserAccountStore _userStore;
        private readonly IPermissionStore _permissionStore;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IFolderRepository _folders;
        private readonly IRoleRepository _roles;
        private readonly IUserRoleRepository _userroles;

        public UserRepository(IUserAccountStore userStore, IPermissionStore permissionStore, IFrameworkStoreSessionFactory sessions, IFolderRepository folders, IRoleRepository roles, IUserRoleRepository userroles)
        {
            _userStore = userStore;
            _permissionStore = permissionStore;
            _sessions = sessions;
            _folders = folders;
            _roles = roles;
            _userroles = userroles;
        }

        // The read path on the seam: the IQueryable is materialized inside this method, so it never leaves the
        // repository (D7). ToList/Where/FirstOrDefault are plain LINQ rather than EF Core's async operators,
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

        public async Task<IEnumerable<User>> GetUsersAsync()
        {
            return await QueryAsync(session => session.Query<UserAggregate>()
                .ToList()
                .ConvertAll(UserMapper.ToModel));
        }

        public async Task<User> AddUserAsync(User user)
        {
            // Harvested behaviour: a User row is shared across sites (the table has no SiteId — User.SiteId is
            // [NotMapped] request state), so registering an existing username on a second site must NOT insert
            // a second row. It re-reads the existing row and re-points its transient SiteId instead.
            var existing = await QueryAsync(session => UserMapper.ToModel(session.Query<UserAggregate>()
                .FirstOrDefault(item => item.Username == user.Username)));
            if (existing == null)
            {
                var aggregate = UserMapper.ToAggregate(user, assignId: false);
                await _userStore.AddAsync(aggregate);
                UserMapper.CopyPersistedValues(aggregate, user);
            }
            else
            {
                int siteId = user.SiteId;
                user = existing;
                user.SiteId = siteId;
            }

            // add folder for user
            var folder = await _folders.GetFolderAsync(user.SiteId, "Users/");
            if (folder != null)
            {
                await _folders.AddFolderAsync(new Folder
                {
                    SiteId = folder.SiteId,
                    ParentId = folder.FolderId,
                    Name = "My Folder",
                    Type = FolderTypes.Private,
                    Path = $"Users/{user.UserId}/",
                    Order = 1,
                    ImageSizes = "",
                    Capacity = Constants.UserFolderCapacity,
                    IsSystem = true,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.Browse, user.UserId, true),
                        new Permission(PermissionNames.View, user.UserId, true),
                        // BL-115: a user's My Folder is PRIVATE. It used to grant View to Everyone, and an anonymous caller
                        // matches Everyone, so every file under Users/{id}/ was downloadable by anyone who guessed the
                        // sequential file id. The owner and site administrators see it; nobody else does.
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, user.UserId, true)
                    }
                });
            }

            // add auto assigned roles to user for site
            var roles = (await _roles.GetRolesAsync(user.SiteId)).Where(item => item.IsAutoAssigned).ToList();
            foreach (var role in roles)
            {
                var userrole = new UserRole();
                userrole.UserId = user.UserId;
                userrole.RoleId = role.RoleId;
                userrole.EffectiveDate = null;
                userrole.ExpiryDate = null;
                userrole.IgnoreSecurityStamp = true;
                await _userroles.AddUserRoleAsync(userrole);
            }

            return user;
        }

        public async Task<User> UpdateUserAsync(User user)
        {
            var aggregate = UserMapper.ToAggregate(user, assignId: true);
            await _userStore.UpdateAsync(aggregate);
            UserMapper.CopyPersistedValues(aggregate, user);
            return user;
        }

        public async Task<User> GetUserAsync(int userId)
        {
            return await GetUserAsync(userId, true);
        }

        public async Task<User> GetUserAsync(int userId, bool tracking)
        {
            // note that the tracking parameter is no longer relevant: the seam hands back a detached aggregate
            // and UserMapper projects it onto a fresh wire contract either way.
            //
            // Read through the query session, NOT _userStore.GetByIdAsync — that is load-bearing, not a style
            // choice (D19). The store's IRepository.GetByIdAsync TRACKS the aggregate in the store's DbContext,
            // and UpdateAsync then attaches a DIFFERENT instance rebuilt from the wire contract with the same
            // key, which EF rejects. UserController.Put walks straight into it: GetUser(user.UserId, false) to
            // authorize, then UpdateUser(user) — both on this same repository instance, hence the same store
            // and the same context. Pinned by UserStoreBehaviourTests.A_session_read_then_a_store_write_persists.
            return await QueryAsync(session => UserMapper.ToModel(session.Query<UserAggregate>()
                .FirstOrDefault(item => item.Id == userId)));
        }

        public async Task<User> GetUserAsync(string username)
        {
            return await GetUserAsync(username, "");
        }

        // Both lookups are filtered in the DATABASE on the user's own columns, exactly as db.User.Where did.
        // This is the sign-in path (UserManager.LoginUser), so it runs on every authentication.
        public async Task<User> GetUserAsync(string username, string email)
        {
            User user = null;
            if (!string.IsNullOrEmpty(username))
            {
                user = await QueryAsync(session => UserMapper.ToModel(session.Query<UserAggregate>()
                    .FirstOrDefault(item => item.Username == username)));
            }
            if (user == null && !string.IsNullOrEmpty(email))
            {
                user = await QueryAsync(session => UserMapper.ToModel(session.Query<UserAggregate>()
                    .FirstOrDefault(item => item.Email == email)));
            }
            return user;
        }

        public async Task DeleteUserAsync(int userId)
        {
            var aggregate = await _userStore.GetByIdAsync(userId);
            if (aggregate == null)
            {
                return;
            }

            // remove permissions for user — now on the seam (D24), through IPermissionStore rather than a raw
            // db.Permission (see the remarks on this class for why it is the store and not the repository).
            // The UserId filter stays in the DATABASE, as db.Permission.Where did: the store's only read is
            // GetAllAsync, so reading through it would scan every permission in the tenant. Read filtered
            // through the session, delete each detached aggregate through the store (D19/rule 9).
            var permissions = await QueryAsync(session => session.Query<PermissionAggregate>()
                .Where(item => item.UserId == userId)
                .ToList());
            foreach (var permission in permissions)
            {
                await _permissionStore.DeleteAsync(permission);
            }

            await _userStore.DeleteAsync(aggregate);
        }
    }
}
