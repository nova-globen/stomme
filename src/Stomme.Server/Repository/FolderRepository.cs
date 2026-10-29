using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface IFolderRepository
    {
        Task<IEnumerable<Folder>> GetFoldersAsync(int siteId);
        Task<Folder> AddFolderAsync(Folder folder);
        Task<Folder> UpdateFolderAsync(Folder folder);
        Task<Folder> GetFolderAsync(int folderId);
        Task<Folder> GetFolderAsync(int folderId, bool tracking);
        Task<Folder> GetFolderAsync(int siteId, string path);
        Task DeleteFolderAsync(int folderId);
        Task<string> GetFolderPathAsync(int folderId);
        Task<string> GetFolderPathAsync(Folder folder);
    }

    /// <summary>
    /// The Folder cluster on the persistence seam: writes through <see cref="IFolderStore"/>, reads
    /// through <see cref="IFrameworkStoreSessionFactory"/>. The <c>DbSet&lt;Folder&gt;</c> is gone from
    /// <see cref="TenantDBContext"/> — that deletion is the compiler's proof this cluster moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its File half did NOT move, and that is D48's finding rather than an omission.</b> The two were
    /// planned to land together because <c>File.Folder</c> was a mapped navigation into the Folder contract;
    /// that half is done (the navigation is severed and <see cref="FileRepository"/> stitches the member on by
    /// hand). What blocked the File ROW is that its two name lookups filter with <c>.ToLower()</c> on the
    /// COLUMN, and no portable spelling of that exists on this seam — see src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// <b>The one outbound edge is <see cref="IPermissionRepository"/>, and it is acyclic (D22 rule 8).</b>
    /// Re-checked per edge rather than inherited: this cluster ALREADY held that edge before the move, so the
    /// container already proved it resolves. The chain is Folder → Permission → Role → Setting, and Setting
    /// takes no repository that reaches back here. Nothing this increment adds is new, so the
    /// store-instead-of-repository escape D24 needed for Permission is not needed here.
    /// </para>
    /// <para>
    /// <b><see cref="ITenantManager"/> is NOT a persistence edge</b>, which is why it survives the move
    /// untouched. It is here for <see cref="GetFolderPathAsync(Folder)"/> alone, which resolves a folder row to
    /// a physical DIRECTORY on disk — <c>Content/Tenants/{tenantId}/Sites/{siteId}/{path}</c>. The tenant id is
    /// part of that path. Nothing about it reads or writes the database.
    /// </para>
    /// <para>
    /// <b>No read here writes, so this cluster does NOT need D47's per-write store.</b> Checked deliberately,
    /// because that is the hazard every gate missed: <c>UrlMappingRepository</c> had to resolve a store per
    /// write because its READ recorded a request, so one repository instance wrote the same row twice and hit
    /// the identity map. Every method below is either a pure read or a single write, and no caller does
    /// Get-then-Update through one instance of this repository — <c>FolderController.Put</c> reads through
    /// <see cref="GetFolderAsync(int, bool)"/>, which is a SESSION read that tracks nothing, and then
    /// writes a detached model. Constructor injection is correct here; see <c>TheFolderWriteLifetimeTests</c>,
    /// which pins it.
    /// </para>
    /// </remarks>
    public class FolderRepository : IFolderRepository
    {
        private readonly IFolderStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IPermissionRepository _permissions;
        private readonly IWebHostEnvironment _environment;
        private readonly ITenantManager _tenants;

        public FolderRepository(IFolderStore store, IFrameworkStoreSessionFactory sessions, IPermissionRepository permissions, IWebHostEnvironment environment, ITenantManager tenants)
        {
            _store = store;
            _sessions = sessions;
            _permissions = permissions;
            _environment = environment;
            _tenants = tenants;
        }

        /// <summary>
        /// Opens a read session, runs <paramref name="read"/> against it and disposes it. Reads NEVER go
        /// through the store.
        /// </summary>
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
        /// A site's folders, hierarchy-ordered, each with its permissions attached.
        /// </summary>
        /// <remarks>
        /// The SiteId filter stays in the DATABASE; the hierarchy walk was always in memory and stays there.
        /// That is not a rule 9 concession — <see cref="GetFoldersHierarchy"/> is a recursive parent/child sort
        /// with no SQL equivalent in the harvested code either.
        /// </remarks>
        public async Task<IEnumerable<Folder>> GetFoldersAsync(int siteId)
        {
            var permissions = (await _permissions.GetPermissionsAsync(siteId, EntityNames.Folder)).ToList();

            var folders = await QueryAsync(session => session.Query<FolderAggregate>()
                .Where(item => item.SiteId == siteId)
                .ToList()
                .ConvertAll(FolderMapper.ToModel));

            foreach (var folder in folders)
            {
                folder.PermissionList = permissions.Where(item => item.EntityId == folder.FolderId).ToList();
            }

            return GetFoldersHierarchy(folders);
        }

        private static List<Folder> GetFoldersHierarchy(List<Folder> folders)
        {
            List<Folder> hierarchy = new List<Folder>();
            Action<List<Folder>, Folder> getPath = null;
            getPath = (folderList, folder) =>
            {
                IEnumerable<Folder> children;
                int level;
                if (folder == null)
                {
                    level = -1;
                    children = folders.Where(item => item.ParentId == null);
                }
                else
                {
                    level = folder.Level;
                    children = folders.Where(item => item.ParentId == folder.FolderId);
                }

                foreach (Folder child in children)
                {
                    child.Level = level + 1;
                    child.HasChildren = folders.Any(item => item.ParentId == child.FolderId);
                    hierarchy.Add(child);
                    getPath(folderList, child);
                }
            };
            folders = folders.OrderBy(item => item.Name).ToList();
            getPath(folders, null);

            // add any non-hierarchical items to the end of the list
            foreach (Folder folder in folders)
            {
                if (hierarchy.Find(item => item.FolderId == folder.FolderId) == null)
                {
                    hierarchy.Add(folder);
                }
            }

            return hierarchy;
        }

        public async Task<Folder> AddFolderAsync(Folder folder)
        {
            var aggregate = FolderMapper.ToAggregate(folder, assignId: false);
            await _store.AddAsync(aggregate);
            FolderMapper.CopyPersistedValues(aggregate, folder);
            await _permissions.UpdatePermissionsAsync(folder.SiteId, EntityNames.Folder, folder.FolderId, folder.PermissionList);
            return folder;
        }

        public async Task<Folder> UpdateFolderAsync(Folder folder)
        {
            var aggregate = FolderMapper.ToAggregate(folder, assignId: true);
            await _store.UpdateAsync(aggregate);
            FolderMapper.CopyPersistedValues(aggregate, folder);
            await _permissions.UpdatePermissionsAsync(folder.SiteId, EntityNames.Folder, folder.FolderId, folder.PermissionList);
            return folder;
        }

        public async Task<Folder> GetFolderAsync(int folderId)
        {
            return await GetFolderAsync(folderId, true);
        }

        /// <summary>
        /// NOTE the <paramref name="tracking"/> parameter is now INERT, and deliberately kept on the
        /// interface. A session read tracks nothing, so both arms return the same detached model. The
        /// harvested tracking arm never mattered to any caller: every mutating caller
        /// (<c>FolderController.Put</c>, <c>UserManager</c>) hands the result back to
        /// <see cref="UpdateFolderAsync"/>, which re-attaches it on a different unit of work. Removing the
        /// overload would churn <c>FolderController</c> and <c>FileController</c> for no behaviour change; it
        /// stays at parity.
        /// </summary>
        public async Task<Folder> GetFolderAsync(int folderId, bool tracking)
        {
            var folder = await QueryAsync(session => FolderMapper.ToModel(session.Query<FolderAggregate>()
                .FirstOrDefault(item => item.Id == folderId)));

            if (folder != null)
            {
                folder.PermissionList = (await _permissions.GetPermissionsAsync(folder.SiteId, EntityNames.Folder, folder.FolderId))?.ToList();
            }

            return folder;
        }

        /// <summary>
        /// Resolves a folder by its path within a site — the read <c>UserRepository</c>, <c>UserManager</c>,
        /// <c>FolderController</c> and <c>DefaultSiteTemplate</c> all use to find a well-known folder.
        /// </summary>
        /// <remarks>
        /// <b>An EXACT, CASE-SENSITIVE match, and it is kept exactly as harvested.</b> Every caller passes the
        /// stored case (<c>"Users/"</c>, <c>$"Users/{userid}/"</c>, <c>""</c>) and the live database stores
        /// mixed case (<c>Public/</c>, <c>Users/1/</c>), so this works — but note it is NOT the same filter
        /// <see cref="FileRepository"/> needs, which is case-INSENSITIVE because it resolves a lowercased URL
        /// segment. Do not "reuse" this one there: <c>Public/</c> would not match a request for
        /// <c>public/</c> and every file would 404. That distinction is what D48 measured; see
        /// src/Stomme.Server/README.md (persistence seam).
        /// <para>
        /// The <c>(site_id, path)</c> UNIQUE index is what makes this a single-row lookup rather than a "pick
        /// one" — and it is a relational constraint with no RavenDB equivalent. See
        /// <see cref="FolderAggregate"/>.
        /// </para>
        /// </remarks>
        public async Task<Folder> GetFolderAsync(int siteId, string path)
        {
            var folder = await QueryAsync(session => FolderMapper.ToModel(session.Query<FolderAggregate>()
                .FirstOrDefault(item => item.SiteId == siteId && item.Path == path)));

            if (folder != null)
            {
                folder.PermissionList = (await _permissions.GetPermissionsAsync(folder.SiteId, EntityNames.Folder, folder.FolderId))?.ToList();
            }

            return folder;
        }

        public async Task DeleteFolderAsync(int folderId)
        {
            var folder = await _store.GetByIdAsync(folderId);
            await _permissions.DeletePermissionsAsync(folder.SiteId, EntityNames.Folder, folderId);
            await _store.DeleteAsync(folder);
        }

        public async Task<string> GetFolderPathAsync(int folderId)
        {
            var folder = await QueryAsync(session => FolderMapper.ToModel(session.Query<FolderAggregate>()
                .FirstOrDefault(item => item.Id == folderId)));

            return await GetFolderPathAsync(folder);
        }

        /// <summary>
        /// Resolves a folder row to its physical directory on disk. The only reason this cluster holds
        /// <see cref="ITenantManager"/> — see the remarks on this class.
        /// </summary>
        public async Task<string> GetFolderPathAsync(Folder folder)
        {
            string path = "";
            switch (folder.Type)
            {
                case FolderTypes.Private:
                    path = Utilities.PathCombine(_environment.ContentRootPath, "Content", "Tenants", (await _tenants.GetTenantAsync()).TenantId.ToString(), "Sites", folder.SiteId.ToString(), folder.Path);
                    break;
                case FolderTypes.Public:
                    path = Utilities.PathCombine(_environment.WebRootPath, "Content", "Tenants", (await _tenants.GetTenantAsync()).TenantId.ToString(), "Sites", folder.SiteId.ToString(), folder.Path);
                    break;
            }
            return path;
        }
    }
}
