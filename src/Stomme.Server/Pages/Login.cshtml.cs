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
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Pages
{
    [AllowAnonymous]
    [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
    public class LoginModel : PageModel
    {
        private readonly UserManager<IdentityUser> _identityUserManager;
        private readonly SignInManager<IdentityUser> _identitySignInManager;
        private readonly IUserManager _userManager;
        private readonly ILogManager _logger;
        private readonly Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder _identityAudit;
        private readonly Stomme.Repository.IUserRepository _users;
        private readonly Stomme.Repository.ISettingRepository _settings;

        public LoginModel(UserManager<IdentityUser> identityUserManager, SignInManager<IdentityUser> identitySignInManager, IUserManager userManager, ILogManager logger,
            Stomme.Repository.IUserRepository users, Stomme.Repository.ISettingRepository settings,
            Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder identityAudit = null)
        {
            _identityUserManager = identityUserManager;
            _identitySignInManager = identitySignInManager;
            _userManager = userManager;
            _logger = logger;
            _users = users;
            _settings = settings;
            _identityAudit = identityAudit;
        }

        public async Task<IActionResult> OnPostAsync(string username, string password, bool remember, string returnurl, string code)
        {
            if (!User.Identity.IsAuthenticated && !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                bool validuser = false;
                var siteId = HttpContext.GetAlias()?.SiteId ?? 0;
                User user = null;
                var outcome = Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInFailed;
                string detail = null;
                IdentityUser identityuser = await _identityUserManager.FindByNameAsync(username);
                if (identityuser != null)
                {
                    var result = await _identitySignInManager.CheckPasswordSignInAsync(identityuser, password, true);
                    user = await _userManager.GetUserAsync(identityuser.UserName, siteId);
                    if (result.Succeeded)
                    {
                        if (user != null && !user.IsDeleted && UserSecurity.ContainsRole(user.Roles, RoleNames.Registered))
                        {
                            // The same gates as UserManager.LoginUser: this page sets the cookie, so a password alone must
                            // not pass where the site asks for a second factor or a confirmed address - posting here directly
                            // skipped both. A second factor is the code the sign-in mailed, still valid, used once.
                            detail = await SecondGateAsync(identityuser, username, siteId, code);
                            validuser = detail == null;
                            outcome = validuser ? Stomme.Models.IdentityAudit.IdentityAuditEvent.SignIn : Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInDenied;
                        }
                        else
                        {
                            outcome = Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInDenied;
                            detail = "not an active member of this site";
                        }
                    }
                    else if (result.IsLockedOut)
                    {
                        outcome = Stomme.Models.IdentityAudit.IdentityAuditEvent.LockedOut;
                    }
                }

                if (validuser)
                {
                    // note that .NET Identity uses a hardcoded ApplicationScheme of "Identity.Application" in SignInAsync
                    await _identitySignInManager.SignInAsync(identityuser, remember);
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Login Successful For User {Username}", username);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Login Failed For User {Username}", username);
                }

                // An account that does not exist is recorded WITHOUT the name typed: it may be a password in the wrong field.
                if (_identityAudit != null)
                {
                    await _identityAudit.RecordAsync(outcome, user?.UserId, identityuser == null ? null : identityuser.UserName,
                        identityuser == null ? "unknown account" : detail, siteId > 0 ? siteId : null);
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Attempt To Login User {Username}", username);
            }

            if (returnurl == null)
            {
                returnurl = "";
            }
            else
            {
                returnurl = WebUtility.UrlDecode(returnurl);
            }
            if (!returnurl.StartsWith("/"))
            {
                returnurl = "/" + returnurl;
            }
            returnurl = Utilities.LocalReturnUrl(returnurl); // "//host" and "/\host" are another site

            return LocalRedirect(Url.Content("~" + returnurl));
        }

        /// <summary>Null when the site lets this password through; otherwise why not, for the audit.</summary>
        private async Task<string> SecondGateAsync(IdentityUser identityuser, string username, int siteId, string code)
        {
            var stored = await _users.GetUserAsync(username);
            var twoFactor = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, "LoginOptions:TwoFactor", "false") == "required" || stored?.TwoFactorRequired == true;
            if (twoFactor)
            {
                if (stored == null || string.IsNullOrEmpty(code) || stored.TwoFactorCode != code || !(DateTime.UtcNow < stored.TwoFactorExpiry))
                {
                    return "two-factor code missing, wrong or expired";
                }

                stored.TwoFactorCode = null;
                stored.TwoFactorExpiry = null;
                await _users.UpdateUserAsync(stored);
                return null;
            }

            if (bool.Parse(await _settings.GetSettingValueAsync(EntityNames.Site, siteId, "LoginOptions:RequireConfirmedEmail", "true")) && !await _identityUserManager.IsEmailConfirmedAsync(identityuser))
            {
                return "e-mail address not verified";
            }

            return null;
        }
    }
}
