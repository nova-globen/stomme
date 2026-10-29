using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Pins the decision rules of <see cref="PermissionRowAccessGuard"/> against a
    /// recording <see cref="IUserPermissions"/> stub — and, for the "no rows" rule, against the REAL
    /// <see cref="UserPermissions"/> over an empty permission repository, because "deny when no rows" is a
    /// property of the overload chosen, not of the stub.
    /// </summary>
    public class PermissionRowAccessGuardTests
    {
        private const int SiteId = 7;

        [Fact]
        public async Task Rows_present_and_granted_allows_and_asks_the_five_argument_overload_for_the_resolved_site()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var guard = new PermissionRowAccessGuard(Request(Principal(RoleNames.Registered)), permissions);

            Assert.True(await guard.IsAllowedAsync(null, "gatetest:manage"));

            var call = Assert.Single(permissions.Calls);
            Assert.Equal((SiteId, "gatetest", PermissionRowAccessGuard.KeyEntityId, "manage"), call);
        }

        [Fact]
        public async Task Rows_present_and_denied_denies()
        {
            var permissions = new RecordingUserPermissions(answer: false);
            var guard = new PermissionRowAccessGuard(Request(Principal(RoleNames.Registered)), permissions);

            Assert.False(await guard.IsAllowedAsync(null, "gatetest:manage"));
            Assert.Single(permissions.Calls);
        }

        // The load-bearing rule ("deny when no rows exist for a key"). Driven through the REAL
        // UserPermissions so the choice of overload is what is under test: the six-argument overload falls
        // back to role membership when no rows exist, which would admit this Administrator; the
        // five-argument one evaluates the (empty) rows and denies.
        [Fact]
        public async Task No_rows_denies_even_an_administrator_because_there_is_no_roles_fallback()
        {
            var accessor = Request(Principal(RoleNames.Admin));
            var real = new UserPermissions(new StubPermissionRepository(), new StubUserRoleRepository(), accessor);
            var guard = new PermissionRowAccessGuard(accessor, real);

            Assert.False(await guard.IsAllowedAsync(null, "gatetest:manage"));
        }

        // The instrument validated in the other direction: the same real evaluator over a seeded
        // Administrators row admits the Administrator. If this were red, the test above would prove nothing.
        [Fact]
        public async Task A_seeded_administrators_row_admits_an_administrator_through_the_real_evaluator()
        {
            var accessor = Request(Principal(RoleNames.Admin));
            var repository = new StubPermissionRepository(
                new Permission(SiteId, "gatetest", PermissionRowAccessGuard.KeyEntityId, "manage", RoleNames.Admin, null, true));
            var real = new UserPermissions(repository, new StubUserRoleRepository(), accessor);
            var guard = new PermissionRowAccessGuard(accessor, real);

            Assert.True(await guard.IsAllowedAsync(null, "gatetest:manage"));

            // And the row set granting only a custom role denies the Administrator (F1: no Host/Admin
            // short-circuit anywhere in the decision).
            var custom = new StubPermissionRepository(
                new Permission(SiteId, "gatetest", PermissionRowAccessGuard.KeyEntityId, "manage", "GateRole", null, true));
            var guardOverCustom = new PermissionRowAccessGuard(accessor, new UserPermissions(custom, new StubUserRoleRepository(), accessor));
            Assert.False(await guardOverCustom.IsAllowedAsync(null, "gatetest:manage"));
        }

        [Fact]
        public async Task The_key_is_split_at_the_last_colon_and_lower_cased()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var guard = new PermissionRowAccessGuard(Request(Principal(RoleNames.Registered)), permissions);

            Assert.True(await guard.IsAllowedAsync(null, "Platform.Modules:Manage"));

            var call = Assert.Single(permissions.Calls);
            Assert.Equal("platform.modules", call.EntityName);
            Assert.Equal("manage", call.PermissionName);
        }

        [Theory]
        [InlineData("nocolon")]
        [InlineData(":manage")]
        [InlineData("gatetest:")]
        [InlineData("   ")]
        public async Task A_malformed_key_is_denied_without_consulting_the_rows(string key)
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var guard = new PermissionRowAccessGuard(Request(Principal(RoleNames.Admin)), permissions);

            // Whitespace-only is "no requirement" by the IAccessGuard contract; everything else is malformed.
            var expected = string.IsNullOrWhiteSpace(key);
            Assert.Equal(expected, await guard.IsAllowedAsync(null, key));
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task No_HttpContext_denies_a_gated_key_without_consulting_the_rows()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var guard = new PermissionRowAccessGuard(new HttpContextAccessor(), permissions);

            Assert.False(await guard.IsAllowedAsync(null, "gatetest:manage"));
            Assert.False(await guard.IsAllowedAsync("some-feature", null, CancellationToken.None));
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task No_requirement_at_all_is_allowed_even_outside_a_request()
        {
            var permissions = new RecordingUserPermissions(answer: false);
            var guard = new PermissionRowAccessGuard(new HttpContextAccessor(), permissions);

            Assert.True(await guard.IsAllowedAsync(null, null));
            Assert.True(await guard.IsAllowedAsync("", ""));
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task System_admin_is_host_membership_not_a_row()
        {
            var permissions = new RecordingUserPermissions(answer: true);

            var host = new PermissionRowAccessGuard(Request(Principal(RoleNames.Host, RoleNames.Admin)), permissions);
            Assert.True(await host.IsAllowedAsync(null, "system:admin"));

            var admin = new PermissionRowAccessGuard(Request(Principal(RoleNames.Admin)), permissions);
            Assert.False(await admin.IsAllowedAsync(null, "System:Admin"));

            // Neither consulted the rows — the stub would have said yes.
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task The_feature_axis_is_denied_without_an_evaluator_and_follows_the_evaluator_with_one()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var accessor = Request(Principal(RoleNames.Admin));

            var withoutEvaluator = new PermissionRowAccessGuard(accessor, permissions);
            Assert.False(await withoutEvaluator.IsAllowedAsync("beta", null));

            var on = new PermissionRowAccessGuard(accessor, permissions, new StubFeatureEvaluator(enabled: true));
            Assert.True(await on.IsAllowedAsync("beta", null));

            var off = new PermissionRowAccessGuard(accessor, permissions, new StubFeatureEvaluator(enabled: false));
            Assert.False(await off.IsAllowedAsync("beta", null));

            // Both axes are ANDed: a held permission does not rescue an off feature, and vice versa.
            Assert.False(await off.IsAllowedAsync("beta", "gatetest:manage"));
            Assert.True(await on.IsAllowedAsync("beta", "gatetest:manage"));
        }

        [Fact]
        public async Task A_denied_permission_short_circuits_before_the_feature_is_evaluated()
        {
            var features = new StubFeatureEvaluator(enabled: true);
            var guard = new PermissionRowAccessGuard(Request(Principal(RoleNames.Admin)), new RecordingUserPermissions(answer: false), features);

            Assert.False(await guard.IsAllowedAsync("beta", "gatetest:manage"));
            Assert.Equal(0, features.Evaluations);
        }

        [Fact]
        public async Task The_member_overload_requires_every_declared_attribute()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var accessor = Request(Principal(RoleNames.Admin));

            var featureOn = new PermissionRowAccessGuard(accessor, permissions, new StubFeatureEvaluator(enabled: true));
            Assert.True(await featureOn.IsAllowedAsync(typeof(DoublyGated)));
            Assert.True(await featureOn.IsAllowedAsync(typeof(Ungated)));

            var featureOff = new PermissionRowAccessGuard(accessor, permissions, new StubFeatureEvaluator(enabled: false));
            Assert.False(await featureOff.IsAllowedAsync(typeof(DoublyGated)));
            Assert.True(await featureOff.IsAllowedAsync(typeof(Ungated)));

            // A method folds its declaring type's gate in (class-level AND method-level).
            var method = typeof(DoublyGated).GetMethod(nameof(DoublyGated.Run))!;
            Assert.False(await featureOff.IsAllowedAsync(method));
        }

        [Requires("gatetest:manage")]
        [Requires(Feature = "beta")]
        private sealed class DoublyGated
        {
            public void Run()
            {
            }
        }

        private sealed class Ungated
        {
        }

        private static ClaimsPrincipal Principal(params string[] roles)
        {
            var identity = new ClaimsIdentity(Constants.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "5"));
            identity.AddClaim(new Claim(Constants.SiteKeyClaimType, "1:" + SiteId));
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            return new ClaimsPrincipal(identity);
        }

        private static IHttpContextAccessor Request(ClaimsPrincipal principal)
        {
            var context = new DefaultHttpContext { User = principal };
            context.Items[Constants.HttpContextAliasKey] = new Alias { SiteId = SiteId, TenantId = 1 };
            return new HttpContextAccessor { HttpContext = context };
        }

        /// <summary>
        /// Records the five-argument (no roles fallback) calls; the six-argument overload — the one that
        /// falls back to role membership when no rows exist — THROWS, so a guard that reaches for it is red.
        /// </summary>
        private sealed class RecordingUserPermissions : IUserPermissions
        {
            public RecordingUserPermissions(bool answer)
            {
                Answer = answer;
            }

            public bool Answer { get; }

            public List<(int SiteId, string EntityName, int EntityId, string PermissionName)> Calls { get; } = new();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName)
            {
                Calls.Add((siteId, entityName, entityId, permissionName));
                return Task.FromResult(Answer);
            }

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName, string roles)
                => throw new InvalidOperationException("The roles-fallback overload must not be used for a permission key: it admits by role membership when no rows exist.");

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, List<Permission> permissions) => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, string permissions) => throw new NotSupportedException();

            public Task<User> GetUserAsync(ClaimsPrincipal user) => throw new NotSupportedException();

            public Task<User> GetUserAsync() => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string entityName, int entityId, string permissionName) => throw new NotSupportedException();
        }

        private sealed class StubFeatureEvaluator : IFeatureEvaluator
        {
            private readonly bool _enabled;

            public StubFeatureEvaluator(bool enabled)
            {
                _enabled = enabled;
            }

            public int Evaluations { get; private set; }

            public Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
            {
                Evaluations++;
                return Task.FromResult(_enabled);
            }
        }

        /// <summary>An in-memory permission repository: only the read overloads the evaluator uses are live.</summary>
        private sealed class StubPermissionRepository : IPermissionRepository
        {
            private readonly List<Permission> _rows;

            public StubPermissionRepository(params Permission[] rows)
            {
                _rows = rows.ToList();
            }

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName)
                => Task.FromResult(_rows.Where(p => p.SiteId == siteId && p.EntityName == entityName));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName)
                => Task.FromResult(_rows.Where(p => p.SiteId == siteId && p.EntityName == entityName && p.PermissionName == permissionName));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId)
                => Task.FromResult(_rows.Where(p => p.SiteId == siteId && p.EntityName == entityName && p.EntityId == entityId));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName)
                => Task.FromResult(_rows.Where(p => p.SiteId == siteId && p.EntityName == entityName && p.EntityId == entityId && p.PermissionName == permissionName));

            public Task<Permission> AddPermissionAsync(Permission permission) => throw new NotSupportedException();

            public Task<Permission> UpdatePermissionAsync(Permission permission) => throw new NotSupportedException();

            public Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false) => throw new NotSupportedException();

            public Task<Permission> GetPermissionAsync(int permissionId) => throw new NotSupportedException();

            public Task DeletePermissionAsync(int permissionId) => throw new NotSupportedException();

            public Task DeletePermissionsAsync(int siteId, string entityName, int entityId) => throw new NotSupportedException();
        }

        /// <summary>No effective-date rows: every role claim on the principal counts, as on a stock site.</summary>
        private sealed class StubUserRoleRepository : IUserRoleRepository
        {
            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId) => Task.FromResult(Enumerable.Empty<UserRole>());

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId) => Task.FromResult(Enumerable.Empty<UserRole>());

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId) => Task.FromResult(Enumerable.Empty<UserRole>());

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
