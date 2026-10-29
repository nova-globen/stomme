using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The CONTROLLER/HUB half of the authorization bridge:
    /// <c>[RequiresPermissionKey("resource:operation")]</c> is decided ONLY by the site's
    /// <c>EntityId = -1</c> Permission rows, through the five-argument <see cref="IUserPermissions"/>
    /// overload - no query-string re-scoping (F8), no roles fallback, no Host short-circuit (F1).
    /// </summary>
    public class PermissionKeyHandlerTests
    {
        private const int SiteId = 7;

        [Fact]
        public async Task A_granting_row_succeeds_and_the_lookup_is_the_minus_one_row_of_the_resolved_site()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var context = Evaluate(out var handler, permissions, Principal(RoleNames.Registered), "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.True(context.HasSucceeded);
            var call = Assert.Single(permissions.Calls);
            Assert.Equal((SiteId, "gatetest", PermissionKeys.KeyEntityId, "manage"), call);
        }

        [Fact]
        public async Task No_granting_row_denies()
        {
            var permissions = new RecordingUserPermissions(answer: false);
            var context = Evaluate(out var handler, permissions, Principal(RoleNames.Admin), "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded);
            Assert.Single(permissions.Calls);
        }

        // F8, closed. The framework's own PermissionHandler reads ?auth{entityname}id= and re-scopes the row
        // lookup to that entity id; a lifted key's rows only ever exist at -1, so obeying the caller would
        // point the lookup at a row set the caller chose. The query string is present here and IGNORED.
        [Fact]
        public async Task A_caller_supplied_auth_entity_id_does_not_re_scope_the_lookup()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var httpContext = Request(Principal(RoleNames.Registered));
            httpContext.Request.QueryString = new QueryString("?authgatetestid=1&entityid=1");

            var handler = new PermissionKeyHandler(new HttpContextAccessor { HttpContext = httpContext }, permissions, new SilentLogManager());
            var context = Context(httpContext.User, "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.True(context.HasSucceeded);
            var call = Assert.Single(permissions.Calls);
            Assert.Equal(PermissionKeys.KeyEntityId, call.EntityId);
        }

        // The instrument in the other direction: the REAL evaluator over an EMPTY permission repository. The
        // six-argument overload would admit this Administrator by role membership; the five-argument one
        // evaluates the (absent) rows and denies. A stub cannot prove this - the overload choice is the test.
        [Fact]
        public async Task No_rows_at_all_denies_an_administrator_because_there_is_no_roles_fallback()
        {
            var accessor = Accessor(Principal(RoleNames.Admin, RoleNames.Host));
            var real = new UserPermissions(new EmptyPermissionRepository(), new NoEffectiveDateUserRoles(), accessor);
            var handler = new PermissionKeyHandler(accessor, real, new SilentLogManager());
            var context = Context(accessor.HttpContext.User, "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded);
        }

        [Fact]
        public async Task A_seeded_administrators_row_admits_an_administrator_through_the_real_evaluator()
        {
            var accessor = Accessor(Principal(RoleNames.Admin));
            var repository = new EmptyPermissionRepository(
                new Permission(SiteId, "gatetest", PermissionKeys.KeyEntityId, "manage", RoleNames.Admin, null, true));
            var handler = new PermissionKeyHandler(accessor, new UserPermissions(repository, new NoEffectiveDateUserRoles(), accessor), new SilentLogManager());
            var context = Context(accessor.HttpContext.User, "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.True(context.HasSucceeded);
        }

        [Fact]
        public async Task System_admin_is_host_membership_and_never_reaches_the_rows()
        {
            var permissions = new RecordingUserPermissions(answer: true);

            var host = Evaluate(out var hostHandler, permissions, Principal(RoleNames.Host, RoleNames.Admin), "system:admin");
            await hostHandler.HandleAsync(host);
            Assert.True(host.HasSucceeded);

            var admin = Evaluate(out var adminHandler, permissions, Principal(RoleNames.Admin), "System:Admin");
            await adminHandler.HandleAsync(admin);
            Assert.False(admin.HasSucceeded);

            Assert.Empty(permissions.Calls);
        }

        [Theory]
        [InlineData("nocolon")]
        [InlineData(":manage")]
        [InlineData("gatetest:")]
        [InlineData("")]
        public async Task A_malformed_key_denies_without_consulting_the_rows(string key)
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var context = Evaluate(out var handler, permissions, Principal(RoleNames.Host, RoleNames.Admin), key);

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded);
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task No_HttpContext_denies()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var handler = new PermissionKeyHandler(new HttpContextAccessor(), permissions, new SilentLogManager());
            var context = Context(Principal(RoleNames.Admin), "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded);
            Assert.Empty(permissions.Calls);
        }

        [Fact]
        public async Task An_unresolved_alias_asks_for_site_minus_one_rather_than_guessing_a_site()
        {
            var permissions = new RecordingUserPermissions(answer: true);
            var httpContext = new DefaultHttpContext { User = Principal(RoleNames.Registered) }; // no alias in Items
            var handler = new PermissionKeyHandler(new HttpContextAccessor { HttpContext = httpContext }, permissions, new SilentLogManager());
            var context = Context(httpContext.User, "gatetest:manage");

            await handler.HandleAsync(context);

            Assert.Equal(-1, Assert.Single(permissions.Calls).SiteId);
        }

        [Fact]
        public void The_requirement_parses_the_key_once_at_policy_build_time()
        {
            var wellFormed = new PermissionKeyRequirement("Platform.Modules:Manage");
            Assert.True(wellFormed.IsWellFormed);
            Assert.Equal("platform.modules", wellFormed.Resource);
            Assert.Equal("manage", wellFormed.Operation);
            Assert.False(wellFormed.IsSystemAdmin);

            Assert.True(new PermissionKeyRequirement("system:admin").IsSystemAdmin);
            Assert.False(new PermissionKeyRequirement("nocolon").IsWellFormed);
            Assert.False(new PermissionKeyRequirement(null).IsWellFormed);
        }

        private static AuthorizationHandlerContext Evaluate(out PermissionKeyHandler handler, IUserPermissions permissions, ClaimsPrincipal principal, string key)
        {
            var accessor = Accessor(principal);
            handler = new PermissionKeyHandler(accessor, permissions, new SilentLogManager());
            return Context(principal, key);
        }

        private static AuthorizationHandlerContext Context(ClaimsPrincipal principal, string key)
        {
            return new AuthorizationHandlerContext(new[] { new PermissionKeyRequirement(key) }, principal, null);
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

        private static HttpContext Request(ClaimsPrincipal principal)
        {
            var context = new DefaultHttpContext { User = principal };
            context.Items[Constants.HttpContextAliasKey] = new Alias { SiteId = SiteId, TenantId = 1 };
            return context;
        }

        private static IHttpContextAccessor Accessor(ClaimsPrincipal principal)
        {
            return new HttpContextAccessor { HttpContext = Request(principal) };
        }

        /// <summary>
        /// Records the five-argument (no roles fallback) calls; the six-argument overload - the one that
        /// admits by role membership when an entity has no rows - THROWS, so a handler that reaches for it
        /// is red rather than quietly permissive.
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
                => throw new InvalidOperationException("The roles-fallback overload must not be used for a permission key (D99).");

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, List<Permission> permissions) => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string permissionName, string permissions) => throw new NotSupportedException();

            public Task<User> GetUserAsync(ClaimsPrincipal user) => throw new NotSupportedException();

            public Task<User> GetUserAsync() => throw new NotSupportedException();

            public Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string entityName, int entityId, string permissionName) => throw new NotSupportedException();
        }

        private sealed class SilentLogManager : ILogManager
        {
            public void Log(LogLevel level, object @class, LogFunction function, string message, params object[] args) { }
            public void Log(LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) { }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, string message, params object[] args) { }
            public void Log(int siteId, LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args) { }
            public void Log(Models.Log log) { }
        }

        private sealed class EmptyPermissionRepository : IPermissionRepository
        {
            private readonly List<Permission> _rows;

            public EmptyPermissionRepository(params Permission[] rows)
            {
                _rows = new List<Permission>(rows);
            }

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName)
                => Task.FromResult<IEnumerable<Permission>>(_rows.FindAll(p => p.SiteId == siteId && p.EntityName == entityName));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName)
                => Task.FromResult<IEnumerable<Permission>>(_rows.FindAll(p => p.SiteId == siteId && p.EntityName == entityName && p.PermissionName == permissionName));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId)
                => Task.FromResult<IEnumerable<Permission>>(_rows.FindAll(p => p.SiteId == siteId && p.EntityName == entityName && p.EntityId == entityId));

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName)
                => Task.FromResult<IEnumerable<Permission>>(_rows.FindAll(p => p.SiteId == siteId && p.EntityName == entityName && p.EntityId == entityId && p.PermissionName == permissionName));

            public Task<Permission> AddPermissionAsync(Permission permission) => throw new NotSupportedException();

            public Task<Permission> UpdatePermissionAsync(Permission permission) => throw new NotSupportedException();

            public Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false) => throw new NotSupportedException();

            public Task<Permission> GetPermissionAsync(int permissionId) => throw new NotSupportedException();

            public Task DeletePermissionAsync(int permissionId) => throw new NotSupportedException();

            public Task DeletePermissionsAsync(int siteId, string entityName, int entityId) => throw new NotSupportedException();
        }

        private sealed class NoEffectiveDateUserRoles : IUserRoleRepository
        {
            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int siteId) => Task.FromResult<IEnumerable<UserRole>>(new List<UserRole>());

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(int userId, int siteId) => Task.FromResult<IEnumerable<UserRole>>(new List<UserRole>());

            public Task<IEnumerable<UserRole>> GetUserRolesAsync(string roleName, int siteId) => Task.FromResult<IEnumerable<UserRole>>(new List<UserRole>());

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