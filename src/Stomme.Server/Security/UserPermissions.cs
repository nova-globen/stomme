using Microsoft.AspNetCore.Http;
using Stomme.Models;
using System.Linq;
using System.Security.Claims;
using Stomme.Repository;
using Stomme.Extensions;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Shared;

namespace Stomme.Security
{
    public interface IUserPermissions
    {
        Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName, string roles);
        Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName);
        Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, List<Permission> permissions);
        Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, string permissions);
        Task<User> GetUserAsync(ClaimsPrincipal user);
        Task<User> GetUserAsync();

        [Obsolete("IsAuthorizedAsync(ClaimsPrincipal principal, string entityName, int entityId, string permissionName) is deprecated. Use IsAuthorizedAsync(ClaimsPrincipal principal, int siteId, string entityName, int entityId, string permissionName) instead.", false)]
        Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string entityName, int entityId, string permissionName);
    }

    public class UserPermissions : IUserPermissions
    {
        private readonly IPermissionRepository _permissions;
        private readonly IUserRoleRepository _userRoles;
        private readonly IHttpContextAccessor _accessor;

        public UserPermissions(IPermissionRepository permissions, IUserRoleRepository userRoles, IHttpContextAccessor accessor)
        {
            _permissions = permissions;
            _userRoles = userRoles;
            _accessor = accessor;
        }

        public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, int siteId, string entityName, int entityId, string permissionName, string roles)
        {
            var permissions = (await _permissions.GetPermissionsAsync(siteId, entityName, entityId, permissionName)).ToList();
            if (permissions != null && permissions.Count != 0)
            {
                return await IsAuthorizedAsync(principal, permissionName, permissions.ToList());
            }
            else
            {
                return UserSecurity.IsAuthorized(await GetUserAsync(principal), roles);
            }
        }

        public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, int siteId, string entityName, int entityId, string permissionName)
        {
            return await IsAuthorizedAsync(principal, permissionName, (await _permissions.GetPermissionsAsync(siteId, entityName, entityId, permissionName)).ToList());
        }

        public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string permissionName, List<Permission> permissionList)
        {
            return UserSecurity.IsAuthorized(await GetUserAsync(principal), permissionName, permissionList);
        }

        public async Task<User> GetUserAsync(ClaimsPrincipal principal)
        {
            User user = new User();
            user.IsAuthenticated = false;
            user.Username = "";
            user.UserId = -1;
            user.Roles = "";

            if (principal == null) return user;

            user.IsAuthenticated = principal.Identity.IsAuthenticated;
            if (user.IsAuthenticated)
            {
                user.Username = principal.Identity.Name;
                user.UserId = principal.UserId();

                // include roles
                var userRoles = (await _userRoles.GetUserRolesAsync(user.UserId, principal.SiteId())).ToList();
                foreach (var roleName in principal.Roles())
                {
                    var role = userRoles.FirstOrDefault(item => item.Role.Name == roleName);
                    if (role != null)
                    {
                        if (Utilities.IsEffectiveAndNotExpired(role.EffectiveDate,role.ExpiryDate))
                        {
                            user.Roles += roleName + ";";
                        }
                    }
                    else
                    {
                        user.Roles += roleName + ";";
                    }
                }
                if (user.Roles != "") user.Roles = ";" + user.Roles;
            }
            return user;
        }

        public async Task<User> GetUserAsync()
        {
            if (_accessor.HttpContext != null)
            {
                return await GetUserAsync(_accessor.HttpContext.User);
            }
            else
            {
                return null;
            }
        }

        // deprecated
        public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string entityName, int entityId, string permissionName)
        {
            return await IsAuthorizedAsync(principal, permissionName, (await _permissions.GetPermissionsAsync(_accessor.HttpContext.GetAlias().SiteId, entityName, entityId, permissionName)).ToList());
        }

        [Obsolete("IsAuthorizedAsync(ClaimsPrincipal principal, string permissionName, string permissions) is deprecated. Use IsAuthorizedAsync(ClaimsPrincipal principal, string permissionName, List<Permission> permissionList) instead", false)]
        public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string permissionName, string permissions)
        {
            return UserSecurity.IsAuthorized(await GetUserAsync(principal), permissionName, JsonSerializer.Deserialize<List<Permission>>(permissions));
        }
    }
}
