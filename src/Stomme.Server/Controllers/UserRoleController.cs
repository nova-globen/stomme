using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Repository;
using System.Linq;
using System.Net;
using Stomme.Security;
using System;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class UserRoleController : Controller
    {
        private readonly IUserRoleRepository _userRoles;
        private readonly IRoleRepository _roles;
        private readonly IUserPermissions _userPermissions;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;
        private readonly Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder _identityAudit;

        public UserRoleController(IUserRoleRepository userRoles, IRoleRepository roles, IUserPermissions userPermissions, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger,
            Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder identityAudit = null)
        {
            _identityAudit = identityAudit;
            _userRoles = userRoles;
            _roles = roles;
            _userPermissions = userPermissions;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x&userid=y&rolename=z
        [HttpGet]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<IEnumerable<UserRole>> Get(string siteid, string userid = null, string rolename = null)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            int UserId = -1;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId && (userid != null && int.TryParse(userid, out UserId) || rolename != null))
            {
                if (await IsAuthorizedAsync(UserId, rolename, SiteId))
                {
                    var userroles = (await _userRoles.GetUserRolesAsync(SiteId)).ToList();
                    if (UserId != -1)
                    {
                        userroles = userroles.Where(item => item.UserId == UserId).ToList();
                    }
                    if (rolename != null)
                    {
                        userroles = userroles.Where(item => item.Role.Name == rolename).ToList();
                    }
                    var user = await _userPermissions.GetUserAsync();
                    for (int i = 0; i < userroles.Count(); i++)
                    {
                        userroles[i] = await FilterAsync(userroles[i], user.UserId);
                    }
                    return userroles.OrderBy(u => u.User.DisplayName);

                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UserRole Get Attempt For Site {SiteId} User {UserId} Role {RoleName}", siteid, userid, rolename);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UserRole Get Attempt For Site {SiteId} User {UserId} Role {RoleName}", siteid, userid, rolename);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<UserRole> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var userrole = await _userRoles.GetUserRoleAsync(id);
            if (userrole != null && SiteValid(userrole.Role.SiteId, alias) && await IsAuthorizedAsync(userrole.UserId, userrole.Role.Name, userrole.Role.SiteId ?? -1))
            {
                return await FilterAsync(userrole, (await _userPermissions.GetUserAsync()).UserId);
            }
            else
            {
                if (userrole != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Role Get Attempt {UserRoleId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        private async Task<bool> IsAuthorizedAsync(int userId, string roleName, int siteId)
        {
            bool authorized = true;
            if (userId != -1)
            {
                authorized = ((await _userPermissions.GetUserAsync(User)).UserId == userId);
            }
            if (authorized && !string.IsNullOrEmpty(roleName))
            {
                authorized = User.IsInRole(roleName);
            }
            if (!authorized)
            {
                authorized = await _userPermissions.IsAuthorizedAsync(User, siteId, EntityNames.UserRole, -1, PermissionNames.Write, RoleNames.Admin);
            }
            return authorized;
        }

        private async Task<UserRole> FilterAsync(UserRole userrole, int userid)
        {
            // include all properties if authorized
            if (await _userPermissions.IsAuthorizedAsync(User, userrole.User.SiteId, EntityNames.UserRole, -1, PermissionNames.Write, RoleNames.Admin))
            {
                return userrole;
            }
            else
            {
                // clone object to avoid mutating cache
                UserRole filtered = null;

                if (userrole != null)
                {
                    filtered = new UserRole();

                    // include public properties
                    filtered.UserRoleId = userrole.UserRoleId;
                    filtered.UserId = userrole.UserId;
                    filtered.RoleId = userrole.RoleId;

                    filtered.User = new User();
                    filtered.User.SiteId = userrole.User.SiteId;
                    filtered.User.UserId = userrole.User.UserId;
                    filtered.User.Username = userrole.User.Username;
                    filtered.User.DisplayName = userrole.User.DisplayName;

                    filtered.Role = new Role();
                    filtered.Role.SiteId = userrole.Role.SiteId;
                    filtered.Role.RoleId = userrole.Role.RoleId;
                    filtered.Role.Name = userrole.Role.Name;
                }

                return filtered;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Policy = $"{EntityNames.UserRole}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<UserRole> Post([FromBody] UserRole userRole)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var role = await _roles.GetRoleAsync(userRole.RoleId);
            if (ModelState.IsValid && role != null && SiteValid(role.SiteId, alias) && RoleValid(role.Name))
            {
                userRole = await _userRoles.AddUserRoleAsync(userRole);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UserRole, userRole.UserRoleId, SyncEventActions.Create);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.User, userRole.UserId, SyncEventActions.Reload);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "User Role Added {UserRole}", userRole);
                await AuditRoleAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.RoleGranted, userRole.UserId, role.Name, alias.SiteId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UserRole Post Attempt {UserRole}", userRole);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                userRole = null;
            }
            return userRole;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Policy = $"{EntityNames.UserRole}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<UserRole> Put(int id, [FromBody] UserRole userRole)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var role = await _roles.GetRoleAsync(userRole.RoleId);
            if (ModelState.IsValid && role != null && SiteValid(role.SiteId, alias) && RoleValid(role.Name) && userRole.UserRoleId == id && await _userRoles.GetUserRoleAsync(userRole.UserRoleId, false) != null)
            {
                userRole = await _userRoles.UpdateUserRoleAsync(userRole);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UserRole, userRole.UserRoleId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.User, userRole.UserId, SyncEventActions.Reload);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "User Role Updated {UserRole}", userRole);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Role Put Attempt {UserRole}", userRole);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                userRole = null;
            }
            return userRole;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Policy = $"{EntityNames.UserRole}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            UserRole userRole = await _userRoles.GetUserRoleAsync(id);
            if (userRole != null && SiteValid(userRole.Role.SiteId, alias) && RoleValid(userRole.Role.Name))
            {
                await _userRoles.DeleteUserRoleAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UserRole, userRole.UserRoleId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.User, userRole.UserId, SyncEventActions.Reload);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Role Deleted {UserRole}", userRole);
                await AuditRoleAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.RoleRevoked, userRole.UserId, userRole.Role.Name, alias.SiteId);

                if (userRole.Role.Name == RoleNames.Host)
                {
                    // add site specific user roles to preserve user access
                    var role = (await _roles.GetRolesAsync(alias.SiteId)).FirstOrDefault(item => item.Name == RoleNames.Registered);
                    userRole = await _userRoles.AddUserRoleAsync(new UserRole { UserId = userRole.UserId, RoleId = role.RoleId, EffectiveDate = null, ExpiryDate = null });
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "User Role Added {UserRole}", userRole);
                    role = (await _roles.GetRolesAsync(alias.SiteId)).FirstOrDefault(item => item.Name == RoleNames.Admin);
                    userRole = await _userRoles.AddUserRoleAsync(new UserRole { UserId = userRole.UserId, RoleId = role.RoleId, EffectiveDate = null, ExpiryDate = null });
                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "User Role Added {UserRole}", userRole);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Role Delete Attempt {UserRoleId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // BL-120: a grant or a revoke is an identity audit line - whose account, which role, by whom (the recorder derives the actor).
        private Task AuditRoleAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent evt, int userId, string roleName, int siteId) =>
            _identityAudit?.RecordAsync(evt, userId, null, roleName, siteId) ?? Task.CompletedTask;

        private bool SiteValid(int? SiteId, Alias alias)
        {
            return (SiteId == alias.SiteId || (SiteId == null && User.IsInRole(RoleNames.Host)));
        }

        private bool RoleValid(string RoleName)
        {
            return (RoleName != RoleNames.Host || User.IsInRole(RoleNames.Host));
        }
    }
}
