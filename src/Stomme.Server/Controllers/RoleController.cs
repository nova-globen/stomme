using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Repository;
using System.Net;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class RoleController : Controller
    {
        private readonly IRoleRepository _roles;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public RoleController(IRoleRepository roles, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _roles = roles;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x&global=true/false
        [HttpGet]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<IEnumerable<Role>> Get(string siteid, string global)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                if (string.IsNullOrEmpty(global))
                {
                    global = "False";
                }
                return await _roles.GetRolesAsync(SiteId, bool.Parse(global));
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Role Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Role> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var role = await _roles.GetRoleAsync(id);
            if (role != null && (role.SiteId == alias.SiteId || User.IsInRole(RoleNames.Host)))
            {
                return role;
            }
            else
            {
                if (role != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Role Get Attempt {RoleId}", id);
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
        [Authorize(Policy = $"{EntityNames.Role}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<Role> Post([FromBody] Role role)
        {
            var alias = await _tenantManager.GetAliasAsync();
            // A blank or whitespace name is not a role. The client marks the input required, but the
            // server accepted one, so an empty row could be created through the API and rendered as a nameless role.
            if (string.IsNullOrWhiteSpace(role?.Name))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "Role Create Rejected - The Name Is Blank");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            if (ModelState.IsValid && role.SiteId == alias.SiteId)
            {
                role = await _roles.AddRoleAsync(role);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Role, role.RoleId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Role Added {Role}", role);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Role Post Attempt {Role}", role);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                role = null;
            }
            return role;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Policy = $"{EntityNames.Role}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<Role> Put(int id, [FromBody] Role role)
        {
            var alias = await _tenantManager.GetAliasAsync();
            // A blank or whitespace name is not a role. The client marks the input required, but the
            // server accepted one, so an empty row could be created through the API and rendered as a nameless role.
            if (string.IsNullOrWhiteSpace(role?.Name))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Update, "Role Update Rejected - The Name Is Blank");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            if (ModelState.IsValid && role.SiteId == alias.SiteId && role.RoleId == id && await _roles.GetRoleAsync(role.RoleId, false) != null)
            {
                role = await _roles.UpdateRoleAsync(role);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Role, role.RoleId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Role Updated {Role}", role);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Role Put Attempt {Role}", role);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                role = null;
            }
            return role;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Policy = $"{EntityNames.Role}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var role = await _roles.GetRoleAsync(id);
            if (role != null && !role.IsSystem && role.SiteId == alias.SiteId)
            {
                await _roles.DeleteRoleAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Role, role.RoleId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Role Deleted {RoleId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Role Delete Attempt {RoleId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
