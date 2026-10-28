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
    /// D65 — the SAML2 provisioning callback (the async half of the bridge). After the IdP assertion, Sustainsys
    /// signs the RAW principal into the short-lived external cookie and returns here. This reads that principal
    /// and routes it through <see cref="StommeSiteAuthenticationBuilderExtensions.ProvisionExternalPrincipalAsync"/>
    /// — the SAME link-or-create path OIDC uses — which creates-or-links the IdentityUser + writes the UserLogin
    /// row (provider <c>saml2:{siteId}</c>) and returns the Identity-cookie identity. On success it swaps the
    /// external cookie for the real Identity cookie and lands the user on their return url. Fully async: no
    /// sync-over-async is introduced (D33).
    /// </summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public class SamlCallbackModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public SamlCallbackModel(IConfigurationRoot configuration)
        {
            _configuration = configuration;
        }

        public async Task<IActionResult> OnGetAsync(string returnurl)
        {
            returnurl = (returnurl == null) ? "/" : returnurl;
            returnurl = (!returnurl.StartsWith("/")) ? "/" + returnurl : returnurl;
            returnurl = Utilities.LocalReturnUrl(returnurl); // BL-244: "//host" and "/\host" are another site

            var alias = HttpContext.GetAlias();

            // The raw SAML principal Sustainsys signed into the external cookie.
            var result = await HttpContext.AuthenticateAsync(StommeExternalSchemesExtensions.Saml2ExternalScheme);
            if (!result.Succeeded || result.Principal == null)
            {
                return Redirect(Utilities.TenantUrl(alias, $"/login?status={ExternalLoginStatus.RemoteFailure}"));
            }

            var saml = _configuration.GetSection("Saml2");
            var identity = await StommeSiteAuthenticationBuilderExtensions.ProvisionExternalPrincipalAsync(
                HttpContext,
                result.Principal,
                AuthenticationProviderTypes.Saml2,
                saml.GetValue("ProviderName", "SAML"),
                saml.GetValue("IdentifierClaimType", ClaimTypes.NameIdentifier),
                saml.GetValue("NameClaimType", ClaimTypes.Name),
                saml.GetValue("EmailClaimType", ClaimTypes.Email));

            // Clear the throwaway external cookie regardless of outcome.
            await HttpContext.SignOutAsync(StommeExternalSchemesExtensions.Saml2ExternalScheme);

            if (identity.Label == ExternalLoginStatus.Success)
            {
                await HttpContext.SignInAsync(Constants.AuthenticationScheme, new ClaimsPrincipal(identity));
                return LocalRedirect(Url.Content("~" + returnurl));
            }

            return Redirect(Utilities.TenantUrl(alias, $"/login?status={identity.Label}&returnurl={returnurl}"));
        }
    }
}
