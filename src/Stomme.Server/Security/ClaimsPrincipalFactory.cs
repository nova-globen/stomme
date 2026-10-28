using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Threading.Tasks;
using Stomme.Models;
using System.Collections.Generic;
using System.Linq;
using Stomme.Repository;
using Stomme.Infrastructure;

namespace Stomme.Security
{
    public class ClaimsPrincipalFactory<TUser> : UserClaimsPrincipalFactory<TUser> where TUser : IdentityUser
    {
        private readonly ITenantManager _tenants;
        // cannot utilize IUserManager due to circular references - which is fine as this method is only called on login
        private readonly IUserRepository _users;
        private readonly IUserRoleRepository _userRoles;
        private readonly UserManager<TUser> _userManager;

        public ClaimsPrincipalFactory(UserManager<TUser> userManager, IOptions<IdentityOptions> optionsAccessor, ITenantManager tenants, IUserRepository users, IUserRoleRepository userroles) : base(userManager, optionsAccessor)
        {
            _tenants = tenants;
            _users = users; 
            _userRoles = userroles;
            _userManager = userManager;
        }

        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(TUser identityuser)
        {
            var identity = await base.GenerateClaimsAsync(identityuser);

            User user = await _users.GetUserAsync(identityuser.UserName);
            if (user != null)
            {
                Alias alias = await _tenants.GetAliasAsync();
                if (alias != null)
                {
                    user.SecurityStamp = await _userManager.GetSecurityStampAsync(identityuser);
                    List<UserRole> userroles = (await _userRoles.GetUserRolesAsync(user.UserId, alias.SiteId)).ToList();
                    identity = UserSecurity.CreateClaimsIdentity(alias, user, userroles);
                }
            }

            return identity;
        }
    }

}
