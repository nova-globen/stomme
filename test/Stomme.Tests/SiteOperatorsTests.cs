#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-177 - a site's operators are its Administrators AND the Host Users, once each, in ascending order: a fresh install's
    /// site has no administrator, so an alert to administrators alone reached nobody. The installed-host case is the metering
    /// drive's (<see cref="MeteringQuotaNotificationTests"/>).
    /// </summary>
    public sealed class SiteOperatorsTests
    {
        [Fact]
        public async Task A_sites_operators_are_its_administrators_and_the_host_users_once_each()
        {
            var roles = new ScriptedUserRoles { [RoleNames.Admin] = new[] { 9, 4 }, [RoleNames.Host] = new[] { 1, 4 } };

            Assert.Equal(new[] { 1, 4, 9 }, await roles.GetSiteOperatorIdsAsync(3));
            Assert.Equal(new[] { (RoleNames.Admin, 3), (RoleNames.Host, 3) }, roles.Asked);
        }

        [Fact]
        public async Task A_site_with_no_administrator_still_has_the_host()
        {
            var roles = new ScriptedUserRoles { [RoleNames.Host] = new[] { 1 } };
            Assert.Equal(new[] { 1 }, await roles.GetSiteOperatorIdsAsync(3));
        }

        [Fact]
        public async Task A_site_with_neither_has_no_operator()
        {
            Assert.Empty(await new ScriptedUserRoles().GetSiteOperatorIdsAsync(3));
        }

        private sealed class ScriptedUserRoles : Dictionary<string, int[]>, IUserRoleRepository
        {
            public List<(string Role, int SiteId)> Asked { get; } = new();

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId)
            {
                Asked.Add((roleName, siteId));
                return Task.FromResult<IEnumerable<UserRole>>((TryGetValue(roleName, out var ids) ? ids : Array.Empty<int>()).Select(id => new UserRole { UserId = id }).ToList());
            }

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId) => throw new NotSupportedException();
            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId) => throw new NotSupportedException();
            public Task<UserRole> AddUserRoleAsync(UserRole userRole) => throw new NotSupportedException();
            public Task<UserRole> UpdateUserRoleAsync(UserRole userRole) => throw new NotSupportedException();
            public Task<UserRole> GetUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task<UserRole> GetUserRoleAsync(int userRoleId, bool tracking) => throw new NotSupportedException();
            public Task<UserRole> GetUserRoleAsync(int userId, int roleId) => throw new NotSupportedException();
            public Task<UserRole> GetUserRoleAsync(int userId, int roleId, bool tracking) => throw new NotSupportedException();
            public Task DeleteUserRoleAsync(int userRoleId) => throw new NotSupportedException();
            public Task DeleteUserRolesAsync(int userId) => throw new NotSupportedException();
        }
    }
}
