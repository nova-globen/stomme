using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using Stomme.Shared;
using System.Linq;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class PageModuleController : Controller
    {
        private readonly IPageModuleRepository _pageModules;
        private readonly IPageRepository _pages;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public PageModuleController(IPageModuleRepository pageModules, IPageRepository pages, IUserPermissions userPermissions, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger)
        {
            _pageModules = pageModules;
            _pages = pages;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<PageModule> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            PageModule pagemodule = await _pageModules.GetPageModuleAsync(id);
            if (pagemodule != null && pagemodule.Module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, pagemodule.Module.PermissionList))
            {
                return pagemodule;
            }
            else
            {
                if (pagemodule != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Get Attempt {PageModuleId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // GET: api/<controller>/pageid/moduleid
        [HttpGet("{pageid}/{moduleid}")]
        public async Task<PageModule> Get(int pageid, int moduleid)
        {
            var alias = await _tenantManager.GetAliasAsync();
            PageModule pagemodule = await _pageModules.GetPageModuleAsync(pageid, moduleid);
            if (pagemodule != null && pagemodule.Module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, pagemodule.Module.PermissionList))
            {
                return pagemodule;
            }
            else
            {
                if (pagemodule != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Get Attempt {PageId} {ModuleId}", pageid, moduleid);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<PageModule> Post([FromBody] PageModule pageModule)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var page = await _pages.GetPageAsync(pageModule.PageId);
            if (ModelState.IsValid && page != null && page.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, page.SiteId, EntityNames.Page, pageModule.PageId, PermissionNames.Edit))
            {
                pageModule = await _pageModules.AddPageModuleAsync(pageModule);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.PageModule, pageModule.PageModuleId, SyncEventActions.Create);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Page Module Added {PageModule}", pageModule);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Post Attempt {PageModule}", pageModule);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                pageModule = null;
            }
            return pageModule;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<PageModule> Put(int id, [FromBody] PageModule pageModule)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var page = await _pages.GetPageAsync(pageModule.PageId);
            if (ModelState.IsValid && page != null && page.SiteId == alias.SiteId && pageModule.PageModuleId == id && await _pageModules.GetPageModuleAsync(pageModule.PageModuleId, false) != null && await _userPermissions.IsAuthorizedAsync(User, page.SiteId, EntityNames.Page, pageModule.PageId, PermissionNames.Edit))
            {
                pageModule = await _pageModules.UpdatePageModuleAsync(pageModule);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.PageModule, pageModule.PageModuleId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Page Module Updated {PageModule}", pageModule);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Put Attempt {PageModule}", pageModule);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                pageModule = null;
            }
            return pageModule;
        }

        // PUT api/<controller>/?pageid=x&pane=y
        [HttpPut]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Put(int pageid, string pane)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var page = await _pages.GetPageAsync(pageid);
            if (page != null && page.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, page.SiteId, EntityNames.Page, pageid, PermissionNames.Edit))
            {
                var panes = pane;
                if (pane == PaneNames.Default || pane == PaneNames.Admin)
                {
                    // treat default and admin panes as a single pane
                    panes = PaneNames.Default + "," + PaneNames.Admin;
                    pane = PaneNames.Default;
                }
                int order = 1;
                List<PageModule> pagemodules = (await _pageModules.GetPageModulesAsync(page.SiteId))
                    .Where(item => item.PageId == pageid && panes.Split(',').Contains(item.Pane)).OrderBy(item => item.Order).ToList();
                foreach (PageModule pagemodule in pagemodules)
                {
                    if (pagemodule.Order != order || pagemodule.Pane != pane)
                    {
                        pagemodule.Order = order;
                        pagemodule.Pane = pane;
                        await _pageModules.UpdatePageModuleAsync(pagemodule);
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.PageModule, pagemodule.PageModuleId, SyncEventActions.Update);
                    }
                    order += 2;
                }
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Page Module Order Updated {PageId} {Pane}", pageid, pane);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Put Attempt {PageId} {Pane}", pageid, pane);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
           }
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            PageModule pagemodule = await _pageModules.GetPageModuleAsync(id);
            if (pagemodule != null && pagemodule.Module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, pagemodule.Module.SiteId, EntityNames.Page, pagemodule.PageId, PermissionNames.Edit))
            {
                await _pageModules.DeletePageModuleAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.PageModule, pagemodule.PageModuleId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Page Module Deleted {PageModuleId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized PageModule Delete Attempt {PageModuleId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
