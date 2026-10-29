using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface IPageRepository
    {
        Task<IEnumerable<Page>> GetPagesAsync(int siteId);
        Task<Page> AddPageAsync(Page page);
        Task<Page> UpdatePageAsync(Page page);
        Task<Page> GetPageAsync(int pageId);
        Task<Page> GetPageAsync(int pageId, bool tracking);
        Task<Page> GetPageAsync(string path, int siteId);
        Task DeletePageAsync(int pageId);
    }

    /// <summary>
    /// The Page cluster on the persistence seam. Page-table access goes through
    /// <see cref="IPageStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// Deliberately OUT of this cluster, mirroring the Sites precedent: the <see cref="Permission"/>,
    /// <see cref="Setting"/> and <see cref="PageModule"/> composition below. A page's PermissionList is
    /// denormalized onto the wire contract by <see cref="IPermissionRepository"/>, its settings are deleted
    /// through <see cref="ISettingRepository"/>, and its module instances are removed through
    /// <see cref="IPageModuleRepository"/> — each a different aggregate with its own root, its own repository
    /// and its own brief, all still on the <c>IDatabase</c>/<c>TenantDBContext</c> path. Coexistence is the
    /// D3-safe plan; those calls move when their own clusters do.
    /// </remarks>
    public class PageRepository : IPageRepository
    {
        private readonly IPageStore _pageStore;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IPageModuleRepository _pageModules;
        private readonly IPermissionRepository _permissions;
        private readonly ISettingRepository _settings;

        public PageRepository(IPageStore pageStore, IFrameworkStoreSessionFactory sessions, IPageModuleRepository pageModules, IPermissionRepository permissions, ISettingRepository settings)
        {
            _pageStore = pageStore;
            _sessions = sessions;
            _pageModules = pageModules;
            _permissions = permissions;
            _settings = settings;
        }

        public async Task<IEnumerable<Page>> GetPagesAsync(int siteId)
        {
            var permissions = (await _permissions.GetPermissionsAsync(siteId, EntityNames.Page)).ToList();
            var pages = await QueryAsync(session => session.Query<PageAggregate>()
                .Where(item => item.SiteId == siteId && item.UserId == null)
                .ToList()
                .ConvertAll(PageMapper.ToModel));
            foreach (var page in pages)
            {
                page.PermissionList = permissions.Where(item => item.EntityId == page.PageId).ToList();
            }
            return GetPagesHierarchy(pages);
        }

        // The read path on the seam: the IQueryable is materialized inside this method, so it never leaves
        // the repository. ToList/Where/OrderBy are plain LINQ rather than EF Core's async operators,
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

        private static List<Page> GetPagesHierarchy(List<Page> pages)
        {
            List<Page> hierarchy = new List<Page>();
            Action<List<Page>, Page> getPath = null;
            getPath = (pageList, page) =>
            {
                IEnumerable<Page> children;
                int level;
                if (page == null)
                {
                    level = -1;
                    children = pages.Where(item => item.ParentId == null);
                }
                else
                {
                    level = page.Level;
                    children = pages.Where(item => item.ParentId == page.PageId);
                }
                foreach (Page child in children)
                {
                    child.Level = level + 1;
                    child.HasChildren = pages.Any(item => item.ParentId == child.PageId && !item.IsDeleted && item.IsNavigation);
                    hierarchy.Add(child);
                    getPath(pageList, child);
                }
            };
            pages = pages.OrderBy(item => item.Order).ToList();
            getPath(pages, null);

            // add any non-hierarchical items to the end of the list
            foreach (Page page in pages)
            {
                if (hierarchy.Find(item => item.PageId == page.PageId) == null)
                {
                    hierarchy.Add(page);
                }
            }
            return hierarchy;
        }

        public async Task<Page> AddPageAsync(Page page)
        {
            var aggregate = PageMapper.ToAggregate(page, assignId: false);
            await _pageStore.AddAsync(aggregate);
            PageMapper.CopyPersistedValues(aggregate, page);
            await _permissions.UpdatePermissionsAsync(page.SiteId, EntityNames.Page, page.PageId, page.PermissionList);
            return page;
        }

        public async Task<Page> UpdatePageAsync(Page page)
        {
            var aggregate = PageMapper.ToAggregate(page, assignId: true);
            await _pageStore.UpdateAsync(aggregate);
            PageMapper.CopyPersistedValues(aggregate, page);
            await _permissions.UpdatePermissionsAsync(page.SiteId, EntityNames.Page, page.PageId, page.PermissionList);
            return page;
        }

        public async Task<Page> GetPageAsync(int pageId)
        {
            return await GetPageAsync(pageId, true);
        }

        public async Task<Page> GetPageAsync(int pageId, bool tracking)
        {
            // note that the tracking parameter is no longer relevant: the seam hands back a detached
            // aggregate and PageMapper projects it onto a fresh wire contract either way.
            //
            // Read through the query session, NOT _pageStore.GetByIdAsync — that is load-bearing, not a
            // style choice. The store's IRepository.GetByIdAsync TRACKS the aggregate in the store's
            // DbContext, and UpdateAsync then attaches a DIFFERENT instance rebuilt from the wire contract
            // with the same key, which EF rejects:
            //   "The instance of entity type 'PageAggregate' cannot be tracked because another instance
            //    with the same key value for {'Id'} is already being tracked."
            // PageController.Put does exactly that: GetPage(id) to authorize, then UpdatePage(page) — both
            // on this same repository instance, hence the same store and the same context. The query session
            // opens its own context, so reads leave the store's change tracker clean. This mirrors
            // SiteRepository, whose GetSite reads via the session and only ever writes through the store.
            // Pinned by A_read_then_write_through_one_store_instance_persists.
            var page = await QueryAsync(session => PageMapper.ToModel(session.Query<PageAggregate>()
                .FirstOrDefault(item => item.Id == pageId)));
            if (page != null)
            {
                page.PermissionList = (await _permissions.GetPermissionsAsync(page.SiteId, EntityNames.Page, page.PageId))?.ToList();
            }
            return page;
        }

        public async Task<Page> GetPageAsync(string path, int siteId)
        {
            // Normalize the ARGUMENT in C# and filter the normalized COLUMN, never the raw Path. A bare
            // `item.Path == path` means two different things behind the seam — case-SENSITIVE on PostgreSQL,
            // case-INSENSITIVE on RavenDB (the D48/D57 mechanism, DRIVEN on both real hosts) — so a mixed-case
            // path silently resolved on one provider and not the other on the render RESOLVE fallback
            // (SiteRouter.razor, reached when the in-memory OrdinalIgnoreCase match at :227 misses) and the
            // admin page lookup (PageController.Get). Both sides lowercased, the providers agree
            // (case-INSENSITIVE, which also matches SiteRouter's own primary OrdinalIgnoreCase match). NEVER
            // `.ToLower()` inside the query — it THROWS on RavenDB. The STORED Path keeps its case;
            // PathNormalized is the lookup key.
            var pathNormalized = NormalizedText.Normalize(path);

            var page = await QueryAsync(session => PageMapper.ToModel(session.Query<PageAggregate>()
                .FirstOrDefault(item => item.PathNormalized == pathNormalized && item.SiteId == siteId)));
            if (page != null)
            {
                page.PermissionList = (await _permissions.GetPermissionsAsync(page.SiteId, EntityNames.Page, page.PageId))?.ToList();
            }
            return page;
        }

        public async Task DeletePageAsync(int pageId)
        {
            var aggregate = await _pageStore.GetByIdAsync(pageId);
            if (aggregate == null)
            {
                return;
            }

            await _permissions.DeletePermissionsAsync(aggregate.SiteId, EntityNames.Page, pageId);
            await _settings.DeleteSettingsAsync(EntityNames.Page, pageId);

            // Page modules belong to the Modules cluster, so they are removed through that cluster's own
            // repository rather than a DbSet on a context this class no longer holds. The harvested code
            // queried db.PageModule directly and then had to reopen its context, because DeletePageModule
            // opens and closes its own; going through the repository removes that hazard entirely.
            foreach (var pageModule in (await _pageModules.GetPageModulesAsync(aggregate.SiteId)).Where(item => item.PageId == pageId).ToList())
            {
                await _pageModules.DeletePageModuleAsync(pageModule.PageModuleId);
            }

            await _pageStore.DeleteAsync(aggregate);
        }
    }
}
