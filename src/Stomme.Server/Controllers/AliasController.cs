using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using Stomme.Shared;
using System.Net;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Repository;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class AliasController : Controller
    {
        private readonly IAliasRepository _aliases;
        private readonly ITenantRepository _tenants;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public AliasController(IAliasRepository aliases, ITenantRepository tenants, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _aliases = aliases;
            _tenants = tenants;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<Alias>> Get()
        {
            var alias = await _tenantManager.GetAliasAsync();
            var aliases = await _aliases.GetAliasesAsync();
            if (!User.IsInRole(RoleNames.Host))
            {
                aliases = aliases.Where(item => item.SiteId == alias.SiteId && item.TenantId == alias.TenantId);
            }
            return aliases;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Alias> Get(int id)
        {
            return await _aliases.GetAliasAsync(id);
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Alias> Post([FromBody] Alias alias)
        {
            if (ModelState.IsValid)
            {
                alias = await _aliases.AddAliasAsync(alias);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Alias, alias.AliasId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Alias Added {Alias}", alias);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Alias Post Attempt {Alias}", alias);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                alias = null;
            }
            return alias;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Alias> Put(int id, [FromBody] Alias alias)
        {
            if (ModelState.IsValid && alias.AliasId == id && await _aliases.GetAliasAsync(alias.AliasId, false) != null)
            {
                alias = await _aliases.UpdateAliasAsync(alias);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Alias, alias.AliasId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Alias Updated {Alias}", alias);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Alias Put Attempt {Alias}", alias);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                alias = null;
            }
            return alias;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Delete(int id)
        {
            var alias = await _aliases.GetAliasAsync(id);
            if (alias != null)
            {
                // BL-100 T1: the Master tenant's LAST alias is never deleted. TenantRepository already refuses
                // to delete the Master tenant row, but deleting its only alias left the installation with no alias at
                // all - the host name then answered "Site Not Configured Correctly - No Matching Alias Exists" and the
                // alias auto-registration cannot fire with zero aliases (measured, MasterTenantAliasGuardTests).
                var tenant = await _tenants.GetTenantAsync(alias.TenantId);
                if (tenant != null && tenant.Name.Equals(TenantNames.Master, StringComparison.OrdinalIgnoreCase)
                    && !(await _aliases.GetAliasesAsync()).Any(item => item.TenantId == alias.TenantId && item.AliasId != alias.AliasId))
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Refused Deleting The Last Alias Of The Master Tenant {AliasId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                await _aliases.DeleteAliasAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Alias, alias.AliasId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Alias Deleted {AliasId}", id);

                var aliases = await _aliases.GetAliasesAsync();
                if (!aliases.Any(item => item.TenantId == alias.TenantId))
                {
                    await _tenants.DeleteTenantAsync(alias.TenantId);

                    // The repository may refuse silently, so the log says what the store now holds, not what was asked.
                    if (await _tenants.GetTenantAsync(alias.TenantId) == null)
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Tenant Deleted {TenantId}", alias.TenantId);
                    }
                    else
                    {
                        _logger.Log(LogLevel.Warning, this, LogFunction.Delete, "Tenant Not Deleted - The Repository Kept The Tenant Row {TenantId}", alias.TenantId);
                    }
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Alias Delete Attempt {AliasId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
