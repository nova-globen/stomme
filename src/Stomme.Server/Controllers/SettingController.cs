using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Security;
using System.Linq;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Repository;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using System.IO;
using System.Text.RegularExpressions;
using System;
using System.Threading.Tasks;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class SettingController : Controller
    {
        private readonly ISettingRepository _settings;
        private readonly IPageModuleRepository _pageModules;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;

        private readonly IOptions<CookieAuthenticationOptions> _cookieOptions;
        private readonly IOptionsSnapshot<CookieAuthenticationOptions> _cookieOptionsSnapshot;
        private readonly IOptionsMonitorCache<CookieAuthenticationOptions> _cookieOptionsMonitorCache;

        private readonly IOptions<OpenIdConnectOptions> _oidcOptions;
        private readonly IOptionsSnapshot<OpenIdConnectOptions> _oidcOptionsSnapshot;
        private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptionsMonitorCache;

        private readonly IOptions<OAuthOptions> _oauthOptions;
        private readonly IOptionsSnapshot<OAuthOptions> _oauthOptionsSnapshot;
        private readonly IOptionsMonitorCache<OAuthOptions> _oauthOptionsMonitorCache;

        private readonly IOptions<IdentityOptions> _identityOptions;
        private readonly IOptionsSnapshot<IdentityOptions> _identityOptionsSnapshot;
        private readonly IOptionsMonitorCache<IdentityOptions> _identityOptionsMonitorCache;

        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;
        private readonly Stomme.Infrastructure.Security.VisitorCookie _visitorCookie;

        public SettingController(ISettingRepository settings, IPageModuleRepository pageModules, IUserPermissions userPermissions, ITenantManager tenantManager, ISyncManager syncManager, 
            IOptions<CookieAuthenticationOptions> cookieOptions, IOptionsSnapshot<CookieAuthenticationOptions> cookieOptionsSnapshot, IOptionsMonitorCache<CookieAuthenticationOptions> cookieOptionsMonitorCache,
            IOptions<OpenIdConnectOptions> oidcOptions, IOptionsSnapshot<OpenIdConnectOptions> oidcOptionsSnapshot, IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsMonitorCache,
            IOptions<OAuthOptions> oauthOptions, IOptionsSnapshot<OAuthOptions> oauthOptionsSnapshot, IOptionsMonitorCache<OAuthOptions> oauthOptionsMonitorCache,
            IOptions<IdentityOptions> identityOptions, IOptionsSnapshot<IdentityOptions> identityOptionsSnapshot, IOptionsMonitorCache<IdentityOptions> identityOptionsMonitorCache,
            ILogManager logger, Stomme.Infrastructure.Security.VisitorCookie visitorCookie)
        {
            _visitorCookie = visitorCookie;
            _settings = settings;
            _pageModules = pageModules;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _cookieOptions = cookieOptions;
            _cookieOptionsSnapshot = cookieOptionsSnapshot;
            _cookieOptionsMonitorCache = cookieOptionsMonitorCache;
            _oidcOptions = oidcOptions;
            _oidcOptionsSnapshot = oidcOptionsSnapshot;
            _oidcOptionsMonitorCache = oidcOptionsMonitorCache;
            _oauthOptions = oauthOptions;
            _oauthOptionsSnapshot = oauthOptionsSnapshot;
            _oauthOptionsMonitorCache = oauthOptionsMonitorCache;
            _identityOptions = identityOptions;
            _identityOptionsSnapshot = identityOptionsSnapshot;
            _identityOptionsMonitorCache = identityOptionsMonitorCache;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>
        [HttpGet]
        public async Task<IEnumerable<Setting>> Get(string entityName, int entityId)
        {
            var alias = await _tenantManager.GetAliasAsync();

            List<Setting> settings = new List<Setting>();
            if (await IsAuthorized(alias, entityName, entityId, PermissionNames.View))
            {
                // The site secret store's rows are never listed - not even to the Host. Only the store reads them.
                settings = (await _settings.GetSettingsAsync(entityName, entityId)).Where(item => !IsStoredSecret(item)).ToList();
                if (await FilterPrivate(alias, entityName, entityId))
                {
                    settings = settings.Where(item => !item.IsPrivate).ToList();
                }
            }
            else
            {
                // suppress unauthorized visitor logging as it is usually caused by clients that do not support cookies or private browsing sessions
                if (FormatName(entityName) != EntityNames.Visitor) 
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Read, "User Not Authorized To Access Settings For EntityName {EntityName} And EntityId {EntityId}", entityName, entityId);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
            return settings;
        }

        // GET api/<controller>/5/xxx
        [HttpGet("{id}/{entityName}")]
        public async Task<Setting> Get(int id, string entityName)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Setting setting = await _settings.GetSettingAsync(entityName, id);
            if (IsStoredSecret(setting))
            {
                // Answered exactly like a row that does not exist.
                setting = null;
            }

            if (setting != null && await IsAuthorized(alias, setting.EntityName, setting.EntityId, PermissionNames.View))
            {
                if (await FilterPrivate(alias, entityName, id) && setting.IsPrivate)
                {
                    setting = null;
                }
                return setting;
            }
            else
            {
                if (setting != null && FormatName(entityName) != EntityNames.Visitor)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Read, "User Not Authorized To Access SettingId {SettingId} For EntityName {EntityName} ", id, entityName);
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
        public async Task<Setting> Post([FromBody] Setting setting)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (WouldWriteStoredSecret(setting?.EntityName, setting?.SettingName, setting?.IsPrivate ?? false))
            {
                return RefuseStoredSecret("Add", setting.EntityName, setting.EntityId, setting.SettingName);
            }

            if (setting != null && RefusesHostReserved(setting.EntityName, setting.SettingName))
            {
                return RefuseHostReserved("Add", setting.EntityName, setting.EntityId, setting.SettingName);
            }

            if (ModelState.IsValid && await IsAuthorized(alias, setting.EntityName, setting.EntityId, PermissionNames.Edit))
            {
                setting = await _settings.AddSettingAsync(setting);
                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Setting Added {Setting}", Describe(setting));
            }
            else
            {
                if (FormatName(setting.EntityName) != EntityNames.Visitor)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Not Authorized To Add Setting {Setting}", Describe(setting));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                setting = null;
            }
            return setting;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        public async Task<Setting> Put(int id, [FromBody] Setting setting)
        {
            var alias = await _tenantManager.GetAliasAsync();

            // The row BY ID is what gets overwritten, whatever name the body claims.
            var existingById = setting == null ? null : await _settings.GetSettingAsync(setting.EntityName, id);
            if (WouldWriteStoredSecret(setting?.EntityName, setting?.SettingName, setting?.IsPrivate ?? false) || IsStoredSecret(existingById))
            {
                return RefuseStoredSecret("Update", setting.EntityName, setting.EntityId, setting.SettingName);
            }

            // The BODY's entity is what gets authorized below, so the body must name the row's own entity - a
            // registered user, allowed their own User settings, otherwise replaced a Site row by its id with a body calling
            // it theirs, and the site's setting was gone (driven).
            if (existingById != null && (!string.Equals(existingById.EntityName, setting.EntityName, StringComparison.OrdinalIgnoreCase) || existingById.EntityId != setting.EntityId))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {SettingId} Update Refused - The Row Belongs To {RowEntityName}:{RowEntityId}, The Body Names {EntityName}:{EntityId}",
                    id, existingById.EntityName, existingById.EntityId, setting.EntityName, setting.EntityId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }

            if (setting != null && (RefusesHostReserved(setting.EntityName, setting.SettingName)
                || (existingById != null && RefusesHostReserved(existingById.EntityName, existingById.SettingName))))
            {
                return RefuseHostReserved("Update", setting.EntityName, setting.EntityId, setting.SettingName);
            }

            if (ModelState.IsValid && setting.SettingId == id && await IsAuthorized(alias, setting.EntityName, setting.EntityId, PermissionNames.Edit))
            {
                setting = await _settings.UpdateSettingAsync(setting);
                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Setting Updated {Setting}", Describe(setting));
            }
            else
            {
                if (FormatName(setting.EntityName) != EntityNames.Visitor)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Update, "User Not Authorized To Update Setting {Setting}", Describe(setting));
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                setting = null;
            }
            return setting;
        }

        // PUT api/<controller>/site/1/settingname  — the value travels in the BODY
        /// <summary>
        /// Adds or updates one setting, with the value in the request body.
        /// </summary>
        /// <remarks>
        /// The route below puts the VALUE IN THE URL, and <c>LogManager</c> copies the request URL into
        /// <c>Log.Url</c> for every entry raised during a request — so saving a private setting through it wrote
        /// the secret into the site Log table, readable by every administrator, by a door that has nothing to do
        /// with what was being logged. BL-112 redacted the log ARGUMENTS; this is the other door.
        /// The legacy route is kept working for any caller still on it, and redacts its own URL.
        /// </remarks>
        [HttpPut("{entityName}/{entityId}/{settingName}")]
        public Task Put(string entityName, int entityId, string settingName, [FromBody] Setting setting) =>
            AddOrUpdateAsync(entityName, entityId, settingName, setting?.SettingValue, setting?.IsPrivate ?? false);

        // PUT api/<controller>/site/1/settingname/x/false
        [HttpPut("{entityName}/{entityId}/{settingName}/{settingValue}/{isPrivate}")]
        public Task Put(string entityName, int entityId, string settingName, string settingValue, bool isPrivate)
        {
            // This URL carries the value. Declare a safe one BEFORE anything can log, so no entry raised during
            // this request records the secret. The replacement route above avoids the problem entirely.
            var request = HttpContext?.Request;
            if (request != null)
            {
                HttpContext.Items[LogManager.RedactedUrlKey] =
                    $"{request.Scheme}://{request.Host}/api/Setting/{entityName}/{entityId}/{settingName}/[redacted]/{isPrivate}";
            }

            return AddOrUpdateAsync(entityName, entityId, settingName, settingValue, isPrivate);
        }

        private async Task AddOrUpdateAsync(string entityName, int entityId, string settingName, string settingValue, bool isPrivate)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (await IsAuthorized(alias, entityName, entityId, PermissionNames.Edit))
            {
                Setting setting = await _settings.GetSettingAsync(entityName, entityId, settingName);
                if (IsStoredSecret(setting) || WouldWriteStoredSecret(entityName, settingName, isPrivate))
                {
                    RefuseStoredSecret("Add Or Update", entityName, entityId, settingName);
                    return;
                }

                if (RefusesHostReserved(entityName, settingName))
                {
                    RefuseHostReserved("Add Or Update", entityName, entityId, settingName);
                    return;
                }

                if (setting == null)
                {
                    setting = new Setting();
                    setting.EntityName = entityName;
                    setting.EntityId = entityId;
                    setting.SettingName = settingName;
                    setting.SettingValue = settingValue;
                    setting.IsPrivate = isPrivate;
                    setting = await _settings.AddSettingAsync(setting);
                    await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Create);
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "Setting Created {Setting}", Describe(setting));
                }
                else
                {
                    if (setting.SettingValue != settingValue || setting.IsPrivate != isPrivate)
                    {
                        setting.SettingValue = settingValue;
                        setting.IsPrivate = isPrivate;
                        setting = await _settings.UpdateSettingAsync(setting);
                        await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Update);
                        _logger.Log(LogLevel.Information, this, LogFunction.Update, "Setting Updated {Setting}", Describe(setting));
                    }
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Update, "User Not Authorized To Add Or Update Setting For {EntityName}:{EntityId}", entityName, entityId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // PUT api/<controller>/site/1
        [HttpPut("{entityName}/{entityId}")]
        public async Task Put(string entityName, int entityId, [FromBody] List<Setting> settings)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && await IsAuthorized(alias, entityName,entityId, PermissionNames.Edit))
            {
                var existingSettings = (await _settings.GetSettingsAsync(entityName, entityId)).ToList();
                foreach (Setting setting in settings)
                {
                    // The ROUTE's entity is what was authorized, and a new row is added with the entity its BODY
                    // names - so a registered user, allowed their own User settings, planted Site rows and Host rows in the
                    // master store (driven). A row naming another entity is not this request's to write; the client
                    // stamps every row with the route's entity, so a legitimate save never carries one.
                    if (!string.Equals(setting.EntityName, entityName, StringComparison.OrdinalIgnoreCase) || setting.EntityId != entityId)
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {SettingName} Skipped - It Names {BodyEntityName}:{BodyEntityId}, Not The Authorized {EntityName}:{EntityId}",
                            setting.SettingName, setting.EntityName, setting.EntityId, entityName, entityId);
                        continue;
                    }

                    Setting existing = existingSettings.FirstOrDefault(item => item.SettingName.Equals(setting.SettingName, StringComparison.OrdinalIgnoreCase));
                    if (IsStoredSecret(existing) || WouldWriteStoredSecret(entityName, setting.SettingName, setting.IsPrivate))
                    {
                        // Skipped, not failed - a bulk save carries a whole settings page, and one reserved row
                        // in it must not cost the administrator the rest of their edit.
                        _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {SettingName} Skipped - It Belongs To The Site Secret Store", setting.SettingName);
                        continue;
                    }

                    if (RefusesHostReserved(entityName, setting.SettingName))
                    {
                        // Skipped, not failed - a site's settings page carries these rows back as it read them, and an
                        // Administrator's save must not fail over them. Logged only when it would have changed something.
                        if (existing == null || existing.SettingValue != setting.SettingValue)
                        {
                            _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {SettingName} Skipped - It Is The Host's To Set", setting.SettingName);
                        }

                        continue;
                    }

                    if (existing == null)
                    {
                        setting.SettingId = 0; // initialize
                        existing = await _settings.AddSettingAsync(setting);
                        await AddSyncEventAsync(alias, existing.EntityName, existing.EntityId, existing.SettingId, SyncEventActions.Create);
                        _logger.Log(LogLevel.Information, this, LogFunction.Update, "Setting Created {Setting}", Describe(existing));

                    }
                    else
                    {
                        // note that setting.SettingId will be 0 or -1 if the Settings were converted from a Dictionary in the SettingService
                        if (existing.SettingValue != setting.SettingValue || (existing.IsPrivate != setting.IsPrivate && setting.SettingId != 0))
                        {
                            existing.SettingValue = setting.SettingValue;
                            existing.IsPrivate = setting.IsPrivate;
                            existing = await _settings.UpdateSettingAsync(existing);
                            await AddSyncEventAsync(alias, existing.EntityName, existing.EntityId, existing.SettingId, SyncEventActions.Update);
                            _logger.Log(LogLevel.Information, this, LogFunction.Update, "Setting Updated {Setting}", Describe(existing));
                        }
                    }
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Update, "User Not Authorized To Update Settings For {EntityName}:{EntityId}", entityName, entityId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // DELETE api/<controller>/site/1/settingname/settingid
        [HttpDelete("{entityName}/{entityId}/{settingName}")]
        public async Task Delete(string entityName, int entityId, string settingName)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Setting setting = await _settings.GetSettingAsync(entityName, entityId, settingName);
            if (IsStoredSecret(setting))
            {
                RefuseStoredSecret("Delete", entityName, entityId, settingName);
                return;
            }

            if (RefusesHostReserved(entityName, settingName))
            {
                RefuseHostReserved("Delete", entityName, entityId, settingName);
                return;
            }

            if (setting != null && await IsAuthorized(alias, setting.EntityName, setting.EntityId, PermissionNames.Edit))
            {
                await _settings.DeleteSettingAsync(setting.EntityName, setting.SettingId);
                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Setting Deleted {Setting}", Describe(setting));
            }
            else
            {
                if (FormatName(entityName) != EntityNames.Visitor)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Setting Does Not Exist Or User Not Authorized To Delete Setting For EntityName {EntityName} EntityId {EntityId} SettingName {SettingName}", entityName, entityId, settingName);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
        }

        // DELETE api/<controller>/1/site
        [HttpDelete("{id}/{entityName}")]
        public async Task Delete(int id, string entityName)
        {
            var alias = await _tenantManager.GetAliasAsync();

            Setting setting = await _settings.GetSettingAsync(entityName, id);
            if (IsStoredSecret(setting))
            {
                RefuseStoredSecret("Delete", setting.EntityName, setting.EntityId, setting.SettingName);
                return;
            }

            if (setting != null && RefusesHostReserved(setting.EntityName, setting.SettingName))
            {
                RefuseHostReserved("Delete", setting.EntityName, setting.EntityId, setting.SettingName);
                return;
            }

            if (setting != null && await IsAuthorized(alias, setting.EntityName, setting.EntityId, PermissionNames.Edit))
            {
                await _settings.DeleteSettingAsync(setting.EntityName, setting.SettingId);
                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Setting Deleted {Setting}", Describe(setting));
            }
            else
            {
                if (FormatName(entityName) != EntityNames.Visitor)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Setting Does Not Exist Or User Not Authorized To Delete Setting For SettingId {SettingId} For EntityName {EntityName} ", id, entityName);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
        }

        // GET: api/<controller>/entitynames
        [HttpGet("entitynames")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<string>> GetEntityNames()
        {
            return await _settings.GetEntityNamesAsync();
        }

        // GET: api/<controller>/entityids?entityname=x
        [HttpGet("entityids")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<int>> GetEntityIds(string entityName)
        {
            return await _settings.GetEntityIdsAsync(entityName);
        }

        // POST api/<controller>/import?settings=x
        [HttpPost("import")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Result> Import([FromBody] Result settings)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && !string.IsNullOrEmpty(settings.Message))
            {
                int rows = 0;

                using (StringReader reader = new StringReader(settings.Message))
                {
                    // regex to split by comma - ignoring commas within double quotes
                    string pattern = ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)";
                    string row;

                    while ((row = reader.ReadLine()) != null)
                    {
                        List<string> cols = new List<string>();
                        string col = "";
                        int startIndex = 0;

                        MatchCollection matches = Regex.Matches(row, pattern);
                        foreach (Match match in matches)
                        {
                            col = row.Substring(startIndex, match.Index - startIndex);
                            if (col.StartsWith("\"") && col.EndsWith("\""))
                            {
                               col = col.Substring(1, col.Length - 2).Replace("\"\"", "\"");
                            }
                            cols.Add(col.Trim());
                            startIndex = match.Index + match.Length;
                        }
                        col = row.Substring(startIndex);
                        if (col.StartsWith("\"") && col.EndsWith("\""))
                        {
                            col = col.Substring(1, col.Length - 2).Replace("\"\"", "\"");
                        }
                        cols.Add(col.Trim());

                        if (cols.Count == 5 && cols[0].ToLower() != "entity" && int.TryParse(cols[1], out int entityId) && bool.TryParse(cols[4], out bool isPrivate))
                        {
                            var setting = await _settings.GetSettingAsync(cols[0], entityId, cols[2]);
                            if (IsStoredSecret(setting) || WouldWriteStoredSecret(cols[0], cols[2], isPrivate))
                            {
                                // An import never writes into the site secret store, and does not count the row.
                                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Imported Setting {SettingName} Skipped - It Belongs To The Site Secret Store", cols[2]);
                                continue;
                            }

                            if (setting == null)
                            {
                                setting = new Setting { EntityName = cols[0], EntityId = entityId, SettingName = cols[2], SettingValue = cols[3], IsPrivate = isPrivate };
                                await _settings.AddSettingAsync(setting);
                                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Create);
                            }
                            else
                            {
                                setting.SettingValue = cols[3];
                                setting.IsPrivate = isPrivate;
                                await _settings.UpdateSettingAsync(setting);
                                await AddSyncEventAsync(alias, setting.EntityName, setting.EntityId, setting.SettingId, SyncEventActions.Update);
                            }
                            rows++;
                        }
                    }
                }

                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Settings Imported {Rows}", rows);
                settings.Message = $"{rows} Settings Imported";
                settings.Success = true;
                return settings;
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Settings Import Attempt");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                settings.Message = "";
                settings.Success = false;
                return settings;
            }
        }

        // DELETE api/<controller>/clear
        [HttpDelete("clear")]
        [Authorize(Roles = RoleNames.Admin)]
        public void Clear()
        {
            (_cookieOptions as SiteOptionsManager<CookieAuthenticationOptions>).Reset();
            (_cookieOptionsSnapshot as SiteOptionsManager<CookieAuthenticationOptions>).Reset();
            _cookieOptionsMonitorCache.Clear();

            (_oidcOptions as SiteOptionsManager<OpenIdConnectOptions>).Reset();
            (_oidcOptionsSnapshot as SiteOptionsManager<OpenIdConnectOptions>).Reset();
            _oidcOptionsMonitorCache.Clear();

            (_oauthOptions as SiteOptionsManager<OAuthOptions>).Reset();
            (_oauthOptionsSnapshot as SiteOptionsManager<OAuthOptions>).Reset();
            _oauthOptionsMonitorCache.Clear();

            (_identityOptions as SiteOptionsManager<IdentityOptions>).Reset();
            (_identityOptionsSnapshot as SiteOptionsManager<IdentityOptions>).Reset();
            _identityOptionsMonitorCache.Clear();

            _logger.Log(LogLevel.Information, this, LogFunction.Other, "Site Options Cache Cleared");
        }

        private async Task<bool> IsAuthorized(Alias alias, string entityName, int entityId, string permissionName)
        {
            bool authorized = false;

            if (entityName == EntityNames.PageModule)
            {
                entityName = EntityNames.Module;
                entityId = (await _pageModules.GetPageModuleAsync(entityId)).ModuleId;
            }

            switch (NormalizeEntityName(entityName))
            {
                case EntityNames.Tenant:
                case EntityNames.ModuleDefinition:
                case EntityNames.Host:
                case EntityNames.Job:
                case EntityNames.Theme:
                    if (FormatName(permissionName) == PermissionNames.Edit)
                    {
                        authorized = User.IsInRole(RoleNames.Host);
                    }
                    else
                    {
                        authorized = true;
                    }
                    break;
                case EntityNames.Site:
                case EntityNames.Role:
                    if (FormatName(permissionName) == PermissionNames.Edit)
                    {
                        authorized = User.IsInRole(RoleNames.Admin);
                    }
                    else
                    {
                        authorized = true;
                    }
                    break;
                case EntityNames.Page:
                case EntityNames.Module:
                case EntityNames.Folder:
                    authorized = await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, entityId, permissionName);
                    break;
                case EntityNames.User:
                    authorized = await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, -1, PermissionNames.Write, RoleNames.Admin) || ((await _userPermissions.GetUserAsync(User)).UserId == entityId);
                    break;
                case EntityNames.Visitor:
                    authorized = User.IsInRole(RoleNames.Admin);
                    if (!authorized)
                    {
                        // Only a visitor cookie this host issued proves the id: one of the caller's own making
                        // read and wrote any visitor's settings.
                        authorized = (entityId == _visitorCookie.VisitorIdOf(HttpContext.Request, alias.SiteId));
                    }
                    break;
                default: // custom entity
                    // Closed to anonymous callers. A custom entity is an app module's own, and a row its author
                    // left non-private was readable by anyone; the framework's own pages read these only signed in
                    // (profile options, user administration). Site settings stay public: the browser client renders from them.
                    authorized = User.Identity?.IsAuthenticated == true;
                    if (FormatName(permissionName) == PermissionNames.Edit)
                    {
                        if (entityId == -1)
                        {
                            authorized = User.IsInRole(entityName.ToLower().StartsWith("master:") ? RoleNames.Host : RoleNames.Admin);
                        }
                        else
                        {
                            authorized = await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, entityId, permissionName);
                        }
                    }
                    break;
            }
            return authorized;
        }

        private async Task<bool> FilterPrivate(Alias alias, string entityName, int entityId)
        {
            bool filter = false;
            switch (NormalizeEntityName(entityName))
            {
                case EntityNames.Tenant:
                case EntityNames.ModuleDefinition:
                case EntityNames.Host:
                case EntityNames.Job:
                case EntityNames.Theme:
                    filter = !User.IsInRole(RoleNames.Host);
                    break;
                case EntityNames.Site:
                case EntityNames.Role:
                    filter = !User.IsInRole(RoleNames.Admin);
                    break;
                case EntityNames.Page:
                case EntityNames.Module:
                case EntityNames.Folder:
                    filter = !await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, entityId, PermissionNames.Edit);
                    break;
                case EntityNames.User:
                    filter = !await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, -1, PermissionNames.Write, RoleNames.Admin) && (await _userPermissions.GetUserAsync(User)).UserId != entityId;
                    break;
                case EntityNames.Visitor:
                    if (!User.IsInRole(RoleNames.Admin))
                    {
                        filter = (entityId != _visitorCookie.VisitorIdOf(Request, alias.SiteId));
                    }
                    break;
                default: // custom entity
                    filter = !User.IsInRole(entityName.ToLower().StartsWith("master:") ? RoleNames.Host : RoleNames.Admin) && !await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, entityName, entityId, PermissionNames.Edit);
                    break;
            }
            return filter;
        }

        private async Task AddSyncEventAsync(Alias alias, string EntityName, int EntityId, int SettingId, string Action)
        {
            // The sync path must publish the EXACT EntityNames casing — FormatName title-cases a multi-word
            // entity ("ModuleDefinition" -> "Moduledefinition"), so its "...Setting" event was invisible to
            // any subscriber matching the constant, and the switch below never matched (the surviving half
            // of the D88 root cause).
            var entityName = NormalizeEntityName(EntityName);
            await _syncManager.AddSyncEventAsync(alias, entityName + "Setting", SettingId, Action);

            switch (entityName)
            {
                // Host settings are merged into the cached Site object (ServerSiteService.GetSiteAsync), so a
                // Host-level change must refresh it too — without this it only took effect after a restart.
                case EntityNames.Host:
                case EntityNames.Module:
                case EntityNames.Page:
                case EntityNames.Site:
                    await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                    break;
                case EntityNames.User:
                    await _syncManager.AddSyncEventAsync(alias, entityName, EntityId, SyncEventActions.Update);
                    break;
            }
        }

        private string FormatName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                // entity names and permission names are case sensitive
                name = name.Substring(0, 1).ToUpper() + name.Substring(1).ToLower();
            }
            return name;
        }

        // The set of framework EntityNames, canonicalized once for case-insensitive normalization.
        private static readonly string[] KnownEntityNames =
        {
            EntityNames.Alias, EntityNames.File, EntityNames.Folder, EntityNames.Job, EntityNames.Language,
            EntityNames.Module, EntityNames.ModuleDefinition, EntityNames.Notification, EntityNames.Page,
            EntityNames.PageModule, EntityNames.Profile, EntityNames.Role, EntityNames.Setting, EntityNames.Site,
            EntityNames.SiteGroup, EntityNames.SiteGroupMember, EntityNames.Tenant, EntityNames.Theme,
            EntityNames.UrlMapping, EntityNames.User, EntityNames.UserRole, EntityNames.Visitor, EntityNames.Host
        };

        // Resolves a case-insensitive entity name to its exact EntityNames constant, so the authorization
        // switches match a multi-word (camelCase) name like "ModuleDefinition". FormatName title-cases
        // ("ModuleDefinition" -> "Moduledefinition"), which never equals the constant, so ModuleDefinition —
        // the ONLY multi-word entity gated in those switches — silently fell through to the custom-entity
        // default branch and was denied for the host. Unknown/custom entities fall back to FormatName,
        // preserving the existing default-branch behaviour.
        private string NormalizeEntityName(string name)
        {
            foreach (var known in KnownEntityNames)
            {
                if (string.Equals(name, known, System.StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }
            return FormatName(name);
        }

        /// <summary>
        /// True for a row of the SITE SECRET STORE: a PRIVATE Site setting whose name is in the store's reference-key
        /// namespace. <see cref="ISiteSecretStore"/> is the only thing that reads or writes one; through this API a site
        /// Administrator - who need not hold the module's own key - could otherwise read the ciphertext, overwrite the
        /// secret or delete it (measured: all three). The name is matched in LOWER CASE because SQL Server's default
        /// collation and RavenDB find a setting by name case-insensitively. A NON-private row with such a name - the MCP
        /// tool-approval override <c>mcp.tool.{name}.approval</c> is one - is an ordinary setting and stays one.
        /// </summary>
        private static bool IsStoredSecret(Setting setting) =>
            setting != null && setting.IsPrivate && IsSecretName(setting.EntityName, setting.SettingName);

        /// <summary>True when a write would CREATE (or turn a row into) a private row in the store's namespace - a planted secret.</summary>
        private static bool WouldWriteStoredSecret(string entityName, string settingName, bool isPrivate) =>
            isPrivate && IsSecretName(entityName, settingName);

        private static bool IsSecretName(string entityName, string settingName) =>
            string.Equals(entityName, EntityNames.Site, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(settingName)
            && SiteSecretStore.IsValidReferenceKey(settingName.ToLowerInvariant());

        /// <summary>
        /// The Site settings that widen a site's egress to private and loopback addresses - the connector egress
        /// (which the AI assistant's model endpoint rides too), the webhook channel's and the MCP client's. Opening the
        /// installation's own network is the Host's decision, not a site Administrator's (D110's correction), so every door
        /// refuses them to anyone else; the Connections page is where the Host sets the first. Matched case-insensitively and
        /// trimmed, as SQL Server and RavenDB find a setting by name. <c>EgressAllowlistDriveTests</c> gates the list: every
        /// <c>AllowedHostsSettingName</c> the server declares must be on it.
        /// </summary>
        public static IReadOnlyCollection<string> HostReservedSiteSettingNames { get; } = new[]
        {
            Stomme.Infrastructure.Connectivity.HostConnectorEgress.AllowedHostsSettingName,
            Stomme.Modules.Notifications.Infrastructure.Channels.WebhookSsrfGuard.AllowedHostsSettingName,
            Stomme.Modules.Mcp.Infrastructure.McpEndpointGuard.AllowedHostsSettingName,
        };

        public static bool IsHostReservedSetting(string entityName, string settingName) =>
            string.Equals(entityName, EntityNames.Site, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(settingName)
            && HostReservedSiteSettingNames.Contains(settingName.Trim(), StringComparer.OrdinalIgnoreCase);

        private bool RefusesHostReserved(string entityName, string settingName) =>
            !User.IsInRole(RoleNames.Host) && IsHostReservedSetting(entityName, settingName);

        private Setting RefuseHostReserved(string action, string entityName, int entityId, string settingName)
        {
            _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {Action} Refused - {EntityName}:{EntityId}/{SettingName} Is The Host's To Set", action, entityName, entityId, settingName);
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return null;
        }

        private Setting RefuseStoredSecret(string action, string entityName, int entityId, string settingName)
        {
            _logger.Log(LogLevel.Error, this, LogFunction.Security, "Setting {Action} Refused - {EntityName}:{EntityId}/{SettingName} Belongs To The Site Secret Store", action, entityName, entityId, settingName);
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return null;
        }

        /// <summary>
        /// A setting WITHOUT its value, for the event log.
        /// </summary>
        /// <remarks>
        /// <c>LogManager</c> serialises every argument whole into BOTH <c>Log.Properties</c> and the interpolated
        /// <c>Log.Message</c>, so passing a <see cref="Setting"/> put its VALUE in the site Log table - readable by
        /// every administrator in the event-log viewer, and driven here for a private setting saved through the
        /// admin UI. The value is never logged, private or not: an operator reading the log needs to know WHICH
        /// setting changed, never what it now holds.
        /// </remarks>
        private static string Describe(Setting setting) =>
            setting == null
                ? "(none)"
                : $"{setting.EntityName}:{setting.EntityId}/{setting.SettingName} (SettingId {setting.SettingId}, IsPrivate {setting.IsPrivate})";
    }
}
