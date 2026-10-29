using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Stomme.Extensions;
using Stomme.Shared;

namespace Stomme.Pages
{
    /// <summary>
    /// The explicit Windows/Negotiate login (installation-wide, config-gated). Unlike SAML/OIDC there is
    /// no redirect dance: Negotiate authenticates the request in place. When the request carries no negotiated
    /// credential this issues an explicit challenge → HTTP 401 with <c>WWW-Authenticate: Negotiate</c> (the
    /// locally-drivable smoke); a domain browser then resends with a Kerberos/NTLM token and the second pass
    /// authenticates. On success the Windows principal is routed through the SAME
    /// <see cref="StommeSiteAuthenticationBuilderExtensions.ProvisionExternalPrincipalAsync"/> link-or-create
    /// bridge (provider <c>negotiate:{siteId}</c>) and swapped for the Identity cookie. Negotiate is challenged
    /// ONLY here — never a default/global challenge — so it never forces Windows auth on other requests (which
    /// would break the D30 render guard). Real Kerberos requires a domain-joined host (sanctioned deferral).
    /// </summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public class WindowsLoginModel : PageModel
    {
        private readonly IAuthenticationSchemeProvider _schemes;
        private readonly IConfiguration _configuration;

        public WindowsLoginModel(IAuthenticationSchemeProvider schemes, IConfigurationRoot configuration)
        {
            _schemes = schemes;
            _configuration = configuration;
        }

        public async Task<IActionResult> OnGetAsync(string returnurl)
        {
            returnurl = (returnurl == null) ? "/" : returnurl;
            returnurl = (!returnurl.StartsWith("/")) ? "/" + returnurl : returnurl;
            returnurl = Utilities.LocalReturnUrl(returnurl); // "//host" and "/\host" are another site

            // Gate: only meaningful when Negotiate is registered (WindowsAuth:Enabled on a supported host).
            if (await _schemes.GetSchemeAsync(StommeExternalSchemesExtensions.NegotiateScheme) == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return new EmptyResult();
            }

            var result = await HttpContext.AuthenticateAsync(StommeExternalSchemesExtensions.NegotiateScheme);
            if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
            {
                // 401 + WWW-Authenticate: Negotiate — the browser (domain-joined) resends with a token.
                return new ChallengeResult(
                    StommeExternalSchemesExtensions.NegotiateScheme,
                    new AuthenticationProperties { RedirectUri = HttpContext.Request.Path + HttpContext.Request.QueryString });
            }

            var windows = _configuration.GetSection("WindowsAuth");
            var identity = await StommeSiteAuthenticationBuilderExtensions.ProvisionExternalPrincipalAsync(
                HttpContext,
                result.Principal,
                AuthenticationProviderTypes.Negotiate,
                windows.GetValue("ProviderName", "Windows"),
                windows.GetValue("IdentifierClaimType", ClaimTypes.Name),
                windows.GetValue("NameClaimType", ClaimTypes.Name),
                windows.GetValue("EmailClaimType", ClaimTypes.Email));

            if (identity.Label == ExternalLoginStatus.Success)
            {
                await HttpContext.SignInAsync(Constants.AuthenticationScheme, new ClaimsPrincipal(identity));
                return LocalRedirect(Url.Content("~" + returnurl));
            }

            return Redirect(Utilities.TenantUrl(HttpContext.GetAlias(), $"/login?status={identity.Label}&returnurl={returnurl}"));
        }
    }
}
