using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class PageController : Controller
    {
        private readonly IPageRepository _pages;
        private readonly IModuleRepository _modules;
        private readonly IPageModuleRepository _pageModules;
        private readonly IPermissionRepository _permissionRepository;
        private readonly ISettingRepository _settings;
        private readonly IUserPermissions _userPermissions;
        private readonly IUrlMappingRepository _urlMappings;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public PageController(IPageRepository pages, IModuleRepository modules, IPageModuleRepository pageModules, IPermissionRepository permissionRepository, ISettingRepository settings, IUserPermissions userPermissions, IUrlMappingRepository urlMappings, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger)
        {
            _pages = pages;
            _modules = modules;
            _pageModules = pageModules;
            _permissionRepository = permissionRepository;
            _settings = settings;
            _userPermissions = userPermissions;
            _urlMappings = urlMappings;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        public async Task<IEnumerable<Page>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Page> pages = new List<Page>();

            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                List<Setting> settings = (await _settings.GetSettingsAsync(EntityNames.Page)).ToList();

                foreach (Page page in await _pages.GetPagesAsync(SiteId))
                {
                    if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, page.PermissionList))
                    {
                        var canEditPage = await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, page.PermissionList);
                        page.Settings = settings.Where(item => item.EntityId == page.PageId)
                            .Where(item => !item.IsPrivate || canEditPage)
                            .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                        pages.Add(page);
                    }
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                pages = null;
            }

            return pages;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<Page> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var page = await _pages.GetPageAsync(id);
            if (page != null && page.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, page.PermissionList))
            {
                var canEditPage = await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, page.PermissionList);
                page.Settings = (await _settings.GetSettingsAsync(EntityNames.Page, page.PageId))
                    .Where(item => !item.IsPrivate || canEditPage)
                    .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                return page;
            }
            else
            {
                if (page != null)
                {
                    _logger.Log(LogLevel.Warning, this, LogFunction.Security, "Unauthorized Page Get Attempt {PageId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // GET api/<controller>/path/x?path=y
        [HttpGet("path/{siteid}")]
        public async Task<Page> Get(string path, int siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Page page = await _pages.GetPageAsync(WebUtility.UrlDecode(path), siteid);
            if (page != null && page.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, page.PermissionList))
            {
                var canEditPage = await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, page.PermissionList);
                page.Settings = (await _settings.GetSettingsAsync(EntityNames.Page, page.PageId))
                    .Where(item => !item.IsPrivate || canEditPage)
                    .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                return page;
            }
            else
            {
                if (page != null)
                {
                    _logger.Log(LogLevel.Warning, this, LogFunction.Security, "Unauthorized Page Get Attempt {SiteId} {Path}", siteid, path);
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
        public async Task<Page> Post([FromBody] Page page)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && page.SiteId == alias.SiteId)
            {
                List<Permission> permissions;
                if (page.ParentId != null)
                {
                    permissions = (await _pages.GetPageAsync(page.ParentId.Value)).PermissionList;
                }
                else
                {
                    permissions = new List<Permission> {
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    };
                }

                if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, permissions))
                {
                    // BL-102: a row for another entity would be written as a SITE-WIDE row (EntityId -1), so Edit on
                    // the parent page would be enough to grant any framework policy. Refused BEFORE the page is written.
                    var foreign = PermissionRowScope.ForeignRows(page.PermissionList, EntityNames.Page);
                    if (foreign.Any())
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Permission Post Attempt {ParentId} {Permissions}",
                            page.ParentId, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return null;
                    }

                    page = await _pages.AddPageAsync(page);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Page, page.PageId, SyncEventActions.Create);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, page.SiteId, SyncEventActions.Refresh);
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "Page Added {Page}", page);

                    if (!page.Path.StartsWith("admin/"))
                    {
                        var modules = (await _modules.GetModulesAsync(page.SiteId)).Where(item => item.AllPages).ToList();
                        foreach (Module module in modules)
                        {
                            var pageModule = (await _pageModules.GetPageModulesAsync(page.SiteId)).FirstOrDefault(item => item.ModuleId == module.ModuleId);
                            await _pageModules.AddPageModuleAsync(new PageModule { PageId = page.PageId, ModuleId = pageModule.ModuleId, Title = pageModule.Title, Pane = pageModule.Pane, Order = pageModule.Order, ContainerType = pageModule.ContainerType });
                        }
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Warning, this, LogFunction.Create, "User Not Authorized To Add Page {Page}", page);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    page = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Post Attempt {Page}", page);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                page = null;
            }
            return page;
        }

        // POST api/<controller>/5?userid=x
        [HttpPost("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Page> Post(int id, string userid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Page page = null;
            Page parent = await _pages.GetPageAsync(id);
            User user = await _userPermissions.GetUserAsync(User);
            if (parent != null && parent.SiteId == alias.SiteId && parent.IsPersonalizable && user.UserId == int.Parse(userid))
            {
                var path = Utilities.GetFriendlyUrl(user.Username);
                page = await _pages.GetPageAsync(parent.Path + "/" + path, parent.SiteId);
                if (page == null)
                {
                    page = new Page();
                    page.SiteId = parent.SiteId;
                    page.ParentId = parent.PageId;
                    page.Name = user.Username;
                    page.Path = parent.Path + "/" + path;
                    page.Title = ((!string.IsNullOrEmpty(user.DisplayName)) ? user.DisplayName : user.Username) + " - " + parent.Name;
                    page.Order = 0;
                    page.IsNavigation = false;
                    page.Url = "";
                    page.ThemeType = parent.ThemeType;
                    page.DefaultContainerType = parent.DefaultContainerType;
                    page.Icon = parent.Icon;
                    page.PermissionList = new List<Permission>()
                    {
                        new Permission(PermissionNames.View, int.Parse(userid), true),
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.Edit, int.Parse(userid), true)
                    };
                    page.IsPersonalizable = false;
                    page.UserId = int.Parse(userid);
                    page = await _pages.AddPageAsync(page);

                    // copy parent page settings
                    var settings = await _settings.GetSettingsAsync(EntityNames.Page, parent.PageId);
                    foreach (var setting in settings)
                    {
                        await _settings.AddSettingAsync(new Setting { EntityName = EntityNames.Page, EntityId = page.PageId, SettingName = setting.SettingName, SettingValue = setting.SettingValue, IsPrivate = setting.IsPrivate });
                    }

                    // copy modules
                    List<PageModule> pagemodules = (await _pageModules.GetPageModulesAsync(page.SiteId)).ToList();
                    foreach (PageModule pm in pagemodules.Where(item => item.PageId == parent.PageId && !item.IsDeleted))
                    {
                        Module module = new Module();
                        module.SiteId = page.SiteId;
                        module.PageId = page.PageId;
                        module.ModuleDefinitionName = pm.Module.ModuleDefinitionName;
                        module.AllPages = false;
                        module.PermissionList = new List<Permission>()
                        {
                            new Permission(PermissionNames.View, int.Parse(userid), true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, int.Parse(userid), true)
                        };
                        module = await _modules.AddModuleAsync(module);

                        // deep copy module content (includes settings)
                        string content = await _modules.ExportModuleAsync(pm.Module, "Copy Page");
                        if (content != "")
                        {
                            await _modules.ImportModuleAsync(module, content, "Copy Page");
                        }

                        PageModule pagemodule = new PageModule();
                        pagemodule.PageId = page.PageId;
                        pagemodule.ModuleId = module.ModuleId;
                        pagemodule.Title = pm.Title;
                        pagemodule.Pane = pm.Pane;
                        pagemodule.Order = pm.Order;
                        pagemodule.ContainerType = pm.ContainerType;
                        pagemodule.EffectiveDate = pm.EffectiveDate;
                        pagemodule.ExpiryDate = pm.ExpiryDate;
                        pagemodule.Header = pm.Header;
                        pagemodule.Footer = pm.Footer;

                        await _pageModules.AddPageModuleAsync(pagemodule);
                    }

                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Page, page.PageId, SyncEventActions.Create);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, page.SiteId, SyncEventActions.Refresh);

                    // set user personalized page path
                    var settingName = $"PersonalizedPagePath:{page.SiteId}:{parent.PageId}";
                    var pathSetting = await _settings.GetSettingAsync(EntityNames.User, page.UserId.Value, settingName);
                    if(pathSetting == null)
                    {
                        pathSetting = new Setting { EntityName = EntityNames.User, EntityId = page.UserId.Value, SettingName = settingName, SettingValue = path, IsPrivate = false };
                        await _settings.AddSettingAsync(pathSetting);
                    }
                    else
                    {
                        pathSetting.SettingValue = path;
                        await _settings.UpdateSettingAsync(pathSetting);
                    }

                    await _syncManager.AddSyncEventAsync(alias, EntityNames.User, user.UserId, SyncEventActions.Update);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Post Attempt {PageId} By User {UserId}", id, userid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                page = null;
            }
            return page;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Page> Put(int id, [FromBody] Page page)
        {
            var alias = await _tenantManager.GetAliasAsync();

            // get current page
            var currentPage = await _pages.GetPageAsync(page.PageId, false);

            if (ModelState.IsValid && page.SiteId == alias.SiteId && page.PageId == id && currentPage != null && await _userPermissions.IsAuthorizedAsync(User, page.SiteId, EntityNames.Page, page.PageId, PermissionNames.Edit))
            {
                // BL-102: a row for another entity would be written as a SITE-WIDE row (EntityId -1). Refused here,
                // BEFORE UpdatePage below writes the page - a throw at the permission sink would already be a
                // partial write. This also keeps the module-permission sync below from ever seeing a foreign row.
                var foreign = PermissionRowScope.ForeignRows(page.PermissionList, EntityNames.Page);
                if (foreign.Any())
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Permission Put Attempt {PageId} {Permissions}",
                        id, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return null;
                }

                // get current page permissions
                var currentPermissions = (await _permissionRepository.GetPermissionsAsync(page.SiteId, EntityNames.Page, page.PageId)).ToList();

                // preserve new path and deleted status
                var newPath = page.Path;
                var deleted = page.IsDeleted;
                page.Path = currentPage.Path;
                page.IsDeleted = currentPage.IsDeleted;

                // update page
                await UpdatePage(alias, page, page.PageId, page.Path, newPath, deleted);

                // get differences between current and new page permissions
                var added = GetPermissionsDifferences(page.PermissionList, currentPermissions);
                var removed = GetPermissionsDifferences(currentPermissions, page.PermissionList);

                // synchronize module permissions
                if (page.UpdateModulePermissions && (added.Count > 0 || removed.Count > 0))
                {
                    var pageModules = await _pageModules.GetPageModulesAsync(page.SiteId);
                    foreach (PageModule pageModule in pageModules.Where(item => item.PageId == page.PageId).ToList())
                    {
                        // ignore "shared" modules
                        if (!pageModules.Any(item => item.ModuleId == pageModule.ModuleId && item.PageId != pageModule.PageId))
                        {
                            var modulePermissions = (await _permissionRepository.GetPermissionsAsync(pageModule.Module.SiteId, EntityNames.Module, pageModule.Module.ModuleId)).ToList();
                            // permissions added
                            foreach (Permission permission in added)
                            {
                                if (!modulePermissions.Any(item => item.PermissionName == permission.PermissionName
                                  && item.RoleId == permission.RoleId && item.UserId == permission.UserId && item.IsAuthorized == permission.IsAuthorized))
                                {
                                    await _permissionRepository.AddPermissionAsync(new Permission
                                    {
                                        SiteId = page.SiteId,
                                        EntityName = EntityNames.Module,
                                        EntityId = pageModule.ModuleId,
                                        PermissionName = permission.PermissionName,
                                        RoleId = permission.RoleId,
                                        UserId = permission.UserId,
                                        IsAuthorized = permission.IsAuthorized
                                    });
                                }
                            }

                            // permissions removed
                            foreach (Permission permission in removed)
                            {
                                var modulePermission = modulePermissions.FirstOrDefault(item => item.PermissionName == permission.PermissionName
                                  && item.RoleId == permission.RoleId && item.UserId == permission.UserId && item.IsAuthorized == permission.IsAuthorized);
                                if (modulePermission != null)
                                {
                                    await _permissionRepository.DeletePermissionAsync(modulePermission.PermissionId);
                                }
                            }
                        }
                    }
                }

                // update child paths
                if (page.ParentId != currentPage.ParentId)
                {
                    foreach (Page _page in (await _pages.GetPagesAsync(page.SiteId)).Where(item => item.Path.StartsWith(currentPage.Path)).ToList())
                    {
                        _page.Path = _page.Path.Replace(currentPage.Path, page.Path);
                        await _pages.UpdatePageAsync(_page);
                    }
                }

                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, page.SiteId, SyncEventActions.Refresh);

                // personalized page
                if (page.UserId != null && currentPage.Path != page.Path)
                {
                    // set user personalized page path
                    var settingName = $"PersonalizedPagePath:{page.SiteId}:{page.ParentId}";
                    var path = page.Path.Substring(page.Path.LastIndexOf("/") + 1);
                    var settings = (await _settings.GetSettingsAsync(EntityNames.User, page.UserId.Value)).ToList();
                    var setting = settings.FirstOrDefault(item => item.SettingName == settingName);
                    if (setting == null)
                    {
                        setting = new Setting { EntityName = EntityNames.User, EntityId = page.UserId.Value, SettingName = settingName, SettingValue = path, IsPrivate = false };
                        await _settings.AddSettingAsync(setting);
                    }
                    else
                    {
                        setting.SettingValue = path;
                        await _settings.UpdateSettingAsync(setting);
                    }
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.User, page.UserId.Value, SyncEventActions.Update);
                }

                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Page Updated {Page}", page);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Put Attempt {Page}", page);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                page = null;
            }
            return page;
        }

        private async Task UpdatePage(Alias alias, Page page, int pageId, string oldPath, string newPath, bool deleted)
        {
            var update = (page.PageId == pageId);
            if (oldPath != newPath)
            {
                var urlMapping = await _urlMappings.GetUrlMappingAsync(page.SiteId, page.Path);
                if (urlMapping != null)
                {
                    urlMapping.MappedUrl = newPath + page.Path.Substring(oldPath.Length);
                    await _urlMappings.UpdateUrlMappingAsync(urlMapping);
                }

                page.Path = newPath + page.Path.Substring(oldPath.Length);
                update = true;
            }
            if (deleted != page.IsDeleted)
            {
                page.IsDeleted = deleted;
                update = true;
            }
            if (update)
            {
                await _pages.UpdatePageAsync(page);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Page, page.PageId, SyncEventActions.Update);
            }

            // update any children
            foreach (var _page in (await _pages.GetPagesAsync(page.SiteId)).Where(item => item.ParentId == page.PageId))
            {
                await UpdatePage(alias, _page, pageId, oldPath, newPath, deleted);
            }
        }

        private List<Permission> GetPermissionsDifferences(List<Permission> permissions1, List<Permission> permissions2)
        {
            var differences = new List<Permission>();
            foreach (Permission p in permissions1)
            {
                if (!permissions2.Any(item => item.PermissionName == p.PermissionName && item.RoleId == p.RoleId && item.UserId == p.UserId && item.IsAuthorized == p.IsAuthorized))
                {
                    differences.Add(p);
                }
            }
            return differences;
        }

        // PUT api/<controller>/?siteid=x&pageid=y&parentid=z
        [HttpPut]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Put(int siteid, int pageid, int? parentid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (siteid == alias.SiteId && (await _pages.GetPageAsync(pageid, false)) != null && await _userPermissions.IsAuthorizedAsync(User, siteid, EntityNames.Page, pageid, PermissionNames.Edit))
            {
                int order = 1;
                List<Page> pages = (await _pages.GetPagesAsync(siteid)).ToList();
                foreach (Page page in pages.Where(item => item.ParentId == parentid).OrderBy(item => item.Order))
                {
                    if (page.Order != order)
                    {
                        page.Order = order;
                        await _pages.UpdatePageAsync(page);
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.Page, page.PageId, SyncEventActions.Update);
                    }
                    order += 2;
                }

                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, siteid, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Page Order Updated {SiteId} {PageId} {ParentId}", siteid, pageid, parentid);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Put Attempt {SiteId} {PageId} {ParentId}", siteid, pageid, parentid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Page page = await _pages.GetPageAsync(id);
            if (page != null && page.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, page.SiteId, EntityNames.Page, page.PageId, PermissionNames.Edit))
            {
                await _pages.DeletePageAsync(page.PageId);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Page, page.PageId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, page.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Page Deleted {PageId}", page.PageId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Page Delete Attempt {PageId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // POST api/<controller>/5/6
        [HttpPost("{fromPageId}/{toPageId}/{usePagePermissions}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Post(int fromPageId, int toPageId, bool usePagePermissions)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var fromPage = await _pages.GetPageAsync(fromPageId);
            if (fromPage != null && fromPage.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, fromPage.PermissionList))
            {
                var toPage = await _pages.GetPageAsync(toPageId);
                if (toPage != null && toPage.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, toPage.PermissionList))
                {
                    // copy page settings
                    var settings = (await _settings.GetSettingsAsync(EntityNames.Page, fromPage.PageId)).ToList();
                    foreach (var setting in settings)
                    {
                        await _settings.AddSettingAsync(new Setting
                        {
                            EntityName = setting.EntityName,
                            EntityId = toPage.PageId,
                            SettingName = setting.SettingName,
                            SettingValue = setting.SettingValue,
                            IsPrivate = setting.IsPrivate
                        });
                    }

                    // copy modules
                    List<PageModule> pageModules = (await _pageModules.GetPageModulesAsync(fromPage.SiteId)).ToList();
                    foreach (PageModule pm in pageModules.Where(item => item.PageId == fromPage.PageId && !item.Module.AllPages && !item.IsDeleted))
                    {
                        Module module;

                        // determine if module is a shared instance (ie. exists on other pages)
                        if (!pageModules.Any(item => item.ModuleId == pm.ModuleId && item.PageId != fromPage.PageId))
                        {
                            // create new module
                            module = new Module();
                            module.SiteId = fromPage.SiteId;
                            module.PageId = toPageId;
                            module.ModuleDefinitionName = pm.Module.ModuleDefinitionName;
                            module.AllPages = false;
                            if (usePagePermissions)
                            {
                                module.PermissionList = toPage.PermissionList;
                            }
                            else
                            {
                                module.PermissionList = pm.Module.PermissionList;
                            }
                            module.PermissionList = module.PermissionList.Select(item => new Permission
                            {
                                SiteId = item.SiteId,
                                EntityName = EntityNames.Module,
                                EntityId = -1,
                                PermissionName = item.PermissionName,
                                RoleName = item.RoleName,
                                UserId = item.UserId,
                                IsAuthorized = item.IsAuthorized,
                            }).ToList();

                            module = await _modules.AddModuleAsync(module);

                            // deep copy module content (includes settings)
                            string content = await _modules.ExportModuleAsync(pm.Module, "Copy Page");
                            if (content != "")
                            {
                                await _modules.ImportModuleAsync(module, content, "Copy Page");
                            }
                        }
                        else
                        {
                            // use existing module
                            module = pm.Module;
                        }

                        PageModule pageModule = new PageModule();
                        pageModule.PageId = toPageId;
                        pageModule.ModuleId = module.ModuleId;
                        pageModule.Title = pm.Title;
                        pageModule.Pane = pm.Pane;
                        pageModule.Order = pm.Order;
                        pageModule.ContainerType = pm.ContainerType;
                        pageModule.EffectiveDate = pm.EffectiveDate;
                        pageModule.ExpiryDate = pm.ExpiryDate;
                        pageModule.Header = pm.Header;
                        pageModule.Footer = pm.Footer;

                        await _pageModules.AddPageModuleAsync(pageModule);
                    }

                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, fromPage.SiteId, SyncEventActions.Refresh);
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
            else
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
