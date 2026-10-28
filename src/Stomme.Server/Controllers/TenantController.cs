using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Shared;
using Stomme.Repository;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class TenantController : Controller
    {
        private readonly ITenantRepository _tenants;

        public TenantController(ITenantRepository tenants)
        {
            _tenants = tenants;
        }

        // GET: api/<controller>
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<Tenant>> Get()
        {
            return await _tenants.GetTenantsAsync();
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Tenant> Get(int id)
        {
            return await _tenants.GetTenantAsync(id);
        }
    }
}
