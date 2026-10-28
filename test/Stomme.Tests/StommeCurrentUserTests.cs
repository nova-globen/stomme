using System.Security.Claims;
using Stomme.Extensions;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Pins the NARROWED <see cref="Stomme.Security.Abstractions.ICurrentUser"/> of the authorization bridge
    /// (D99 point 6): id / name / authenticated / roles / site, and nothing else. The site rule is the
    /// load-bearing one — it must be the SAME source <see cref="PermissionRowAccessGuard"/> scopes its rows by
    /// (the request-resolved alias, -1 when there is none), or "who am I" and "what may I do" answer for two
    /// different sites on the same request.
    /// </summary>
    public class StommeCurrentUserTests
    {
        [Fact]
        public void An_anonymous_request_has_no_id_no_name_no_roles_and_no_site()
        {
            var user = new StommeCurrentUser(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

            Assert.False(user.IsAuthenticated);
            Assert.Null(user.UserId);
            Assert.Null(user.UserName);
            Assert.Empty(user.Roles);
            Assert.Equal(-1, user.SiteId);
        }

        [Fact]
        public void No_HttpContext_at_all_is_anonymous_on_site_minus_one()
        {
            var user = new StommeCurrentUser(new HttpContextAccessor());

            Assert.False(user.IsAuthenticated);
            Assert.Null(user.UserId);
            Assert.Null(user.UserName);
            Assert.Empty(user.Roles);
            Assert.Equal(-1, user.SiteId);
        }

        [Fact]
        public void An_authenticated_request_carries_the_id_the_name_the_roles_and_the_resolved_site()
        {
            var context = Authenticated(siteKey: "1:7");
            context.Items[Constants.HttpContextAliasKey] = new Alias { SiteId = 7, TenantId = 1 };

            var user = new StommeCurrentUser(new HttpContextAccessor { HttpContext = context });

            Assert.True(user.IsAuthenticated);
            Assert.Equal(5, user.UserId);
            Assert.Equal("alice", user.UserName);
            Assert.Equal(7, user.SiteId);
            Assert.Contains(RoleNames.Admin, user.Roles);
            Assert.Contains(RoleNames.Admin.ToLowerInvariant(), user.Roles); // case-insensitive set
            Assert.DoesNotContain(RoleNames.Host, user.Roles);
        }

        // The rule this exists for: with NO resolved alias the site is -1 — exactly what
        // PermissionRowAccessGuard scopes by (GetAlias()?.SiteId ?? -1). Falling back to the cookie's SiteKey
        // here would make ICurrentUser.SiteId and the row lookup disagree on the very same request.
        [Fact]
        public void Without_a_resolved_alias_the_site_is_minus_one_even_when_the_cookie_names_one()
        {
            var context = Authenticated(siteKey: "1:7");

            var user = new StommeCurrentUser(new HttpContextAccessor { HttpContext = context });

            Assert.True(user.IsAuthenticated);
            Assert.Equal(-1, user.SiteId);
            Assert.Equal(-1, context.GetAlias()?.SiteId ?? -1); // the instrument: the guard reads the same -1
        }

        private static DefaultHttpContext Authenticated(string siteKey)
        {
            var identity = new ClaimsIdentity(Constants.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "5"));
            identity.AddClaim(new Claim(ClaimTypes.Role, RoleNames.Admin));
            identity.AddClaim(new Claim(Constants.SiteKeyClaimType, siteKey));
            return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }
    }
}