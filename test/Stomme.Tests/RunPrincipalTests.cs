using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A pipeline run with no request acts as its user (BL-280, the owner's answer: its publisher, or whoever started it by
    /// hand): the guard decides by that user's STORED rows on the run's site, never grants the system key, and denies when
    /// no one is set; a request is never affected; the current user reports the run's user and site.
    /// </summary>
    public sealed class RunPrincipalTests
    {
        private const int Site = 7;
        private const int User = 42;

        [Fact]
        public async Task With_no_request_and_no_run_user_everything_is_denied_as_before()
        {
            var stored = new RecordingStoredRows(answer: true);
            var guard = new PermissionRowAccessGuard(NoRequest(), new NoRequestPermissions(), null, new RunPrincipal(), stored);

            Assert.False(await guard.IsAllowedAsync(null, "notes:edit"));
            Assert.Empty(stored.Calls);
        }

        [Fact]
        public async Task A_run_user_is_decided_by_their_stored_rows_on_the_runs_site()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User, "publisher");
            var granted = new RecordingStoredRows(answer: true);
            var denied = new RecordingStoredRows(answer: false);

            Assert.True(await new PermissionRowAccessGuard(NoRequest(), new NoRequestPermissions(), null, run, granted).IsAllowedAsync(null, "notes:edit"));
            Assert.Equal((Site, User, "notes:edit"), Assert.Single(granted.Calls));
            Assert.False(await new PermissionRowAccessGuard(NoRequest(), new NoRequestPermissions(), null, run, denied).IsAllowedAsync(null, "notes:edit"));
        }

        [Fact]
        public async Task A_run_never_holds_the_system_key()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User);
            var stored = new RecordingStoredRows(answer: true);

            Assert.False(await new PermissionRowAccessGuard(NoRequest(), new NoRequestPermissions(), null, run, stored).IsAllowedAsync(null, PermissionRowAccessGuard.SystemAdminKey));
            Assert.Empty(stored.Calls);
        }

        [Fact]
        public async Task A_feature_gated_run_still_needs_the_feature()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User);

            Assert.False(await new PermissionRowAccessGuard(NoRequest(), new NoRequestPermissions(), null, run, new RecordingStoredRows(true)).IsAllowedAsync("beta", "notes:edit"));
        }

        [Fact]
        public async Task A_request_is_decided_by_its_own_principal_whatever_the_run_says()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User);
            var stored = new RecordingStoredRows(answer: true);
            var context = new DefaultHttpContext();
            context.Items[Constants.HttpContextAliasKey] = new Alias { SiteId = Site, TenantId = 1 };

            var guard = new PermissionRowAccessGuard(new HttpContextAccessor { HttpContext = context }, new NoRequestPermissions(), null, run, stored);

            Assert.False(await guard.IsAllowedAsync(null, "notes:edit")); // the request's anonymous principal holds nothing
            Assert.Empty(stored.Calls);
        }

        [Fact]
        public void The_current_user_is_the_run_user_with_no_request_and_the_request_user_with_one()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User, "publisher");

            var headless = new StommeCurrentUser(NoRequest(), run);
            Assert.True(headless.IsAuthenticated);
            Assert.Equal(User, headless.UserId);
            Assert.Equal("publisher", headless.UserName);
            Assert.Equal(Site, headless.SiteId);

            var withRequest = new StommeCurrentUser(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, run);
            Assert.False(withRequest.IsAuthenticated);
            Assert.Null(withRequest.UserId);
        }

        [Fact]
        public void A_scope_acts_as_one_user_only()
        {
            var run = new RunPrincipal();
            run.ActAs(Site, User);
            run.ActAs(Site, User); // the same again is harmless

            Assert.Throws<InvalidOperationException>(() => run.ActAs(Site, User + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RunPrincipal().ActAs(Site, 0));
        }

        private static IHttpContextAccessor NoRequest() => new HttpContextAccessor { HttpContext = null };

        private sealed class RecordingStoredRows(bool answer) : IStoredRowPermissionEvaluator
        {
            public List<(int Site, int User, string Key)> Calls { get; } = new();

            public Task<bool> IsAuthorizedAsync(int siteId, int userId, string permissionKey, CancellationToken ct = default)
            {
                Calls.Add((siteId, userId, permissionKey));
                return Task.FromResult(answer);
            }
        }

        /// <summary>The request-path permissions: never asked here, and false if they were.</summary>
        private sealed class NoRequestPermissions : IUserPermissions
        {
            public Task<bool> IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName, string roles) => Task.FromResult(false);

            public Task<bool> IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal user, int siteId, string entityName, int entityId, string permissionName) => Task.FromResult(false);

            public Task<bool> IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal user, string permissionName, List<Permission> permissions) => Task.FromResult(false);

            public Task<bool> IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal user, string permissionName, string permissions) => Task.FromResult(false);

            public Task<User> GetUserAsync(System.Security.Claims.ClaimsPrincipal user) => Task.FromResult<User>(null);

            public Task<User> GetUserAsync() => Task.FromResult<User>(null);

            public Task<bool> IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal user, string entityName, int entityId, string permissionName) => Task.FromResult(false);
        }
    }
}
