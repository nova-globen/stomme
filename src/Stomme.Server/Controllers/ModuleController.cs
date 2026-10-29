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
using System.IO;
using System.Threading.Tasks;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class ModuleController : Controller
    {
        private readonly IModuleRepository _modules;
        private readonly IPageModuleRepository _pageModules;
        private readonly IPageRepository _pages;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly ISettingRepository _settings;
        private readonly IFolderRepository _folders;
        private readonly IFileRepository _files;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public ModuleController(IModuleRepository modules, IPageModuleRepository pageModules, IPageRepository pages, IModuleDefinitionRepository moduleDefinitions, ISettingRepository settings, IFolderRepository folders, IFileRepository files, IUserPermissions userPermissions, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger)
        {
            _modules = modules; 
            _pageModules = pageModules;
            _pages = pages;
            _moduleDefinitions = moduleDefinitions;
            _settings = settings;
            _folders = folders;
            _files = files;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        public async Task<IEnumerable<Module>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Module> modules = new List<Module>();

            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                List<ModuleDefinition> moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(SiteId)).ToList();
                List<Setting> settings = (await _settings.GetSettingsAsync(EntityNames.Module)).ToList();

                foreach (PageModule pagemodule in await _pageModules.GetPageModulesAsync(SiteId))
                {
                    if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, pagemodule.Module.PermissionList))
                    {
                        Module module = new Module();
                        module.SiteId = pagemodule.Module.SiteId;
                        module.ModuleDefinitionName = pagemodule.Module.ModuleDefinitionName;
                        module.AllPages = pagemodule.Module.AllPages;
                        module.PermissionList = pagemodule.Module.PermissionList;
                        module.CreatedBy = pagemodule.Module.CreatedBy;
                        module.CreatedOn = pagemodule.Module.CreatedOn;
                        module.ModifiedBy = pagemodule.Module.ModifiedBy;
                        module.ModifiedOn = pagemodule.Module.ModifiedOn;
                        module.DeletedBy = pagemodule.DeletedBy;
                        module.DeletedOn = pagemodule.DeletedOn;
                        module.IsDeleted = pagemodule.IsDeleted;

                        module.PageModuleId = pagemodule.PageModuleId;
                        module.ModuleId = pagemodule.ModuleId;
                        module.PageId = pagemodule.PageId;
                        module.Title = pagemodule.Title;
                        module.Pane = pagemodule.Pane;
                        module.Order = pagemodule.Order;
                        module.ContainerType = pagemodule.ContainerType;
                        module.EffectiveDate = pagemodule.EffectiveDate;
                        module.ExpiryDate = pagemodule.ExpiryDate;
                        module.Header = pagemodule.Header;
                        module.Footer = pagemodule.Footer;

                        module.ModuleDefinition = _moduleDefinitions.FilterModuleDefinition(moduledefinitions.Find(item => item.ModuleDefinitionName == module.ModuleDefinitionName));

                        var canEditModule = await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, pagemodule.Module.PermissionList);
                        module.Settings = settings.Where(item => item.EntityId == pagemodule.ModuleId)
                            .Where(item => !item.IsPrivate || canEditModule)
                            .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);

                        modules.Add(module);
                    }
                    modules = modules.OrderBy(item => item.PageId).ThenBy(item => item.Pane).ThenBy(item => item.Order).ToList();
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                modules = null;
            }

            return modules;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<Module> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Module module = await _modules.GetModuleAsync(id);
            if (module != null && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User,PermissionNames.View, module.PermissionList))
            {
                List<ModuleDefinition> moduledefinitions = (await _moduleDefinitions.GetModuleDefinitionsAsync(module.SiteId)).ToList();
                module.ModuleDefinition = _moduleDefinitions.FilterModuleDefinition(moduledefinitions.Find(item => item.ModuleDefinitionName == module.ModuleDefinitionName));
                var canEditModule = await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, module.PermissionList);
                module.Settings = (await _settings.GetSettingsAsync(EntityNames.Module, id))
                    .Where(item => !item.IsPrivate || canEditModule)
                    .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                return module;
            }
            else
            {
                if (module != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Get Attempt {ModuleId}", id);
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
        public async Task<Module> Post([FromBody] Module module)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Page, module.PageId, PermissionNames.Edit))
            {
                // A row for another entity would be written as a SITE-WIDE row (EntityId -1), so Edit on a
                // page would be enough to grant any framework policy. Refused BEFORE the module is written.
                var foreign = PermissionRowScope.ForeignRows(module.PermissionList, EntityNames.Module);
                if (foreign.Any())
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Permission Post Attempt {PageId} {Permissions}",
                        module.PageId, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return null;
                }

                module = await _modules.AddModuleAsync(module);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Module, module.ModuleId, SyncEventActions.Create);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Module Added {Module}", module);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Post Attempt {Module}", module);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                module = null;
            }
            return module;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Module> Put(int id, [FromBody] Module module)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var _module = await _modules.GetModuleAsync(module.ModuleId, false);

            if (ModelState.IsValid && module.SiteId == alias.SiteId && module.ModuleId == id && _module != null && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Module, module.ModuleId, PermissionNames.Edit))
            {
                // A row for another entity would be written as a SITE-WIDE row (EntityId -1) - driven, Edit on
                // this one module wrote (User,-1,Write) for All Users and DELETE api/User/{id} then answered 200
                // anonymously. Refused BEFORE the module is written, so a refusal is never a partial write.
                var foreign = PermissionRowScope.ForeignRows(module.PermissionList, EntityNames.Module);
                if (foreign.Any())
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Permission Put Attempt {ModuleId} {Permissions}",
                        id, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return null;
                }

                module = await _modules.UpdateModuleAsync(module);

                if (_module.AllPages != module.AllPages)
                {
                    var pageModules = (await _pageModules.GetPageModulesAsync(module.SiteId)).ToList();
                    if (module.AllPages)
                    {
                        var pageModule = await _pageModules.GetPageModuleAsync(module.PageModuleId);
                        var pages = (await _pages.GetPagesAsync(module.SiteId)).ToList();
                        foreach (Page page in pages)
                        {
                            if (!pageModules.Exists(item => item.ModuleId == module.ModuleId && item.PageId == page.PageId) && !page.Path.StartsWith("admin/"))
                            {
                                await _pageModules.AddPageModuleAsync(new PageModule { PageId = page.PageId, ModuleId = pageModule.ModuleId, Title = pageModule.Title, Pane = pageModule.Pane, Order = pageModule.Order, ContainerType = pageModule.ContainerType, EffectiveDate = pageModule.EffectiveDate, ExpiryDate = pageModule.ExpiryDate });
                            }
                        }
                    }
                    else
                    {
                        foreach (var pageModule in pageModules)
                        {
                            if (pageModule.ModuleId == module.ModuleId && pageModule.PageModuleId != module.PageModuleId)
                            {
                                await _pageModules.DeletePageModuleAsync(pageModule.PageModuleId);
                            }
                        }
                    }
                }

                await _syncManager.AddSyncEventAsync(alias, EntityNames.Module, module.ModuleId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Module Updated {Module}", module);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Put Attempt {Module}", module);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                module = null;
            }
            return module;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var module = await _modules.GetModuleAsync(id);
            if (module != null && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Module, module.ModuleId, PermissionNames.Edit))
            {
                await _modules.DeleteModuleAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Module, module.ModuleId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Module Deleted {ModuleId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Delete Attempt {ModuleId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET api/<controller>/export?moduleid=x&pageid=y
        [HttpGet("export")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<string> Export(int moduleid, int pageid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            string content = "";
            var module = await _modules.GetModuleAsync(moduleid);
            if (module != null && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Page, pageid, PermissionNames.Edit))
            {
                content = await _modules.ExportModuleAsync(moduleid);
                if (!string.IsNullOrEmpty(content))
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Read, "Module Content Exported {ModuleId}", moduleid);
                }
                else
                {
                    _logger.Log(LogLevel.Warning, this, LogFunction.Read, "No Module Content Exported {ModuleId}", moduleid);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Export Attempt {ModuleId}", moduleid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
            return content;
        }

        // POST api/<controller>/export?moduleid=x&pageid=y&folderid=z&filename=a
        [HttpPost("export")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<int> Export(int moduleid, int pageid, int folderid, string filename)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var fileid = -1;
            var module = await _modules.GetModuleAsync(moduleid);
            if (module != null && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Page, pageid, PermissionNames.Edit) &&
                await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Folder, folderid, PermissionNames.Edit) && !string.IsNullOrEmpty(filename))
            {
                // get content
                var content = await _modules.ExportModuleAsync(module, "Export Module");

                // get folder
                var folder = await _folders.GetFolderAsync(folderid, false);
                string folderPath = await _folders.GetFolderPathAsync(folder);
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                // create json file
                filename = Utilities.GetFriendlyUrl(Path.GetFileNameWithoutExtension(filename)) + ".json";
                string filepath = Path.Combine(folderPath, filename);
                if (System.IO.File.Exists(filepath))
                {
                    System.IO.File.Delete(filepath);
                }
                System.IO.File.WriteAllText(filepath, content);

                // register file
                var file = await _files.GetFileAsync(folderid, filename);
                if (file == null)
                {
                    file = new Models.File { FolderId = folderid, Name = filename, Extension = "json", Size = (int)new FileInfo(filepath).Length, ImageWidth = 0, ImageHeight = 0 };
                    await _files.AddFileAsync(file);
                }
                else
                {
                    file.Size = (int)new FileInfo(filepath).Length;
                    await _files.UpdateFileAsync(file);
                }
                fileid = file.FileId;

                _logger.Log(LogLevel.Information, this, LogFunction.Read, "Content Exported For Module {ModuleId} To Folder {FolderId}", moduleid, folderid);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Export Attempt For Module {Module} To Folder {FolderId}", moduleid, folderid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }

            return fileid;
        }

        // POST api/<controller>/import?moduleid=x&pageid=y
        [HttpPost("import")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<bool> Import(int moduleid, int pageid, [FromBody] string content)
        {
            var alias = await _tenantManager.GetAliasAsync();

            bool success = false;
            var module = await _modules.GetModuleAsync(moduleid);
            if (ModelState.IsValid && module != null && module.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, module.SiteId, EntityNames.Page, pageid, PermissionNames.Edit))
            {
                success = await _modules.ImportModuleAsync(module, content, "Import Module");
                if (success)
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "Module Content Imported {ModuleId}", moduleid);
                }
                else
                {
                    _logger.Log(LogLevel.Warning, this, LogFunction.Update, "Module Content Import Failed {ModuleId}", moduleid);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Module Import Attempt {ModuleId}", moduleid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
            return success;
        }
    }
}
