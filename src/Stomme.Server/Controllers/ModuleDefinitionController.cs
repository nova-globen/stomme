using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class ModuleDefinitionController : Controller
    {
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly ITenantRepository _tenants;
        private readonly ISqlRepository _sql;
        private readonly IUserPermissions _userPermissions;
        private readonly IPermissionRepository _permissions;
        private readonly IInstallationManager _installationManager;
        private readonly IWebHostEnvironment _environment;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITenantManager _tenantManager;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;

        public ModuleDefinitionController(IModuleDefinitionRepository moduleDefinitions, ITenantRepository tenants, ISqlRepository sql, IUserPermissions userPermissions, IPermissionRepository permissions, IInstallationManager installationManager, IWebHostEnvironment environment, IServiceProvider serviceProvider, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger)
        {
            _moduleDefinitions = moduleDefinitions;
            _tenants = tenants;
            _sql = sql;
            _userPermissions = userPermissions;
            _permissions = permissions;
            _installationManager = installationManager;
            _environment = environment;
            _serviceProvider = serviceProvider;
            _tenantManager = tenantManager;
            _syncManager = syncManager;
            _logger = logger;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        public async Task<IEnumerable<ModuleDefinition>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                List<ModuleDefinition> moduledefinitions = new List<ModuleDefinition>();
                foreach (ModuleDefinition moduledefinition in await _moduleDefinitions.GetModuleDefinitionsAsync(SiteId))
                {
                    if (await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Utilize, OwnPermissions(moduledefinition)))
                    {
                        if (string.IsNullOrEmpty(moduledefinition.Version)) moduledefinition.Version = new Version(1, 0, 0).ToString();
                        moduledefinitions.Add(moduledefinition);
                    }
                }
                return moduledefinitions;
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5?siteid=x
        [HttpGet("{id}")]
        public async Task<ModuleDefinition> Get(int id, string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                ModuleDefinition moduledefinition = await _moduleDefinitions.GetModuleDefinitionAsync(id, SiteId);
                if (moduledefinition != null && await _userPermissions.IsAuthorizedAsync(User, PermissionNames.Utilize, OwnPermissions(moduledefinition)))
                {
                    moduledefinition.Version = (string.IsNullOrEmpty(moduledefinition.Version)) ? new Version(1, 0, 0).ToString() : moduledefinition.Version;
                    return moduledefinition;
                }
                else
                {
                    if (moduledefinition != null)
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Get Attempt {ModuleDefinitionId} {SiteId}", id, siteid);
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    }
                    else
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                    return null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Get Attempt {ModuleDefinitionId} {SiteId}", id, siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        /// <summary>
        /// The definition's OWN permission rows - the ones the Utilize question is about.
        /// </summary>
        /// <remarks>
        /// <see cref="UserSecurity.IsAuthorized(Models.User, string, List{Permission})"/> filters on
        /// <c>PermissionName</c> ALONE, and since D99 the read path MERGES the declared keys' foreign rows into
        /// this list - so an operation half that collided with <c>Utilize</c> would help decide who may see the
        /// definition. Today only casing keeps them apart (a key is lower-cased, the framework's names are
        /// TitleCase), which is an accident, not a check. The list is filtered to the definition's own entity
        /// here so the question and the rows are about the same thing.
        /// </remarks>
        private static List<Permission> OwnPermissions(ModuleDefinition moduledefinition)
        {
            return (moduledefinition.PermissionList ?? new List<Permission>())
                .Where(permission => permission.EntityName == EntityNames.ModuleDefinition)
                .ToList();
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public ModuleDefinition Post([FromBody] ModuleDefinition moduleDefinition)
        {
            if (ModelState.IsValid)
            {
                string rootPath;
                DirectoryInfo rootFolder = Directory.GetParent(_environment.ContentRootPath);
                string templatePath = Utilities.PathCombine(_environment.WebRootPath, "Modules", "Templates", moduleDefinition.Template, Path.DirectorySeparatorChar.ToString());

                if (!string.IsNullOrEmpty(moduleDefinition.ModuleDefinitionName))
                {
                    moduleDefinition.ModuleDefinitionName = moduleDefinition.ModuleDefinitionName.Replace("[Owner]", moduleDefinition.Owner).Replace("[Module]", moduleDefinition.Name);
                }
                else
                {
                    moduleDefinition.ModuleDefinitionName = moduleDefinition.Owner + ".Module." + moduleDefinition.Name;
                }

                if (moduleDefinition.Template.ToLower().Contains("internal"))
                {
                    rootPath = Utilities.PathCombine(rootFolder.FullName, Path.DirectorySeparatorChar.ToString());
                    moduleDefinition.ServerManagerType = moduleDefinition.ModuleDefinitionName + ".Manager." + moduleDefinition.Name + "Manager, " + moduleDefinition.Owner + ".Server.Stomme";
                    moduleDefinition.ModuleDefinitionName = moduleDefinition.ModuleDefinitionName + ", " + moduleDefinition.Owner + ".Client.Stomme";
                }
                else
                {
                    rootPath = Utilities.PathCombine(rootFolder.Parent.FullName, moduleDefinition.Owner + ".Module." + moduleDefinition.Name, Path.DirectorySeparatorChar.ToString());
                    moduleDefinition.ServerManagerType = moduleDefinition.ModuleDefinitionName + ".Manager." + moduleDefinition.Name + "Manager, " + moduleDefinition.ModuleDefinitionName + ".Server.Stomme";
                    moduleDefinition.ModuleDefinitionName = moduleDefinition.ModuleDefinitionName + ", " + moduleDefinition.ModuleDefinitionName + ".Client.Stomme";
                }

                ProcessTemplatesRecursively(new DirectoryInfo(templatePath), rootPath, rootFolder.Name, templatePath, moduleDefinition);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Module Definition Created {ModuleDefinition}", moduleDefinition);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Post Attempt {ModuleDefinition}", moduleDefinition);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                moduleDefinition = null;
            }

            return moduleDefinition;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task Put(int id, [FromBody] ModuleDefinition moduleDefinition)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var current = (ModelState.IsValid && moduleDefinition.SiteId == alias.SiteId && moduleDefinition.ModuleDefinitionId == id)
                ? await _moduleDefinitions.GetModuleDefinitionAsync(moduleDefinition.ModuleDefinitionId, moduleDefinition.SiteId)
                : null;
            if (current != null)
            {
                // D99 point 4 / F10. Everything below the ModuleDefinition entity is a SITE-WIDE row:
                // UpdatePermissionsAsync stamps any row whose EntityName differs from ModuleDefinition with
                // EntityId -1, so an unvalidated PUT let an ADMINISTRATOR grant themselves an arbitrary
                // framework policy (User:Write and friends) through a module's own editor - driven, it
                // returned 200 and wrote the row.
                //
                // A submitted foreign row must be a LIFTED KEY this definition declares. "Declared" alone is
                // NOT enough, and the first version of this check made exactly that mistake: the stock Users
                // definition DECLARES User:Write:Administrators, so a site administrator could still write a
                // site-wide User:Write row - to "All Users", which made the Host-only DELETE api/User/{id}
                // reachable anonymously (driven, 200). The framework's own policies are deliberately
                // ROW-LESS - UserPermissions falls back to role membership exactly while no row exists - and
                // they are not editable through a module's editor, for a host or anyone else.
                var declaredKeys = PermissionKeys.ParseKeys(current.PermissionNames);
                var rejected = (moduleDefinition.PermissionList ?? new List<Permission>())
                    .Where(permission => !string.IsNullOrEmpty(permission.EntityName)
                        && permission.EntityName != EntityNames.ModuleDefinition
                        && !declaredKeys.Any(key => key.Resource == permission.EntityName && key.Operation == permission.PermissionName))
                    .ToList();
                if (rejected.Any())
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Permission Put Attempt {ModuleDefinitionId} {Permissions}",
                        id, string.Join(",", rejected.Select(permission => $"{permission.EntityName}:{permission.PermissionName}")));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                // The server-side half of the grid's lockout rule. PermissionGrid DISABLES the Administrators
                // column for a non-host, so a submission that drops a declared Administrators grant did not
                // come from the grid - and UpdatePermissionsAsync would DELETE the row (measured: it did,
                // leaving the key granted to a custom role only). Put it back rather than lock the site's
                // administrators out of a capability they cannot then restore.
                //
                // Only for a key the submission MENTIONS. UpdatePermissionsAsync can only delete within the
                // (EntityName, PermissionName) pairs the submission carries, so a pair it does not mention is
                // not at risk - and restoring there would CREATE a row from nothing, which is how the first
                // version of this guard materialised framework policies on a plain save.
                if (moduleDefinition.PermissionList != null && !User.IsInRole(RoleNames.Host))
                {
                    foreach (var declaration in declaredKeys)
                    {
                        if (!declaration.Roles.Contains(RoleNames.Admin))
                        {
                            continue;
                        }

                        if (!moduleDefinition.PermissionList.Any(permission => permission.EntityName == declaration.Resource
                            && permission.PermissionName == declaration.Operation))
                        {
                            continue; // the pair is not in the submission's delete scope
                        }

                        if (!moduleDefinition.PermissionList.Any(permission => permission.EntityName == declaration.Resource
                            && permission.PermissionName == declaration.Operation && permission.RoleName == RoleNames.Admin))
                        {
                            // ABSENT, not "not authorized". A row that is present is never deleted by
                            // UpdatePermissionsAsync, so it was not dropped - and a present DENY row is a
                            // deliberate revocation (see RevokeClearedKeysAsync), which this guard must not
                            // silently lift the next time someone grants the key to another role.
                            moduleDefinition.PermissionList.Add(new Permission(alias.SiteId, declaration.Resource, PermissionKeys.KeyEntityId, declaration.Operation, RoleNames.Admin, null, true));
                            _logger.Log(LogLevel.Warning, this, LogFunction.Security, "Restored Administrators Grant On {Permission} For ModuleDefinition {ModuleDefinitionId}",
                                declaration.Entry, id);
                        }
                    }
                }

                await _moduleDefinitions.UpdateModuleDefinitionAsync(moduleDefinition);
                await RevokeClearedKeysAsync(alias.SiteId, declaredKeys, moduleDefinition.PermissionList, id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.ModuleDefinition, moduleDefinition.ModuleDefinitionId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh); // fingerprint changed
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Module Definition Updated {ModuleDefinition}", moduleDefinition);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Put Attempt {ModuleDefinition}", moduleDefinition);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        /// <summary>
        /// Revokes every declared KEY the submission cleared completely - the operator action "untick every
        /// cell of this capability", which the framework's own write path cannot express.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the PUT has to do this at all.</b> <c>UpdatePermissionsAsync</c> loads - and therefore
        /// deletes within - only the <c>(EntityName, PermissionName)</c> pairs the submission MENTIONS. A
        /// submission that clears every cell of a key mentions no row for that pair at all, so the delete pass
        /// never sees the surviving grants: the last grant persisted silently, and with <c>modified</c> false
        /// the Permissions cache was not even cleared. DRIVEN: the key still answered 200 after the save.
        /// </para>
        /// <para>
        /// <b>And why a DENY row rather than no rows.</b> Zero rows is not "revoked" - it is the state a key is
        /// in before it is ever seeded, and <c>ReconcileDeclaredKeyPermissionsAsync</c> re-seeds exactly that
        /// state. DRIVEN: deleting the rows gave a correct 403, and the very next module-definitions read
        /// (38 definitions, one cache miss) put the Administrators grant straight back and the key answered
        /// 200 again. An explicit DENY on <see cref="RoleNames.Admin"/> says "revoked" in the permission
        /// model's own vocabulary: <c>UserSecurity</c> is deny-FIRST, so nobody holds the key; the key now HAS
        /// rows, so the additive reconciliation leaves this site alone; and the row is a normal grid cell, so
        /// an operator can lift the revocation by ticking it (which flips this row rather than adding one).
        /// The deny names Administrators and NOT <see cref="RoleNames.Everyone"/> deliberately - an Everyone
        /// deny outranks every later grant, so the capability could never be granted to anyone again.
        /// </para>
        /// <para>
        /// One <c>UpdatePermissionsAsync</c> call does the whole thing: the submission it gets names the pair,
        /// so its delete pass removes the surviving grants, its add/update pass writes (or flips) the deny row,
        /// and it resolves the RoleId - which a bare <c>AddPermissionAsync</c> would leave null, and a row with
        /// neither RoleId nor UserId is DROPPED on read, i.e. invisible and therefore re-seeded next boot.
        /// </para>
        /// </remarks>
        private async Task RevokeClearedKeysAsync(int siteId, IReadOnlyList<PermissionKeyDeclaration> declaredKeys, List<Permission> submitted, int id)
        {
            foreach (var declaration in declaredKeys)
            {
                if (submitted != null && submitted.Any(permission => permission.EntityName == declaration.Resource
                    && permission.PermissionName == declaration.Operation))
                {
                    continue;
                }

                var rows = (await _permissions.GetPermissionsAsync(siteId, declaration.Resource, declaration.Operation))
                    .Where(permission => permission.EntityId == PermissionKeys.KeyEntityId)
                    .ToList();
                if (rows.Count == 1 && rows[0].RoleName == RoleNames.Admin && !rows[0].IsAuthorized)
                {
                    continue; // already revoked - saving again must not churn the row
                }

                var tombstone = new List<Permission>
                {
                    new Permission(siteId, declaration.Resource, PermissionKeys.KeyEntityId, declaration.Operation, RoleNames.Admin, null, false)
                };
                await _permissions.UpdatePermissionsAsync(siteId, declaration.Resource, PermissionKeys.KeyEntityId, tombstone);

                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Revoked Permission Key {Permission} For ModuleDefinition {ModuleDefinitionId} ({Rows} Grant(s) Removed)",
                    declaration.Key, id, rows.Count);
            }
        }

        // DELETE api/<controller>/5?siteid=x
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Delete(int id, int siteid)
        {
            var alias = await _tenantManager.GetAliasAsync(); // save current
            ModuleDefinition moduledefinition = await _moduleDefinitions.GetModuleDefinitionAsync(id, siteid);
            if (moduledefinition != null && moduledefinition.SiteId == alias.SiteId && Utilities.GetAssemblyName(moduledefinition.ServerManagerType) != "Stomme.Server")
            {
                // execute uninstall logic or scripts
                if (!string.IsNullOrEmpty(moduledefinition.ServerManagerType))
                {
                    Type moduletype = Type.GetType(moduledefinition.ServerManagerType);
                    if (moduletype != null)
                    {
                        string result = string.Empty;
                        foreach (Tenant tenant in await _tenants.GetTenantsAsync())
                        {
                            try
                            {
                                if (moduletype.GetInterface(nameof(IInstallable)) != null)
                                {
                                    _tenantManager.SetTenant(tenant);
                                    var moduleobject = ActivatorUtilities.CreateInstance(_serviceProvider, moduletype);
                                    await ((IInstallable)moduleobject).UninstallAsync(tenant);
                                }
                                else
                                {
                                    await _sql.ExecuteScriptAsync(tenant, moduletype.Assembly, Utilities.GetTypeName(moduledefinition.ModuleDefinitionName) + ".Uninstall.sql");
                                }
                            }
                            catch (Exception ex)
                            {
                                result = "For " + tenant.Name + " " + ex.Message;
                            }
                        }
                        await _tenantManager.SetAliasAsync(alias); // restore current
                        if (string.IsNullOrEmpty(result))
                        {
                            _logger.Log(LogLevel.Information, this, LogFunction.Delete, "{ModuleDefinitionName} Uninstalled For All Tenants", moduledefinition.ModuleDefinitionName);
                        }
                        else
                        {
                            _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Error Uninstalling {ModuleDefinitionName} {Error}", moduledefinition.ModuleDefinitionName, result);
                        }
                    }
                }

                // remove module assets
                if (_installationManager.UninstallPackage(moduledefinition.PackageName))
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Module Assets Removed For {ModuleDefinitionName}", moduledefinition.ModuleDefinitionName);
                }
                else
                {
                    // attempt to delete assemblies based on naming convention
                    foreach(string asset in Directory.GetFiles(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), Utilities.GetTypeName(moduledefinition.ModuleDefinitionName) + "*.*"))
                    {
                        System.IO.File.Delete(asset);
                    }
                    _logger.Log(LogLevel.Warning, this, LogFunction.Delete, "Module Assets Removed For {ModuleDefinitionName}. Please Note That Some Assets May Have Been Missed Due To A Missing Asset Manifest. An Asset Manifest Is Only Created If A Module Is Installed From A Nuget Package.", moduledefinition.Name);
                }

                // clean up module static resource folder
                string assetpath = Path.Combine(_environment.WebRootPath, "Modules", Utilities.GetTypeName(moduledefinition.ModuleDefinitionName));
                if (Directory.Exists(assetpath))
                {
                    Directory.Delete(assetpath, true);
                    _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Module Static Resources Folder Removed For {ModuleDefinitionName}", moduledefinition.ModuleDefinitionName);
                }

                // remove module definition
                await _moduleDefinitions.DeleteModuleDefinitionAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.ModuleDefinition, moduledefinition.ModuleDefinitionId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, moduledefinition.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Module Definition {ModuleDefinitionName} Deleted", moduledefinition.Name);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized ModuleDefinition Delete Attempt {ModuleDefinitionId} {SiteId}", id, siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET: api/<controller>/templates
        [HttpGet("templates")]
        [Authorize(Roles = RoleNames.Host)]
        public List<Template> GetTemplates()
        {
            var templates = new List<Template>();
            var root = Directory.GetParent(_environment.ContentRootPath);
            string templatePath = Utilities.PathCombine(_environment.WebRootPath, "Modules", "Templates", Path.DirectorySeparatorChar.ToString());
            if (Directory.Exists(templatePath))
            {
                foreach (string directory in Directory.GetDirectories(templatePath))
                {
                    string name = directory.Replace(templatePath, "");
                    var manifest = Directory.GetFiles(directory, "*.json");
                    if (manifest.Any())
                    {
                        var template = JsonSerializer.Deserialize<Template>(System.IO.File.ReadAllText(manifest[0]));
                        template.Name = name;
                        template.Location = "";
                        if (template.Type.ToLower() != "internal")
                        {
                            template.Location = Utilities.PathCombine(root.Parent.ToString(), Path.DirectorySeparatorChar.ToString());
                        }
                        templates.Add(template);
                    }
                    else
                    {
                        templates.Add(new Template { Name = name, Title = name, Type = "External", Version = "", Namespace = "", Location = Utilities.PathCombine(root.Parent.ToString(), Path.DirectorySeparatorChar.ToString()) });
                    }
                }
            }
            return templates;
        }

        private void ProcessTemplatesRecursively(DirectoryInfo current, string rootPath, string rootFolder, string templatePath, ModuleDefinition moduleDefinition)
        {
            var tokenReplace = InitializeTokenReplace(rootPath, rootFolder, moduleDefinition);

            // process folder
            var folderPath = Utilities.PathCombine(rootPath, current.FullName.Replace(templatePath, ""));
            folderPath = tokenReplace.ReplaceTokens(folderPath);
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            tokenReplace.AddSource("Folder", folderPath);
            var files = current.GetFiles("*.*");
            if (files != null)
            {
                foreach (FileInfo file in files)
                {
                    // process file
                    var filePath = Path.Combine(folderPath, file.Name);
                    filePath = tokenReplace.ReplaceTokens(filePath);
                    tokenReplace.AddSource("File", Path.GetFileName(filePath));

                    if (ModuleTemplateTokens.IsBinary(file.Name))
                    {
                        // A binary file is copied byte for byte: read as text and written back, the package icon came out
                        // re-encoded and invalid (BL-86 increment 7).
                        System.IO.File.Copy(file.FullName, filePath, overwrite: true);
                        continue;
                    }

                    var text = System.IO.File.ReadAllText(file.FullName);
                    text = tokenReplace.ReplaceTokens(text);
                    System.IO.File.WriteAllText(filePath, text);
                }

                var folders = current.GetDirectories();

                foreach (DirectoryInfo folder in folders.Reverse())
                {
                    ProcessTemplatesRecursively(folder, rootPath, rootFolder, templatePath, moduleDefinition);
                }
            }
        }

        private ITokenReplace InitializeTokenReplace(string rootPath, string rootFolder, ModuleDefinition moduleDefinition)
        {
            var tokenReplace = _serviceProvider.GetService<ITokenReplace>();
            tokenReplace.AddSource(() =>
            {
                return new Dictionary<string, object>
                {
                    { "RootPath", rootPath },
                    { "RootFolder", rootFolder },
                    { "Owner", moduleDefinition.Owner },
                    { "Module", moduleDefinition.Name },
                    { "ModuleKey", ModuleTemplateTokens.ModuleKey(moduleDefinition.Name) },
                    { "Description", moduleDefinition.Description },
                    { "ServerManagerType", moduleDefinition.ServerManagerType }
                };
            });

            // A generated module references Stomme through ONE package, Stomme.Sdk.Slim (D81): at the chosen release,
            // restored from the framework's package feed - or, for "local", at this framework's own version from the slim SDK
            // packed out of this source tree (examples/local-feed, examples/pack-sdk-slim.ps1). The old template referenced
            // the Debug build's DLLs by path instead, which D81 retired (BL-86 increment 7). "Local" is the version this tree's
            // packages carry, so a preview's label too (BL-192): the release number alone names no package on a preview's feed.
            var local = moduleDefinition.Version == "local";
            var frameworkVersion = local ? Constants.PackageVersion : moduleDefinition.Version;
            var localFeed = Utilities.PathCombine(Directory.GetParent(rootPath.TrimEnd(Path.DirectorySeparatorChar))?.FullName ?? rootPath, "examples", "local-feed");
            tokenReplace.AddSource(() => new Dictionary<string, object>
            {
                { "FrameworkVersion", frameworkVersion },
                { "FrameworkRelease", ModuleTemplateTokens.ReleaseNumber(frameworkVersion) },
                { "StommeFeed", local && Directory.Exists(localFeed) ? localFeed : ModuleTemplateTokens.PackageFeed },
            });

            return tokenReplace;
        }
    }
}
