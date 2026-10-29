using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Stomme.Extensions;
using Stomme.Managers;
using Stomme.Security;
using Stomme.Shared;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Identity.OpenIddict
{
    /// <summary>
    /// <c>api/*</c> accepts an access token issued by THIS host's
    /// authority. Registered only while <c>OpenIddict:Enabled</c> is on, so a stock install is unchanged. A request to
    /// <c>/api/</c> carrying <c>Authorization: Bearer</c> is validated by OpenIddict's own validation handler (signature,
    /// lifetime, the host-inferred issuer - another tenant's token is ID2088 - and the token's entry, so a revoked token
    /// fails); it must be meant for Stomme's API (the <see cref="StommeOpenIddictExtensions.ApiResource"/> audience
    /// and the <c>api</c> scope) and carry a PERSON - or, for a client-credentials token, whose subject is its client, the
    /// application's SERVICE ACCOUNT while <c>OpenIddict:ServiceAccounts:Enabled</c> is on (<see cref="ServiceAccounts"/>);
    /// otherwise such a token authenticates no one. The subject is resolved to a user of the REQUEST's site and the
    /// principal is Stomme's own (<see cref="UserSecurity.CreateClaimsIdentity"/>), so roles and permissions are read
    /// at every request and never frozen into the token; a deleted user's token authenticates no one. Anything that fails
    /// leaves the request anonymous - the endpoint's own authorization then answers - and nothing about the token is logged.
    /// </summary>
    internal sealed class OpenIddictApiBearerMiddleware
    {
        public const string ApiScope = "api";

        private readonly RequestDelegate _next;

        public OpenIddictApiBearerMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            if (IsApiBearerRequest(context.Request) && context.User?.Identity?.IsAuthenticated != true)
            {
                ClaimsPrincipal principal = null;
                try
                {
                    principal = await ResolveAsync(context);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // fail closed: a token that cannot be resolved signs no one in, and the endpoint's own authorization answers
                    context.RequestServices.GetService<ILogger<OpenIddictApiBearerMiddleware>>()?.LogError(exception, "An access token on {Path} could not be resolved to a user; the request continues anonymous.", context.Request.Path);
                }

                if (principal != null)
                {
                    context.User = principal;
                }
            }

            await _next(context);
        }

        internal static bool IsApiBearerRequest(HttpRequest request) =>
            request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        private static async Task<ClaimsPrincipal> ResolveAsync(HttpContext context)
        {
            var alias = context.GetAlias();
            if (alias == null)
            {
                return null;
            }

            var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            var token = result?.Succeeded == true ? result.Principal : null;
            if (token == null
                || !token.HasAudience(StommeOpenIddictExtensions.ApiResource)
                || !token.HasScope(ApiScope))
            {
                return null;
            }

            var subject = token.GetClaim(Claims.Subject);
            var client = token.GetClaim(Claims.ClientId);
            if (string.IsNullOrEmpty(subject))
            {
                return null;
            }

            var identityUsers = context.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
            IdentityUser identityUser;
            if (string.Equals(subject, client, StringComparison.Ordinal))
            {
                // No person behind the token (client credentials): its application's SERVICE ACCOUNT, when the switch is on
                // and the server made one - an account of the derived name that is not locked out for good is a
                // person's, never used for an application.
                var configuration = context.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfigurationRoot>();
                if (!ServiceAccounts.IsEnabled(configuration))
                {
                    return null;
                }

                identityUser = await identityUsers.FindByNameAsync(ServiceAccounts.UsernameFor(client));
                if (identityUser == null || !await ServiceAccounts.IsLockedOutForGoodAsync(identityUsers, identityUser))
                {
                    return null;
                }
            }
            else
            {
                identityUser = await identityUsers.FindByIdAsync(subject);
            }

            if (identityUser == null || string.IsNullOrEmpty(identityUser.UserName))
            {
                return null;
            }

            var users = context.RequestServices.GetRequiredService<IUserManager>();
            var user = await users.GetUserAsync(identityUser.UserName, alias.SiteId);
            if (user == null || user.IsDeleted)
            {
                return null;
            }

            return new ClaimsPrincipal(UserSecurity.CreateClaimsIdentity(alias, user));
        }
    }
}
