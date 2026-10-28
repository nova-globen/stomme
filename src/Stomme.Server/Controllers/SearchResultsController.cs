using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class SearchResultsController : Controller
    {
        private readonly ISearchService _searchService;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public SearchResultsController(ISearchService searchService, ILogManager logger, ITenantManager tenantManager)
        {
            _searchService = searchService;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        [HttpPost]
        public async Task<SearchResults> Post([FromBody] SearchQuery searchQuery)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && searchQuery.SiteId == alias.SiteId)
            {
                return await _searchService.GetSearchResultsAsync(searchQuery);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Search Results Post Attempt {SearchQuery}", searchQuery);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }
    }
}
