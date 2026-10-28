using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using Stomme.Shared;
using System.Linq;
using System.Net;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using System;
using System.Threading.Tasks;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class FolderController : Controller
    {
        private readonly IFolderRepository _folders;
        private readonly IUserPermissions _userPermissions;
        private readonly IFileRepository _files;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public FolderController(IFolderRepository folders, IUserPermissions userPermissions, IFileRepository files, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _folders = folders;
            _userPermissions = userPermissions;
            _files = files;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        public async Task<IEnumerable<Folder>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Folder> folders = new List<Folder>();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                var hierarchy = (await _folders.GetFoldersAsync(SiteId)).ToList();
                foreach (Folder folder in hierarchy)
                {
                    // note that Browse permission is used for this method
                    if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Browse, folder.PermissionList))
                    {
                        folders.Add(folder);
                    }
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                folders = null;
            }
            return folders;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<Folder> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Folder folder = await _folders.GetFolderAsync(id);
            if (folder != null && folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, folder.PermissionList))
            {
                return folder;
            }
            else
            {
                if (folder != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Get Attempt {FolderId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // GET api/<controller>/path/x/?path=y
        [HttpGet("path/{siteId}")]
        public async Task<Folder> GetByPath(int siteId, string path)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var folderPath = WebUtility.UrlDecode(path).Replace("\\", "/"); // handle legacy path format
            folderPath = (folderPath == "/") ? "" : folderPath;
            if (!folderPath.EndsWith("/") && folderPath != "")
            {
                folderPath += "/";
            }
            Folder folder = await _folders.GetFolderAsync(siteId, folderPath);
            if (folder == null && User.IsInRole(RoleNames.Host) && path.StartsWith("Users/"))
            {
                // create the user folder on this site for the host user
                var userId = int.Parse(path.ReplaceMultiple(new string[] { "Users", "/" }, ""));
                folder = await _folders.GetFolderAsync(siteId, "Users/");
                if (folder != null)
                {
                    folder = await _folders.AddFolderAsync(new Folder
                    {
                        SiteId = folder.SiteId,
                        ParentId = folder.FolderId,
                        Name = "My Folder",
                        Type = FolderTypes.Private,
                        Path = path,
                        Order = 1,
                        ImageSizes = "",
                        Capacity = Constants.UserFolderCapacity,
                        IsSystem = true,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.Browse, userId, true),
                            new Permission(PermissionNames.View, userId, true),
                            // BL-115: a user's My Folder is PRIVATE. It used to grant View to Everyone, and an anonymous caller
                            // matches Everyone, so every file under Users/{id}/ was downloadable by anyone who guessed the
                            // sequential file id. The owner and site administrators see it; nobody else does.
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, userId, true)
                        }
                    });
                }
            }
            if (folder != null && folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, folder.PermissionList))
            {
                return folder;
            }
            else
            {
                if (folder != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Get Attempt {Path} For Site {SiteId}", path, siteId);
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
        public async Task<Folder> Post([FromBody] Folder folder)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && folder.SiteId == alias.SiteId)
            {
                List<Permission> permissions;
                if (folder.ParentId != null)
                {
                    permissions = (await _folders.GetFolderAsync(folder.ParentId.Value)).PermissionList;
                }
                else
                {
                    permissions = new List<Permission> {
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true),
                    };
                }
                if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, permissions))
                {
                    // BL-102: a row for another entity would be written as a SITE-WIDE row (EntityId -1), so Edit on
                    // the parent folder would be enough to grant any framework policy. Refused BEFORE any write.
                    var foreign = PermissionRowScope.ForeignRows(folder.PermissionList, EntityNames.Folder);
                    if (foreign.Any())
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Permission Post Attempt {ParentId} {Permissions}",
                            folder.ParentId, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return null;
                    }

                    if (folder.IsPathValid())
                    {
                        if (string.IsNullOrEmpty(folder.Path) && folder.ParentId != null)
                        {
                            Folder parent = await _folders.GetFolderAsync(folder.ParentId.Value);
                            folder.Path = Utilities.UrlCombine(parent.Path, folder.Name);
                        }
                        if (!folder.Path.EndsWith("/"))
                        {
                            folder.Path = folder.Path + "/";
                        }
                        folder = await _folders.AddFolderAsync(folder);
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.Folder, folder.FolderId, SyncEventActions.Create);
                        _logger.Log(LogLevel.Information, this, LogFunction.Create, "Folder Added {Folder}", folder);
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Create, "Folder Name Not Valid {Folder}", folder);
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        folder = null;
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Post Attempt {Folder}", folder);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    folder = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Post Attempt {Folder}", folder);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                folder = null;
            }
            return folder;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Folder> Put(int id, [FromBody] Folder folder)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && folder.SiteId == alias.SiteId && folder.FolderId == id && (await _folders.GetFolderAsync(folder.FolderId, false)) != null && await _userPermissions.IsAuthorizedAsync(User, folder.SiteId, EntityNames.Folder, folder.FolderId, PermissionNames.Edit))
            {
                // BL-102: a row for another entity would be written as a SITE-WIDE row (EntityId -1). Refused BEFORE
                // the directory move and the folder write below, so a refusal is never a partial write.
                var foreign = PermissionRowScope.ForeignRows(folder.PermissionList, EntityNames.Folder);
                if (foreign.Any())
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Permission Put Attempt {FolderId} {Permissions}",
                        id, string.Join(",", foreign.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return null;
                }

                if (folder.IsPathValid())
                {
                    if (folder.ParentId != null)
                    {
                        Folder parent = await _folders.GetFolderAsync(folder.ParentId.Value);
                        folder.Path = Utilities.UrlCombine(parent.Path, folder.Name);
                    }
                    if (!folder.Path.EndsWith("/"))
                    {
                        folder.Path = folder.Path + "/";
                    }

                    Folder _folder = await _folders.GetFolderAsync(id, false);
                    if (_folder.Path != folder.Path && Directory.Exists(await _folders.GetFolderPathAsync(_folder)))
                    {
                        Directory.Move(await _folders.GetFolderPathAsync(_folder), await _folders.GetFolderPathAsync(folder));
                    }

                    folder = await _folders.UpdateFolderAsync(folder);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Folder, folder.FolderId, SyncEventActions.Update);
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "Folder Updated {Folder}", folder);
                }
                else
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "Folder Name Not Valid {Folder}", folder);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    folder = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Put Attempt  {Folder}", folder);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                folder = null;
            }
            return folder;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var folder = await _folders.GetFolderAsync(id, false);
            if (folder != null && folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, folder.SiteId, EntityNames.Folder, id, PermissionNames.Edit))
            {
                var folderPath = await _folders.GetFolderPathAsync(folder);
                if (Directory.Exists(folderPath))
                {
                    // remove all files from disk (including thumbnails, etc...)
                    foreach (var filePath in Directory.GetFiles(folderPath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                    Directory.Delete(folderPath);
                }

                // remove files from database
                foreach (var file in await _files.GetFilesAsync(id))
                {
                    await _files.DeleteFileAsync(file.FileId);
                }

                await _folders.DeleteFolderAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Folder, folder.FolderId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Folder Deleted {FolderId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Folder Delete Attempt {FolderId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
