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
    public class MigrationHistoryController : Controller
    {
        private readonly IMigrationHistoryRepository _history;

        public MigrationHistoryController(IMigrationHistoryRepository history)
        {
            _history = history;
        }

        // GET: api/<controller>
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<MigrationHistory>> Get()
        {
            return await _history.GetMigrationHistoryAsync();
        }
    }
}
