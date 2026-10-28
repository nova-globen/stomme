using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using Stomme.Shared;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Stomme.Infrastructure;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class SyncController : Controller
    {
        private readonly ISyncManager _syncManager;
        private readonly ITenantManager _tenantManager;

        public SyncController(ISyncManager syncManager, ITenantManager tenantManager)
        {
            _syncManager = syncManager;
            _tenantManager = tenantManager;
        }

        // GET api/<controller>/yyyyMMddHHmmssfff
        [HttpGet("{lastSyncDate}")]
        public async Task<Sync> Get(string lastSyncDate)
        {
            var alias = await _tenantManager.GetAliasAsync();
            DateTime currentdate = DateTime.UtcNow;
            DateTime lastdate = DateTime.ParseExact(lastSyncDate, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            if (lastdate == DateTime.MinValue)
            {
                lastdate = currentdate;
            }
            Sync sync = new Sync
            {
                SyncDate = currentdate,
                SyncEvents = _syncManager.GetSyncEvents(alias.TenantId, lastdate)
            };
            return sync;
        }        
    }
}
