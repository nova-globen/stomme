using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class SiteTemplateController : Controller
    {
        private readonly ISiteTemplateRepository _siteTemplates;

        public SiteTemplateController(ISiteTemplateRepository siteTemplates)
        {
            _siteTemplates = siteTemplates;
        }

        // GET: api/<controller>
        [HttpGet]
        public async Task<IEnumerable<SiteTemplate>> Get()
        {
            return await _siteTemplates.GetSiteTemplatesAsync();
        }
    }
}
