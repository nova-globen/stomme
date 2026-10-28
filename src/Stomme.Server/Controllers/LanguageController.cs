using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class LanguageController : Controller
    {
        private readonly ILanguageRepository _languages;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public LanguageController(ILanguageRepository languages, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _languages = languages;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        [HttpGet]
        public async Task<IEnumerable<Language>> Get(string siteid, string packagename)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && (SiteId == alias.SiteId || SiteId == -1))
            {
                List<Language> languages = new List<Language>();
                if (SiteId == -1)
                {
                    if (!string.IsNullOrEmpty(packagename))
                    {
                        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), $"{packagename}*{Constants.SatelliteAssemblyExtension}", SearchOption.AllDirectories))
                        {
                            var code = Path.GetFileName(Path.GetDirectoryName(file));
                            if (!languages.Any(item => item.Code == code))
                            {
                                languages.Add(new Language { Code = code, Name = CultureInfo.GetCultureInfo(code).DisplayName, Version = FileVersionInfo.GetVersionInfo(file).ProductVersion, IsDefault = false });
                            }
                        }
                    }
                }
                else
                {
                    languages = (await _languages.GetLanguagesAsync(SiteId)).ToList();
                    foreach (Language language in languages)
                    {
                        language.Name = CultureInfo.GetCultureInfo(language.Code).DisplayName;
                    }
                    if (!string.IsNullOrEmpty(packagename))
                    {
                        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), $"{packagename}*{Constants.SatelliteAssemblyExtension}", SearchOption.AllDirectories))
                        {
                            var code = Path.GetFileName(Path.GetDirectoryName(file));
                            if (languages.Any(item => item.Code == code))
                            {
                                languages.Single(item => item.Code == code).Version = FileVersionInfo.GetVersionInfo(file).ProductVersion;
                            }
                        }
                    }
                    var defaultCulture = CultureInfo.GetCultureInfo(Constants.DefaultCulture);
                    languages.Add(new Language { Code = defaultCulture.Name, Name = defaultCulture.DisplayName, Version = Constants.Version, IsDefault = !languages.Any(l => l.IsDefault) });
                }
                return languages.OrderBy(item => item.Name);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Language Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        [HttpGet("{id}")]
        public async Task<Language> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var language = await _languages.GetLanguageAsync(id);
            if (language != null && language.SiteId == alias.SiteId)
            {
                language.Name = CultureInfo.GetCultureInfo(language.Code).DisplayName;
                return language;
            }
            else
            {
                if (language != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Language Get Attempt {LanguageId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        [HttpPut]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task Put([FromBody] Language language)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (HasNoCode(language))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Update, "Language Update Rejected - No Culture Code");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (ModelState.IsValid && language.SiteId == alias.SiteId)
            {
                await _languages.UpdateLanguageAsync(language);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Language, language.LanguageId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Language Updated {Language}", language);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Language Put Attempt {Language}", language);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        [HttpPost]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<Language> Post([FromBody] Language language)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (HasNoCode(language))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "Language Create Rejected - No Culture Code");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            if (ModelState.IsValid && language.SiteId == alias.SiteId)
            {
                language = await _languages.AddLanguageAsync(language);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Language, language.LanguageId, SyncEventActions.Create);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Language Added {Language}", language);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Language Post Attempt {Language}", language);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                language = null;
            }
            return language;
        }

        // BL-143 (the BL-87 class): a language IS its culture code. Blank, whitespace and the Add form's "-" placeholder were
        // all written through the API - measured, 200 and a row each - and a blanked PUT answered an empty 500.
        private static bool HasNoCode(Language language) =>
            string.IsNullOrWhiteSpace(language?.Code) || language.Code.Trim() == "-";

        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var language = await _languages.GetLanguageAsync(id);
            if (language != null && language.SiteId == alias.SiteId)
            {
                await _languages.DeleteLanguageAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Language, language.LanguageId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Language Deleted {LanguageId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Language Delete Attempt {LanguageId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
