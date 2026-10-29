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
    public interface IPageModuleRepository
    {
        Task<IEnumerable<PageModule>> GetPageModulesAsync(int siteId);
        Task<PageModule> AddPageModuleAsync(PageModule pageModule);
        Task<PageModule> UpdatePageModuleAsync(PageModule pageModule);
        Task<PageModule> GetPageModuleAsync(int pageModuleId);
        Task<PageModule> GetPageModuleAsync(int pageModuleId, bool tracking);
        Task<PageModule> GetPageModuleAsync(int pageId, int moduleId);
        Task DeletePageModuleAsync(int pageModuleId);
    }

    /// <summary>
    /// The PageModule cluster on the persistence seam: writes through <see cref="IPageModuleStore"/>,
    /// reads through <see cref="IFrameworkStoreSessionFactory"/>. The <c>DbSet&lt;PageModule&gt;</c> is
    /// gone from <see cref="TenantDBContext"/> — that deletion is the compiler's proof this cluster moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D19 and D21 deliberately left this cluster behind, and that is why it was cheap to move now.</b>
    /// PageModule.Page and PageModule.Module were EF navigations eager-loaded with <c>.Include(...)</c>. When
    /// the Page cluster and the Module cluster took those tables onto the seam, both navigations
    /// had to go — they were severed to ids and this class started stitching the contract's Page and Module
    /// members on from second queries, while the PageModule ROW stayed in <c>TenantDBContext</c>. So this
    /// increment is a row move with no navigation work: the hard part was done, twice, by someone else.
    /// </para>
    /// <para>
    /// Page is read via <see cref="IFrameworkStoreSessionFactory"/> rather than <c>IPageRepository</c>
    /// deliberately: <c>PageRepository</c> already depends on <see cref="IPageModuleRepository"/>
    /// (<c>DeletePage</c> removes a page's module instances through this class), so injecting
    /// <c>IPageRepository</c> here would be a circular DI dependency. Module is read via
    /// <see cref="IModuleRepository"/>, which is cycle-free: this class ALREADY depends on it and
    /// <c>ModuleRepository</c> depends on Permissions/Settings/ModuleDefinitions/IServiceProvider — never on
    /// this class.
    /// </para>
    /// <para>
    /// <b>THE FOUR OUTBOUND EDGES ARE ALL ACYCLIC, RE-CHECKED FOR THIS INCREMENT (D22 rule 8).</b>
    /// ModuleDefinition, Module, Permission and Setting. The one that looks dangerous is
    /// <see cref="IPermissionRepository"/>, because <c>PermissionRepository</c> takes
    /// <see cref="IRoleRepository"/> back — that is exactly why <c>RoleRepository</c> and
    /// <c>UserRepository</c> take <see cref="IPermissionStore"/> instead of the repository. It is NOT a
    /// problem here: the cycle D24 avoids is Permission→Role→Permission, and NOTHING on Permission's outbound
    /// path (Role→Setting→stores) reaches back into PageModule. So this class may hold the REPOSITORY, and
    /// should: <c>PermissionRepository</c> is cache-backed (<c>ICacheManager</c>) and every render reads its
    /// permissions through that cache. Swapping to <see cref="IPermissionStore"/> here to be "safe" would
    /// bypass the cache on the hottest read in the product and buy nothing.
    /// </para>
    /// <para>
    /// <b>ON THE RENDER PATH — the most render-critical cluster on the seam.</b> Every page render enumerates
    /// its page's module instances through <see cref="GetPageModulesAsync"/>. A fault here is a blank page.
    /// </para>
    /// </remarks>
    public class PageModuleRepository : IPageModuleRepository
    {
        private readonly IPageModuleStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly IModuleRepository _modules;
        private readonly IPermissionRepository _permissions;
        private readonly ISettingRepository _settings;

        public PageModuleRepository(IPageModuleStore store, IFrameworkStoreSessionFactory sessions, IModuleDefinitionRepository moduleDefinitions, IModuleRepository modules, IPermissionRepository permissions, ISettingRepository settings)
        {
            _store = store;
            _sessions = sessions;
            _moduleDefinitions = moduleDefinitions;
            _modules = modules;
            _permissions = permissions;
            _settings = settings;
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

        // One query for the whole site, mirroring what the Page Include's join did. Deliberately NOT filtered
        // on UserId: personalized pages carry module instances too, and the harvested join returned them.
        private async Task<Dictionary<int, Page>> GetPagesByIdAsync(int siteId)
        {
            return await QueryAsync(session => session.Query<PageAggregate>()
                .Where(item => item.SiteId == siteId)
                .ToList()
                .ConvertAll(PageMapper.ToModel)
                .ToDictionary(item => item.PageId));
        }

        private async Task<Page> GetPageByIdAsync(int pageId)
        {
            return await QueryAsync(session => PageMapper.ToModel(session.Query<PageAggregate>()
                .FirstOrDefault(item => item.Id == pageId)));
        }

        public async Task<IEnumerable<PageModule>> GetPageModulesAsync(int siteId)
        {
            // THE site filter. It used to ride the Module eager load —
            //     db.PageModule.Include(item => item.Module).Where(item => item.Module.SiteId == siteId)
            // — i.e. a join, filtered in the database. The navigation is gone, and the lazy translation
            // would be to load every PageModule row in the tenant and filter in memory. That would be a
            // correctness/perf cliff on a shared tenant database, where a "tenant" can hold many sites: it
            // would read every OTHER site's page modules on every call.
            //
            // Instead the site's module ids are resolved first, through the Module cluster's own DB-side
            // SiteId filter, and PageModule is then filtered on `ModuleId IN (...)` — still evaluated by the
            // database, and translated by every provider (the same Contains-over-a-key-list shape D45 proved).
            // The modules are needed anyway to populate pageModule.Module, so this is the SAME number of round
            // trips as the old Include, and the same rows cross the wire. Pinned by
            // GetPageModulesFiltersInTheDatabaseTests.
            var modules = (await _modules.GetModulesAsync(siteId)).ToDictionary(item => item.ModuleId);
            var moduleIds = modules.Keys.ToList();

            var pagemodules = await QueryAsync(session => session.Query<PageModuleAggregate>()
                .Where(item => moduleIds.Contains(item.ModuleId))
                .ToList()
                .ConvertAll(PageModuleMapper.ToModel));

            if (pagemodules.Any())
            {
                var moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(siteId)).ToList();
                var permissions = (await _permissions.GetPermissionsAsync(siteId, EntityNames.Module)).ToList();
                var pages = await GetPagesByIdAsync(siteId); // replaces the Page eager load (see GetPagesById)
                for (int index = 0; index < pagemodules.Count; index++)
                {
                    // replaces the Module eager load. Cloned per page module because the harvested join gave
                    // each PageModule its own Module graph and GetPageModule below mutates it (PermissionList,
                    // ModuleDefinition) — a shared instance would have the last writer win for a module that
                    // is on more than one page.
                    pagemodules[index].Module = CloneForPageModule(modules[pagemodules[index].ModuleId]);
                    pagemodules[index] = await GetPageModuleAsync(pagemodules[index], moduledefinitions, permissions);
                    pagemodules[index].Page = pages.GetValueOrDefault(pagemodules[index].PageId);
                }
            }
            return pagemodules;
        }

        // Module.Clone() is not usable here: it dereferences PermissionList and Settings, which are null on a
        // module straight off the seam. This copies exactly the persisted columns, which is all the harvested
        // Include produced.
        private static Module CloneForPageModule(Module module) => new()
        {
            ModuleId = module.ModuleId,
            SiteId = module.SiteId,
            ModuleDefinitionName = module.ModuleDefinitionName,
            AllPages = module.AllPages,
            CreatedBy = module.CreatedBy,
            CreatedOn = module.CreatedOn,
            ModifiedBy = module.ModifiedBy,
            ModifiedOn = module.ModifiedOn
        };

        public async Task<PageModule> AddPageModuleAsync(PageModule pageModule)
        {
            var aggregate = PageModuleMapper.ToAggregate(pageModule, assignId: false);
            await _store.AddAsync(aggregate);
            // ModuleController.Post creates a module and its first instance and returns the result to the
            // client, which navigates to the new instance by id — see PageModuleMapper.
            PageModuleMapper.CopyPersistedValues(aggregate, pageModule);
            return pageModule;
        }

        public async Task<PageModule> UpdatePageModuleAsync(PageModule pageModule)
        {
            var aggregate = PageModuleMapper.ToAggregate(pageModule, assignId: true);
            await _store.UpdateAsync(aggregate);
            PageModuleMapper.CopyPersistedValues(aggregate, pageModule);
            return pageModule;
        }

        public async Task<PageModule> GetPageModuleAsync(int pageModuleId)
        {
            return await GetPageModuleAsync(pageModuleId, true);
        }

        /// <summary>
        /// Reads one module instance and populates its severed Module and Page members, its ModuleDefinition,
        /// its PermissionList and its <c>Module.IsShared</c> flag.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A DELIBERATE DIVERGENCE: this returns null for an unknown id where the harvested code threw
        /// NullReferenceException.</b> The harvested body set <c>pagemodule.Module.IsShared</c> OUTSIDE the
        /// null check that guards everything else, so an unknown id threw before it could return null.
        /// </para>
        /// <para>
        /// The divergence is not a tidy-up — it makes existing, currently-unreachable caller code reachable.
        /// <c>PageModuleController.Get</c> ends with <c>else { HttpContext.Response.StatusCode = NotFound; }</c>
        /// for exactly the null this method could never return; that 404 branch is DEAD CODE today and the API
        /// answers 500 instead. <c>PageModuleController.Put</c>'s validation guard reads
        /// <c>await _pageModules.GetPageModuleAsync(id, false) != null</c> — a comparison that can never be
        /// false, so a PUT against a deleted instance 500s instead of falling through to BadRequest. Two named
        /// call sites already encode the contract this restores.
        /// </para>
        /// <para>
        /// It is also consistent in two directions. Within this class, the sibling
        /// <see cref="GetPageModuleAsync(int, int)"/> already returns null for a miss. Across the migrated
        /// clusters, D24 made the same strictly-safer call in <c>DeletePermissionAsync</c> for the same shape.
        /// </para>
        /// <para>
        /// One caller is NOT improved and is named rather than hidden: <c>SettingController</c> does
        /// <c>(await _pageModules.GetPageModuleAsync(entityId)).ModuleId</c>. It threw NullReferenceException
        /// before (from inside this method) and throws NullReferenceException now (from its own dereference).
        /// The exception moves; it does not appear or disappear.
        /// </para>
        /// <para>
        /// <paramref name="tracking"/> is now INERT — a session read tracks nothing. It is kept on the
        /// interface because <c>PageModuleController.Put</c> passes it and removing it would churn the
        /// controller for no behaviour change. Nothing here ever depended on tracking to persist: the harvested
        /// code read on one context and wrote through <c>db.Entry(...).State = Modified</c> on another.
        /// </para>
        /// </remarks>
        public async Task<PageModule> GetPageModuleAsync(int pageModuleId, bool tracking)
        {
            var pagemodule = await QueryAsync(session => PageModuleMapper.ToModel(session.Query<PageModuleAggregate>()
                .FirstOrDefault(item => item.Id == pageModuleId)));

            if (pagemodule == null)
            {
                return null;
            }

            pagemodule.Module = await _modules.GetModuleAsync(pagemodule.ModuleId, tracking); // replaces the Module eager load
            var moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(pagemodule.Module.SiteId)).ToList();
            var permissions = (await _permissions.GetPermissionsAsync(pagemodule.Module.SiteId, EntityNames.Module)).ToList();
            pagemodule = await GetPageModuleAsync(pagemodule, moduledefinitions, permissions);
            pagemodule.Page = await GetPageByIdAsync(pagemodule.PageId); // replaces the Page eager load
            pagemodule.Module.IsShared = await CountModuleInstancesAsync(pagemodule.ModuleId) > 1;

            return pagemodule;
        }

        /// <summary>
        /// How many pages this module is instanced on — the input to <c>Module.IsShared</c>, which the module
        /// settings UI shows and which <c>ModuleController</c> branches on.
        /// </summary>
        /// <remarks>
        /// <b>Still a SERVER-SIDE COUNT, and that is the point.</b> The harvested call was
        /// <c>db.PageModule.CountAsync(item =&gt; item.ModuleId == id)</c> — a database <c>COUNT</c> that never
        /// materialized a row. The seam's <see cref="IRepository{TAggregate, TId}"/> has no count primitive, so
        /// the obvious port is <c>Query&lt;T&gt;().Where(...).ToList().Count</c> — which would fetch every
        /// instance of the module just to measure the list. That is avoided: <see cref="IQuerySession.Query"/>
        /// hands back an <see cref="IQueryable{T}"/>, and <c>Count(predicate)</c> over it is composed into the
        /// provider's own query on BOTH halves of the seam — SQL <c>COUNT(*)</c> on the relational side, a
        /// server-side count on RavenDB. So no rows cross the wire and the harvested shape is preserved
        /// exactly. Because "does this translate on both providers" is precisely the question D45 got wrong by
        /// assuming, it is answered by the conformance suite rather than by this comment — see
        /// <c>A_module_instance_count_is_computed_by_the_provider_not_by_materializing_the_rows</c>.
        /// </remarks>
        private async Task<int> CountModuleInstancesAsync(int moduleId) =>
            await QueryAsync(session => session.Query<PageModuleAggregate>().Count(item => item.ModuleId == moduleId));

        public async Task<PageModule> GetPageModuleAsync(int pageId, int moduleId)
        {
            var pagemodule = await QueryAsync(session => PageModuleMapper.ToModel(session.Query<PageModuleAggregate>()
                .SingleOrDefault(item => item.PageId == pageId && item.ModuleId == moduleId)));

            if (pagemodule != null)
            {
                pagemodule.Module = await _modules.GetModuleAsync(pagemodule.ModuleId, false); // replaces the Module eager load
                var moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(pagemodule.Module.SiteId)).ToList();
                var permissions = (await _permissions.GetPermissionsAsync(pagemodule.Module.SiteId, EntityNames.Module)).ToList();
                pagemodule = await GetPageModuleAsync(pagemodule, moduledefinitions, permissions);
                pagemodule.Page = await GetPageByIdAsync(pagemodule.PageId); // replaces the Page eager load
            }
            return pagemodule;
        }

        /// <summary>
        /// Deletes a module instance, its settings, and the module itself once its last instance is gone.
        /// </summary>
        /// <remarks>
        /// Returns silently for an unknown id where the harvested code threw NullReferenceException — the same
        /// strictly-safer divergence D24 made in <c>DeletePermissionAsync</c> for this exact shape, and
        /// consistent with DeleteSetting/DeleteModule/DeleteRole/DeleteUser.
        /// </remarks>
        public async Task DeletePageModuleAsync(int pageModuleId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(pageModuleId);
            if (aggregate == null)
            {
                return;
            }

            var moduleId = aggregate.ModuleId;
            // no Page/Module eager load: this method needs only the module's SiteId, read below through the
            // Module cluster, and the local never escapes.
            var module = await _modules.GetModuleAsync(moduleId, false);
            await _settings.DeleteSettingsAsync(EntityNames.PageModule, pageModuleId);
            await _store.DeleteAsync(aggregate);

            // check if there are any remaining module instances in the site
            var pageModules = await GetPageModulesAsync(module.SiteId);
            if (!pageModules.Any(item => item.ModuleId == moduleId))
            {
                await _modules.DeleteModuleAsync(moduleId);
            }
        }

        private async Task<PageModule> GetPageModuleAsync(PageModule pageModule, List<ModuleDefinition> moduleDefinitions, List<Permission> modulePermissions)
        {
            var permissions = modulePermissions.Where(item => item.EntityId == pageModule.ModuleId).ToList();

            // moduledefinition permissionnames can specify permissions for other entities (ie. API permissions)
            pageModule.Module.ModuleDefinition = moduleDefinitions.Find(item => item.ModuleDefinitionName == pageModule.Module.ModuleDefinitionName);
            if (pageModule.Module.ModuleDefinition != null && !string.IsNullOrEmpty(pageModule.Module.ModuleDefinition.PermissionNames) && pageModule.Module.ModuleDefinition.PermissionNames.Contains(":"))
            {
                foreach (var permissionname in pageModule.Module.ModuleDefinition.PermissionNames.Split(",", System.StringSplitOptions.RemoveEmptyEntries))
                {
                    if (permissionname.Contains(":"))
                    {
                        // moduledefinition permissionnames can be in the form of "EntityName:PermissionName:Roles"
                        var segments = permissionname.Split(':');
                        if (segments.Length == 3 && segments[0] != EntityNames.Module)
                        {
                            permissions.AddRange((await _permissions.GetPermissionsAsync(pageModule.Module.SiteId, segments[0], segments[1])).Where(item => item.EntityId == -1));
                        }
                    }
                }
            }
            pageModule.Module.PermissionList = permissions?.ToList();

            return pageModule;
        }
    }
}
