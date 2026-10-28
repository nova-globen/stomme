using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Stomme.Models;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class OutputCacheController : Controller
    {
        private readonly IOutputCacheService _cacheService;

        public OutputCacheController(IOutputCacheService cacheService)
        {
            _cacheService = cacheService;
        }

        // DELETE api/<controller>/{tag}
        [HttpDelete("{tag}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task EvictByTag(string tag)
        {
            await _cacheService.EvictByTag(tag);
        }
    }
}
