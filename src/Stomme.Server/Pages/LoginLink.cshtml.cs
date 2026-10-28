using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Managers;
using Stomme.Shared;

namespace Stomme.Pages
{
    [AllowAnonymous]
    [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
    public class LoginLinkModel : PageModel
    {
        private readonly UserManager<IdentityUser> _identityUserManager;
        private readonly SignInManager<IdentityUser> _identitySignInManager;
        private readonly ILogManager _logger;

        public LoginLinkModel(UserManager<IdentityUser> identityUserManager, SignInManager<IdentityUser> identitySignInManager, ILogManager logger)
        {
            _identityUserManager = identityUserManager;
            _identitySignInManager = identitySignInManager;
            _logger = logger;
        }

        public async Task<IActionResult> OnGetAsync(string name, string token, string returnurl)
        {
            returnurl = (returnurl == null) ? "" : WebUtility.UrlDecode(returnurl);

            if (bool.Parse(HttpContext.GetSiteSettings().GetValue("LoginOptions:LoginLink", "false")) &&
                !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(token))
            {
                var validuser = false;

                if (!User.Identity.IsAuthenticated)
                {
                    IdentityUser identityuser = await _identityUserManager.FindByNameAsync(name);
                    if (identityuser != null)
                    {
                        var result = await _identityUserManager.ConfirmEmailAsync(identityuser, token);
                        if (result.Succeeded)
                        {
                            await _identitySignInManager.SignInAsync(identityuser, false);
                            _logger.Log(LogLevel.Information, this, LogFunction.Security, "Login Link Successful For User {Username}", name);
                            validuser = true;
                        }
                    }
                }

                if (!validuser)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Login Link Failed For User {Username}", name);
                    returnurl = HttpContext.GetAlias().Path + $"/login?status={ExternalLoginStatus.LoginLinkFailed}";
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Login Link Attempt For User {Username}", name);
                returnurl = HttpContext.GetAlias().Path;
            }

            if (!returnurl.StartsWith("/"))
            {
                returnurl = "/" + returnurl;
            }
            returnurl = Utilities.LocalReturnUrl(returnurl); // BL-244: "//host" and "/\host" are another site

            return LocalRedirect(Url.Content("~" + returnurl));
        }
    }
}
