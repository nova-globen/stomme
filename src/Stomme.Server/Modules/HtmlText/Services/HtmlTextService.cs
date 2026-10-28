using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Stomme.Documentation;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.HtmlText.Services
{
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class ServerHtmlTextService : IHtmlTextService, ITransientService
    {
        private readonly IHtmlTextRepository _htmlTextRepository;
        private readonly IUserPermissions _userPermissions;
        private readonly ICacheManager _cache;
        private readonly ILogManager _logger;
        private readonly IHttpContextAccessor _accessor;
        private readonly ITenantManager _tenantManager;

        public ServerHtmlTextService(IHtmlTextRepository htmlTextRepository, IUserPermissions userPermissions, ICacheManager cache, ITenantManager tenantManager, ILogManager logger, IHttpContextAccessor accessor)
        {
            _htmlTextRepository = htmlTextRepository;
            _userPermissions = userPermissions;
            _cache = cache;
            _logger = logger;
            _accessor = accessor;
            // the alias is resolved per call rather than cached here: GetAliasAsync reaches the store on a
            // miss and a constructor cannot await. It memoizes onto SiteState for the scope, so the resolved
            // alias is the same object this field used to hold.
            _tenantManager = tenantManager;
        }

        public async Task<List<Models.HtmlText>> GetHtmlTextsAsync(int moduleId)
        {
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Registered))
            {
                return (await _htmlTextRepository.GetHtmlTextsAsync(moduleId)).ToList();
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Html/Text Get Attempt {ModuleId}", moduleId);
                return null;
            }
        }

        public async Task<Models.HtmlText> GetHtmlTextAsync(int moduleId)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (await _userPermissions.IsAuthorizedAsync(_accessor.HttpContext.User, alias.SiteId, EntityNames.Module, moduleId, PermissionNames.View))
            {
                return await _cache.GetCacheAsync(alias, $"HtmlText:{moduleId}", async entry =>
                {
                    return await _htmlTextRepository.GetHtmlTextAsync(moduleId);
                });
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Html/Text Get Attempt {ModuleId}", moduleId);
                return null;
            }
        }

        public async Task<Models.HtmlText> AddHtmlTextAsync(Models.HtmlText htmlText)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (await _userPermissions.IsAuthorizedAsync(_accessor.HttpContext.User, alias.SiteId, EntityNames.Module, htmlText.ModuleId, PermissionNames.Edit))
            {
                htmlText = await _htmlTextRepository.AddHtmlTextAsync(htmlText);
                await ClearCacheAsync(alias, htmlText.ModuleId);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Html/Text Added {HtmlText}", htmlText);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Html/Text Add Attempt {HtmlText}", htmlText);
                htmlText = null;
            }
            return htmlText;
        }

        public async Task DeleteHtmlTextAsync(int htmlTextId, int moduleId)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (await _userPermissions.IsAuthorizedAsync(_accessor.HttpContext.User, alias.SiteId, EntityNames.Module, moduleId, PermissionNames.Edit))
            {
                await _htmlTextRepository.DeleteHtmlTextAsync(htmlTextId);
                await ClearCacheAsync(alias, moduleId);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Html/Text Deleted {HtmlTextId}", htmlTextId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Html/Text Delete Attempt {HtmlTextId} {ModuleId}", htmlTextId, moduleId);
            }
        }

        private Task ClearCacheAsync(Alias alias, int moduleId)
        {
            return _cache.RemoveCacheAsync(alias, $"HtmlText:{moduleId}");
        }
    }
}
