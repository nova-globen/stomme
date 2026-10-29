using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using Stomme.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Security;
using System.Linq;
using System.Net;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Extensions;
using SkiaSharp;
using System.Net.Http;
using Microsoft.AspNetCore.Cors;
using System.IO.Compression;
using Stomme.Services;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

// ReSharper disable StringIndexOfIsCultureSpecific.1

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class FileController : Controller
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IFileRepository _files;
        private readonly IFolderRepository _folders;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;
        private readonly ISettingRepository _settingRepository;
        private readonly IImageService _imageService;
        private readonly IFileIngestionGuard _ingestion;

        public FileController(IWebHostEnvironment environment, IFileRepository files, IFolderRepository folders, IUserPermissions userPermissions, ISettingRepository settingRepository, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager, IImageService imageService, IFileIngestionGuard ingestion)
        {
            _ingestion = ingestion;
            _environment = environment;
            _files = files;
            _folders = folders;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
            _settingRepository = settingRepository;
            _imageService = imageService;
        }

        // GET: api/<controller>?folder=x
        [HttpGet]
        public async Task<IEnumerable<Models.File>> Get(string folder)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Models.File> files = new List<Models.File>();
            int folderid;
            if (int.TryParse(folder, out folderid))
            {
                Folder Folder = await _folders.GetFolderAsync(folderid);
                if (Folder != null && Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Browse, Folder.PermissionList))
                {
                    files = (await _files.GetFilesAsync(folderid)).ToList();
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Get Attempt {FolderId}", folder);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    files = null;
                }
            }
            else
            {
                if (User.IsInRole(RoleNames.Host))
                {
                    folder = GetFolderPath(folder);
                    if (Directory.Exists(folder))
                    {
                        foreach (string file in Directory.GetFiles(folder))
                        {
                            files.Add(new Models.File { Name = Path.GetFileName(file), Extension = Path.GetExtension(file)?.Replace(".", "") });
                        }
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Get Attempt {Folder}", folder);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    files = null;
                }
            }

            return files;
        }

        // GET: api/<controller>/siteId/folderPath
        [HttpGet("{siteId}/{path}")]
        public async Task<IEnumerable<Models.File>> Get(int siteId, string path)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Models.File> files;

            Folder folder = await _folders.GetFolderAsync(siteId, WebUtility.UrlDecode(path));
            if (folder != null && folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Browse, folder.PermissionList))
            {
                files = (await _files.GetFilesAsync(folder.FolderId)).ToList();
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Get Attempt {SiteId} {Path}", siteId, path);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                files = null;
            }

            return files;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        public async Task<Models.File> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Models.File file = await _files.GetFileAsync(id);
            if (file != null && file.Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, file.Folder.PermissionList))
            {
                return file;
            }
            else
            {
                if (file != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Get Attempt {FileId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        [HttpGet("name/{name}/{folderId}")]
        public async Task<Models.File> Get(string name, int folderId)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Models.File file = await _files.GetFileAsync(folderId, name);
            if (file != null && file.Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, file.Folder.PermissionList))
            {
                return file;
            }
            else
            {
                if (file != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Get Attempt {Name} For Folder {FolderId}", name, folderId);
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
        public async Task<Models.File> Post([FromBody] Models.File file)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var folder = await _folders.GetFolderAsync(file.FolderId);
            if (ModelState.IsValid && folder != null && folder.SiteId == alias.SiteId)
            {
                if (await _userPermissions.IsAuthorizedAsync(User, folder.SiteId, EntityNames.Folder, file.FolderId, PermissionNames.Edit))
                {
                    if (await _ingestion.IsAllowedFileNameAsync(alias, file.Name))
                    {
                        var filepath = await _files.GetFilePathAsync(file);
                        if (System.IO.File.Exists(filepath))
                        {
                            file = await CreateFile(alias, file.Name, folder.FolderId, filepath, enforceCapacity: true);
                            if (file != null)
                            {
                                file = await _files.AddFileAsync(file);
                                await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Create);
                                _logger.Log(LogLevel.Information, this, LogFunction.Create, "File Added {File}", file);
                            }
                        }
                        else
                        {
                            _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Does Not Exist At Path {FilePath}", filepath);
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            file = null;
                        }
                    }
                    else
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Name Is Invalid Or Contains Invalid Extension {File}", file.Name);
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        file = null;
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Post Attempt {File}", file);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    file = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Post Attempt {File}", file);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                file = null;
            }

            return file;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Models.File> Put(int id, [FromBody] Models.File file)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var File = await _files.GetFileAsync(file.FileId, false);
            if (ModelState.IsValid && file.Folder.SiteId == alias.SiteId && file.FileId == id && File != null // ensure file exists
                && await _userPermissions.IsAuthorizedAsync(User, file.Folder.SiteId, EntityNames.Folder, File.FolderId, PermissionNames.Edit) // ensure user had edit rights to original folder
                && await _userPermissions.IsAuthorizedAsync(User, file.Folder.SiteId, EntityNames.Folder, file.FolderId, PermissionNames.Edit)) // ensure user has edit rights to new folder
            {
                if (await _ingestion.IsAllowedFileNameAsync(alias, file.Name))
                {
                    if (File.Name != file.Name || File.FolderId != file.FolderId)
                    {
                        file.Folder = await _folders.GetFolderAsync(file.FolderId, false);
                        string folderpath = await _folders.GetFolderPathAsync(file.Folder);
                        if (!Directory.Exists(folderpath))
                        {
                            Directory.CreateDirectory(folderpath);
                        }
                        System.IO.File.Move(await _files.GetFilePathAsync(File), Path.Combine(folderpath, file.Name));
                    }

                    var newfile = await CreateFile(alias, File.Name, file.Folder.FolderId, await _files.GetFilePathAsync(file), enforceCapacity: true);
                    if (newfile != null)
                    {
                        file.Extension = newfile.Extension;
                        file.Size = newfile.Size;
                        file.ImageWidth = newfile.ImageWidth;
                        file.ImageHeight = newfile.ImageHeight;
                    }

                    file = await _files.UpdateFileAsync(file);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Update);
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "File Updated {File}", file);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Name Is Invalid Or Contains Invalid Extension {File}", file.Name);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    file = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Put Attempt {File}", file);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                file = null;
            }

            return file;
        }

        // PUT api/<controller>/unzip/5
        [HttpPut("unzip/{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IActionResult> Unzip(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var zipfile = await _files.GetFileAsync(id, false);
            if (zipfile != null && zipfile.Folder.SiteId == alias.SiteId && zipfile.Extension.ToLower() == "zip")
            {
                // BL-100 F1: every entry is staged, counted, scanned and capacity-checked by the ingestion guard
                // before ANY of them reaches the folder; a refusal leaves the folder - and the zip - as they were.
                string folderpath = await _folders.GetFolderPathAsync(zipfile.Folder);
                var result = await _ingestion.ExtractZipAsync(new FileIngestionTarget(alias, zipfile.Folder, folderpath), Path.Combine(folderpath, zipfile.Name), HttpContext.RequestAborted);
                if (result.IsRefused)
                {
                    return Refusal(result, "Unzip", zipfile.Name);
                }

                foreach (var skipped in result.Skipped)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Name Is Invalid Or Contains Invalid Extension {File}", skipped);
                }

                foreach (var name in result.Files)
                {
                    var file = await CreateFile(alias, name, zipfile.Folder.FolderId, Path.Combine(folderpath, name), enforceCapacity: false);
                    if (file != null)
                    {
                        if (file.FileId == 0)
                        {
                            file = await _files.AddFileAsync(file);
                        }
                        else
                        {
                            file = await _files.UpdateFileAsync(file);
                        }
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Create);
                        _logger.Log(LogLevel.Information, this, LogFunction.Create, "File Extracted {File}", file);
                    }
                }

                // delete zip file (unless the archive held a same-named entry, which has just replaced it)
                if (!result.Files.Contains(zipfile.Name, StringComparer.OrdinalIgnoreCase))
                {
                    await _files.DeleteFileAsync(zipfile.FileId);
                    System.IO.File.Delete(Path.Combine(folderpath, zipfile.Name));
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "Zip File Removed {File}", zipfile);
                }
                return NoContent();
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Unzip Attempt {FileId}", id);
                return StatusCode((int)HttpStatusCode.Forbidden);
            }
        }
        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Models.File file = await _files.GetFileAsync(id);
            if (file != null && file.Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, file.Folder.SiteId, EntityNames.Folder, file.Folder.FolderId, PermissionNames.Edit))
            {
                string filepath = await _files.GetFilePathAsync(file);
                if (System.IO.File.Exists(filepath))
                {
                    // remove file and thumbnails
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(filepath), Path.GetFileNameWithoutExtension(filepath) + ".*"))
                    {
                        System.IO.File.Delete(f);
                    }
                }

                await _files.DeleteFileAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "File Deleted {File}", file);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Delete Attempt {FileId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // POST api/<controller>/upload/url?url=x&folderid=y&name=z
        /// <remarks>
        /// BL-100 F1: was <c>GET api/File/upload?url=</c> - a state change on a GET, which the
        /// antiforgery filter never validates and which the Lax Identity cookie accompanies on a cross-site top-level
        /// navigation, so any page could make a signed-in editor's browser plant a download in their folder. Now a
        /// POST (antiforgery-validated), fetched through the outbound connect guard (no loopback/private/link-local
        /// destinations, no redirects), capped and scanned by the ingestion guard. The client's
        /// <c>FileService.UploadFileAsync</c> changed with it.
        /// </remarks>
        [HttpPost("upload/url")]
        public async Task<IActionResult> UploadFileFromUrl([FromQuery] string url, [FromQuery] string folderid, [FromQuery] string name)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Folder folder = null;
            int FolderId;
            if (int.TryParse(folderid, out FolderId))
            {
                folder = await _folders.GetFolderAsync(FolderId);
            }

            if (folder != null && folder.SiteId == alias.SiteId && !string.IsNullOrEmpty(url) && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, folder.PermissionList))
            {
                if (string.IsNullOrEmpty(name))
                {
                    name = url.Substring(url.LastIndexOf("/", StringComparison.Ordinal) + 1);
                }

                string folderPath = await _folders.GetFolderPathAsync(folder);
                var result = await _ingestion.DownloadAsync(new FileIngestionTarget(alias, folder, folderPath), url, name, HttpContext.RequestAborted);
                if (result.IsRefused)
                {
                    return Refusal(result, "Download " + url, name);
                }

                var file = await CreateFile(alias, name, folder.FolderId, Path.Combine(folderPath, name), enforceCapacity: false);
                if (file != null)
                {
                    file = file.FileId == 0 ? await _files.AddFileAsync(file) : await _files.UpdateFileAsync(file);
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "File Downloaded {File}", file);
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Create);
                }
                return Ok(file);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Download Attempt {FolderId} {Url}", folderid, url);
                return StatusCode((int)HttpStatusCode.Forbidden);
            }
        }

        // POST api/<controller>/upload
        [EnableCors(Constants.MauiCorsPolicy)]
        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile([FromForm] string folder, IFormFile formfile)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (string.IsNullOrEmpty(folder))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Upload Does Not Contain A Folder");
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            if (formfile == null || formfile.Length <= 0)
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Upload Does Not Contain A File");
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            // ensure filename is valid
            string fileName = formfile.FileName;
            if (Path.GetExtension(fileName).Contains(':'))
            {
                fileName = fileName.Substring(0, fileName.LastIndexOf(':')); // remove invalid suffix from extension
            }

            // ensure headers exist
            if (!Request.Headers.TryGetValue("PartCount", out StringValues partcount) || !int.TryParse(partcount, out int partCount) || partCount <= 0 ||
                !Request.Headers.TryGetValue("TotalParts", out StringValues totalparts) || !int.TryParse(totalparts, out int totalParts) || totalParts <= 0)
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Upload Is Missing Required Headers");
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            try
            {
                string folderPath = "";
                Folder Folder = null;
                int FolderId;
                if (int.TryParse(folder, out FolderId))
                {
                    Folder = await _folders.GetFolderAsync(FolderId);
                    if (Folder != null && Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, Folder.PermissionList))
                    {
                        folderPath = await _folders.GetFolderPathAsync(Folder);
                    }
                }
                else
                {
                    FolderId = -1;
                    if (User.IsInRole(RoleNames.Host))
                    {
                        folderPath = GetFolderPath(folder);
                    }
                }

                if (string.IsNullOrEmpty(folderPath))
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Upload Attempt {Folder} {File}", folder, formfile.FileName);
                    return StatusCode((int)HttpStatusCode.Forbidden);
                }

                // BL-100 F1: parts are staged outside the folder, the set's size is capped from its FIRST part, and
                // the merged file is scanned and capacity-checked before it replaces anything in the folder.
                FileIngestionResult result;
                await using (var content = formfile.OpenReadStream())
                {
                    result = await _ingestion.AcceptChunkAsync(new FileIngestionTarget(alias, FolderId != -1 ? Folder : null, folderPath), fileName, partCount, totalParts, content, formfile.Length, HttpContext.RequestAborted);
                }

                if (result.IsRefused)
                {
                    return Refusal(result, "Upload " + folder, fileName);
                }

                if (result.IsAccepted && FolderId != -1)
                {
                    foreach (var upload in result.Files)
                    {
                        var file = await CreateFile(alias, upload, FolderId, Path.Combine(folderPath, upload), enforceCapacity: false);
                        if (file != null)
                        {
                            if (file.FileId == 0)
                            {
                                file = await _files.AddFileAsync(file);
                            }
                            else
                            {
                                file = await _files.UpdateFileAsync(file);
                            }
                            _logger.Log(LogLevel.Information, this, LogFunction.Create, "File Uploaded {File}", Path.Combine(folderPath, upload));
                            await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, SyncEventActions.Create);
                        }
                    }
                }
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Create, ex, "File Upload Attempt Failed {Folder} {File}", folder, formfile.FileName);
                return StatusCode((int)HttpStatusCode.InternalServerError);
            }
        }

        /// <summary>
        /// A guard refusal: a Security log naming the reason, and a distinguishable answer - 403 for a name/extension
        /// refusal (the historical status of that check), 400 with <c>{ reason, message }</c> for every other.
        /// </summary>
        private IActionResult Refusal(FileIngestionResult result, string operation, string subject)
        {
            _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Ingestion Refused {Operation} {Subject} {Reason} {Message}", operation, subject, result.Reason, result.Message);
            var status = result.Reason is FileIngestionRefusalReason.Extension or FileIngestionRefusalReason.InvalidName
                ? HttpStatusCode.Forbidden
                : HttpStatusCode.BadRequest;
            return StatusCode((int)status, new { reason = result.Reason.ToString(), message = result.Message });
        }
        /// <summary>
        /// Get file with header
        /// Content-Disposition: inline
        /// https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Disposition
        /// </summary>
        /// <param name="id">File Id from Stomme filesystem </param>
        /// <returns>file content</returns>

        // GET api/<controller>/download/5
        [HttpGet("download/{id}")]
        public async Task<IActionResult> DownloadInline(int id)
        {
            return await Download(id, false);
        }
        /// <summary>
        /// Get file with header
        /// Content-Disposition: attachment; filename="filename.jpg"
        /// https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Disposition
        ///
        /// </summary>
        /// <param name="id">File Id from Stomme filesystem</param>
        /// <returns></returns>

        // GET api/<controller>/download/5/attach
        [HttpGet("download/{id}/attach")]
        public async Task<IActionResult> DownloadAttachment(int id)
        {
            return await Download(id, true);
        }

        /// <summary>
        /// The answer for a request this controller has REFUSED: the status the caller already set, with the broken-image
        /// placeholder as its body when the app ships one.
        /// </summary>
        /// <remarks>
        /// This used to return <c>null</c> when <c>wwwroot/images/error.png</c> was absent, and MVC reports a null
        /// action result as HTTP 500 - so a missing static asset turned a correct 403 or 404 into a server error, and an
        /// authorisation refusal into noise in error monitoring. The refusal never depended on the picture; now neither
        /// does its status.
        /// </remarks>
        private IActionResult Refused()
        {
            string errorPath = Path.Combine(GetFolderPath("wwwroot/images"), "error.png");
            return System.IO.File.Exists(errorPath)
                ? PhysicalFile(errorPath, MimeUtilities.GetMimeType(errorPath))
                : new StatusCodeResult(HttpContext.Response.StatusCode);
        }

        private async Task<IActionResult> Download(int id, bool asAttachment)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var file = await _files.GetFileAsync(id);
            if (file != null && file.Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, file.Folder.PermissionList))
            {
                var filepath = await _files.GetFilePathAsync(file);
                if (System.IO.File.Exists(filepath))
                {
                    if (asAttachment)
                    {
                        await _syncManager.AddSyncEventAsync(alias, EntityNames.File, file.FileId, "Download");
                        return PhysicalFile(filepath, file.GetMimeType(), file.Name);
                    }
                    else
                    {
                        return PhysicalFile(filepath, file.GetMimeType());
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Read, "File Does Not Exist {FileId} {FilePath}", id, filepath);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Access Attempt {FileId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }

            return Refused();
        }

        [HttpGet("image/{id}/{width}/{height}/{mode}/{position}/{background}/{rotate}/{recreate}")]
        public async Task<IActionResult> GetImage(int id, int width, int height, string mode, string position, string background, string rotate, string recreate)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var file = await _files.GetFileAsync(id);

            var _ImageFiles = (await _settingRepository.GetSettingAsync(EntityNames.Site, alias.SiteId, "ImageFiles"))?.SettingValue;
            _ImageFiles = (string.IsNullOrEmpty(_ImageFiles)) ? Constants.ImageFiles : _ImageFiles;

            if (file != null && file.Folder.SiteId == alias.SiteId && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.View, file.Folder.PermissionList))
            {
                if (_ImageFiles.Split(',').Contains(file.Extension.ToLower()))
                {
                    var filepath = await _files.GetFilePathAsync(file);
                    if (System.IO.File.Exists(filepath))
                    {
                        if (!bool.TryParse(recreate, out _)) recreate = "false";

                        string format = "png";

                        string imagepath = filepath.Replace(Path.GetExtension(filepath), "." + width.ToString() + "x" + height.ToString() + "." + format);
                        if (!System.IO.File.Exists(imagepath) || bool.Parse(recreate))
                        {
                            // user has edit access to folder or folder supports the image size being created
                            if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Edit, file.Folder.PermissionList) ||
                              (!string.IsNullOrEmpty(file.Folder.ImageSizes) && (file.Folder.ImageSizes == "*" || file.Folder.ImageSizes.ToLower().Split(",").Contains(width.ToString() + "x" + height.ToString()))))
                            {
                                imagepath = _imageService.CreateImage(filepath, width, height, mode, position, background, rotate, format, imagepath);
                            }
                            else
                            {
                                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Invalid Image Size For Folder {Folder} {Width} {Height}", file.Folder, width, height);
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                            }
                        }
                        if (!string.IsNullOrEmpty(imagepath))
                        {
                            if (!string.IsNullOrEmpty(file.Folder.CacheControl))
                            {
                                HttpContext.Response.Headers.Append(HeaderNames.CacheControl, value: file.Folder.CacheControl);
                            }
                            return PhysicalFile(imagepath, file.GetMimeType());
                        }
                        else
                        {
                            _logger.Log(LogLevel.Error, this, LogFunction.Create, "Error Displaying Image For File {File} {Width} {Height}", file, width, height);
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        }
                    }
                    else
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Read, "File Does Not Exist {FileId} {FilePath}", id, filepath);
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "File Is Not An Image {File}", file);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
            else
            {
                if (file != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized File Access Attempt {FileId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }

            return Refused();
        }

        private string GetFolderPath(string folder)
        {
            return Utilities.PathCombine(_environment.ContentRootPath, folder);
        }

        private void CreateDirectory(string folderpath)
        {
            if (!Directory.Exists(folderpath))
            {
                string path = folderpath.StartsWith(Path.DirectorySeparatorChar) ? Path.DirectorySeparatorChar.ToString() : string.Empty;
                var separators = new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
                string[] folders = folderpath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                foreach (string folder in folders)
                {
                    path = Utilities.PathCombine(path, folder, Path.DirectorySeparatorChar.ToString());
                    if (!Directory.Exists(path))
                    {
                        try
                        {
                            Directory.CreateDirectory(path);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log(LogLevel.Error, this, LogFunction.Create, ex, "Unable To Create Folder {Folder}", path);
                        }
                    }
                }
            }
        }

        /// <param name="enforceCapacity">
        /// True for the paths that register a file ALREADY in the folder (<see cref="Post"/>, <see cref="Put"/>), which
        /// keep their historical refusal (the file and its row are removed). False for the ingestion paths: the
        /// <see cref="IFileIngestionGuard"/> checked capacity before the move, so nothing here may delete anything.
        /// </param>
        private async Task<Models.File> CreateFile(Alias alias, string filename, int folderid, string filepath, bool enforceCapacity)
        {
            var file = await _files.GetFileAsync(folderid, filename);

            var _ImageFiles = (await _settingRepository.GetSettingAsync(EntityNames.Site, alias.SiteId, "ImageFiles"))?.SettingValue;
            _ImageFiles = (string.IsNullOrEmpty(_ImageFiles)) ? Constants.ImageFiles : _ImageFiles;

            var folder = await _folders.GetFolderAsync(folderid, false);
            FileInfo fileinfo = new FileInfo(filepath);
            if (fileinfo.Length >= int.MaxValue)
            {
                // File.Size is an int; the ingestion caps keep every guarded file below this, so only a file placed on disk by other means gets here
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "File Is Too Large To Register {Folder} {File} {Length}", folder, filepath, fileinfo.Length);
                return null;
            }

            if (!enforceCapacity || !await _ingestion.ExceedsCapacityAsync(folder, fileinfo.Length, Array.Empty<string>()))
            {
                if (file == null)
                {
                    file = new Models.File();
                }
                file.Name = filename;
                file.FolderId = folderid;

                file.Extension = fileinfo.Extension.ToLower().Replace(".", "");
                file.Size = (int)fileinfo.Length;
                file.ImageHeight = 0;
                file.ImageWidth = 0;

                if (_ImageFiles.Split(',').Contains(file.Extension.ToLower()))
                {
                    try
                    {
                        using FileStream stream = new(
                            filepath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read);

                        using SKCodec codec = SKCodec.Create(stream);

                        if (codec is not null)
                        {
                            file.ImageHeight = codec.Info.Height;
                            file.ImageWidth = codec.Info.Width;
                        }
                    }
                    catch
                    {
                        // Error opening or reading image file.
                    }
                }
            }
            else
            {
                System.IO.File.Delete(filepath);
                if (file != null)
                {
                    await _files.DeleteFileAsync(file.FileId);
                }
                file = null;
                _logger.Log(LogLevel.Warning, this, LogFunction.Create, "File Exceeds Folder Capacity And Has Been Removed {Folder} {File}", folder, filepath);
            }

            return file;
        }

    }
}
