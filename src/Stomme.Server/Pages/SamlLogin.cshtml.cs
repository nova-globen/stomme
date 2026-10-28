using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stomme.Extensions;
using Stomme.Shared;

namespace Stomme.Pages
{
    /// <summary>
    /// D65 — the explicit SAML2 login challenge (the installation-wide analogue of <c>External.cshtml</c> for
    /// the per-site OIDC consumer). Issues a <see cref="ChallengeResult"/> for the "Saml2" scheme ONLY when that
    /// scheme is registered (config-gated), so it is never a default/global challenge — the D30 render guard is
    /// unaffected. Sustainsys builds the AuthnRequest and redirects to the IdP; after the assertion it signs the
    /// raw principal into the external cookie and returns to <c>/signin-saml2</c> (this challenge's RedirectUri),
    /// where the async provisioning bridge runs.
    /// </summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public class SamlLoginModel : PageModel
    {
        private readonly IAuthenticationSchemeProvider _schemes;

        public SamlLoginModel(IAuthenticationSchemeProvider schemes)
        {
            _schemes = schemes;
        }

        public async Task<IActionResult> OnGetAsync(string returnurl)
        {
            returnurl = (returnurl == null) ? "/" : returnurl;
            returnurl = (!returnurl.StartsWith("/")) ? "/" + returnurl : returnurl;
            returnurl = Utilities.LocalReturnUrl(returnurl); // BL-244: "//host" and "/\host" are another site

            // Gate: challenge only when SAML2 is actually registered (Saml2:Enabled + configured IdP).
            if (await _schemes.GetSchemeAsync(StommeExternalSchemesExtensions.Saml2Scheme) == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return new EmptyResult();
            }

            var properties = new AuthenticationProperties
            {
                // Sustainsys returns here after the ACS sign-in into the external cookie.
                RedirectUri = "/signin-" + Stomme.Shared.AuthenticationProviderTypes.Saml2
                    + "?returnurl=" + WebUtility.UrlEncode(returnurl)
            };
            return new ChallengeResult(StommeExternalSchemesExtensions.Saml2Scheme, properties);
        }
    }
}
