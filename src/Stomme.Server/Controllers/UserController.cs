using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class UserController : Controller
    {
        private readonly IUserRepository _users;
        private readonly ITenantManager _tenantManager;
        private readonly IUserManager _userManager;
        private readonly ISiteRepository _sites;
        private readonly IUserPermissions _userPermissions;
        private readonly IJwtManager _jwtManager;
        private readonly IFileRepository _files;
        private readonly ISettingRepository _settings;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;

        public UserController(IUserRepository users, ITenantManager tenantManager, IUserManager userManager, ISiteRepository sites, IUserPermissions userPermissions, IJwtManager jwtManager, IFileRepository files, ISettingRepository settings, ISyncManager syncManager, ILogManager logger)
        {
            _users = users;
            _tenantManager = tenantManager;
            _userManager = userManager;
            _sites = sites;
            _userPermissions = userPermissions;
            _jwtManager = jwtManager;
            _files = files;
            _settings = settings;
            _syncManager = syncManager;
            _logger = logger;
        }

        // GET api/<controller>/5?siteid=x
        [HttpGet("{id}")]
        [Authorize]
        public async Task<User> Get(int id, string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (int.TryParse(siteid, out int SiteId) && SiteId == alias.SiteId)
            {
                User user = await _userManager.GetUserAsync(id, SiteId);
                if (user == null)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return await Filter(user);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Get Attempt {UserId} {SiteId}", id, siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/username/{username}?siteid=x
        [HttpGet("username/{username}")]
        public async Task<User> Get(string username, string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (int.TryParse(siteid, out int SiteId) && SiteId == alias.SiteId)
            {
                User user = await _userManager.GetUserAsync(username, SiteId);
                if (user == null)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return await Filter(user);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Get Attempt {Username} {SiteId}", username, siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/name/{username}/{email}?siteid=x
        [HttpGet("search/{username}/{email}")]
        public async Task<User> Get(string username, string email, string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (int.TryParse(siteid, out int SiteId) && SiteId == alias.SiteId)
            {
                username = (username == "-") ? "" : username;
                email = (email == "-") ? "" : email;
                User user = await _userManager.GetUserAsync(username, email, SiteId);
                if (user == null)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return await Filter(user);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Get Attempt {Username} {Email} {SiteId}", username, email, siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        private async Task<User> Filter(User user)
        {
            // clone object to avoid mutating cache 
            User filtered = null;

            if (user != null)
            {
                filtered = new User();

                // public properties
                filtered.SiteId = user.SiteId;
                filtered.UserId = user.UserId;
                filtered.Username = user.Username;
                filtered.DisplayName = user.DisplayName;

                // restricted properties
                filtered.Password = "";
                filtered.TwoFactorCode = "";
                filtered.SecurityStamp = "";

                // include private properties if authenticated user is accessing their own user account or is an administrator
                if (await _userPermissions.IsAuthorizedAsync(User, user.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == user.UserId)
                {
                    filtered.Email = user.Email;
                    filtered.TimeZoneId = user.TimeZoneId;
                    filtered.PhotoFileId = user.PhotoFileId;
                    filtered.LastLoginOn = user.LastLoginOn;
                    filtered.LastIPAddress = user.LastIPAddress;
                    filtered.TwoFactorRequired = user.TwoFactorRequired;
                    filtered.EmailConfirmed = user.EmailConfirmed;
                    filtered.Roles = user.Roles;
                    filtered.CreatedBy = user.CreatedBy;
                    filtered.CreatedOn = user.CreatedOn;
                    filtered.ModifiedBy = user.ModifiedBy;
                    filtered.ModifiedOn = user.ModifiedOn;
                    filtered.DeletedBy = user.DeletedBy;
                    filtered.DeletedOn = user.DeletedOn;
                    filtered.IsDeleted = user.IsDeleted;
                    filtered.Settings = user.Settings; // include all settings
                }
            }

            return filtered;
        }

        // POST api/<controller>
        [HttpPost]
        [EnableRateLimiting(AccountRateLimiting.RegistrationPolicy)]
        public async Task<User> Post([FromBody] User user)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid && user.SiteId == alias.SiteId)
            {
                bool allowregistration;
                if (await _userPermissions.IsAuthorizedAsync(User, user.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin))
                {
                    user.IsAuthenticated = true; // admins can add any existing user to a site
                    allowregistration = true;
                }
                else
                {
                    user.EmailConfirmed = false; // standard users cannot specify that their email is verified
                    user.IsAuthenticated = false; // existing users can only be added to a site if they provide a valid username and password
                    allowregistration = (await _sites.GetSiteAsync(user.SiteId)).AllowRegistration;
                }

                if (allowregistration)
                {
                    user = await _userManager.AddUser(user);
                }
                else
                {
                    _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Create, "User Registration Is Not Enabled For Site. User Was Not Added {User}", user);
                }

                return user;
            }
            else
            {
                user.Password = ""; // remove sensitive information
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Post Attempt {User}", user);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize]
        public async Task<User> Put(int id, [FromBody] User user)
        {
            var alias = await _tenantManager.GetAliasAsync();

            var existing = await _userManager.GetUserAsync(user.UserId, user.SiteId);
            if (ModelState.IsValid && user.SiteId == alias.SiteId && user.UserId == id && existing != null
                && (await _userPermissions.IsAuthorizedAsync(User, user.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || User.Identity.Name == user.Username))
            {
                // only authorized users can update the email confirmation
                if (!await _userPermissions.IsAuthorizedAsync(User, user.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin))
                {
                    user.EmailConfirmed = existing.EmailConfirmed;
                }
                user = await _userManager.UpdateUser(user);
            }
            else
            {
                user.Password = ""; // remove sensitive information
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Put Attempt {User}", user);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                user = null;
            }
            return user;
        }

        // DELETE api/<controller>/5?siteid=x
        [HttpDelete("{id}")]
        [Authorize(Policy = $"{EntityNames.User}:{PermissionNames.Write}:{RoleNames.Host}")]
        public async Task Delete(int id, string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();

            User user = await _users.GetUserAsync(id, false);
            if (user != null && int.TryParse(siteid, out int SiteId) && SiteId == alias.SiteId)
            {
                await _userManager.DeleteUser(id, SiteId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Delete Attempt {UserId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // DELETE api/<controller>/me
        // A person deletes their OWN account on this site - the same path as the
        // administrator's delete, so UserManager.DeleteUser queues the erasure for both and the two are one mechanism.
        // A Host account is not deleted this way: another Host removes it, so an install cannot lose its last Host by
        // a click. The API sign-out clears the cookie for a browser that called directly (WebAssembly); the profile
        // page also posts to the logout page, which is what ends an interactive circuit's cookie.
        [HttpDelete("me")]
        [Authorize]
        public async Task DeleteMe()
        {
            var alias = await _tenantManager.GetAliasAsync();
            var caller = await _userPermissions.GetUserAsync(User);
            if (caller == null || caller.UserId <= 0 || User.IsInRole(RoleNames.Host))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Self Delete Attempt {UserId}", caller?.UserId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            await _userManager.DeleteUser(caller.UserId, alias.SiteId);
            await HttpContext.SignOutAsync(Constants.AuthenticationScheme);
        }

        // POST api/<controller>/signin
        [HttpPost("signin")]
        [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
        public async Task<User> Login([FromBody] User user, bool setCookie, bool isPersistent)
        {
            if (ModelState.IsValid)
            {
                user = await _userManager.LoginUser(user, setCookie, isPersistent);
            }
            else
            {
                user = new User { SiteId = user.SiteId, Username = user.Username, IsAuthenticated = false };
            }
            return user;
        }

        // POST api/<controller>/logout
        [HttpPost("logout")]
        [Authorize]
        public async Task Logout([FromBody] User user)
        {
            if ((await _userPermissions.GetUserAsync(User)).UserId == user.UserId)
            {
                await HttpContext.SignOutAsync(Constants.AuthenticationScheme);
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, "Logout");
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Logout {Username}", (user != null) ? user.Username : "");
            }
        }

        // POST api/<controller>/logout
        [HttpPost("logouteverywhere")]
        [Authorize]
        public async Task LogoutEverywhere([FromBody] User user)
        {
            if ((await _userPermissions.GetUserAsync(User)).UserId == user.UserId)
            {
                await _userManager.LogoutUserEverywhere(user);
                await HttpContext.SignOutAsync(Constants.AuthenticationScheme);
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, "Logout");
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Logout Everywhere {Username}", (user != null) ? user.Username : "");
            }
        }

        // POST api/<controller>/verify
        [HttpPost("verify")]
        [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
        public async Task<User> Verify([FromBody] User user, string token)
        {
            if (ModelState.IsValid)
            {
                user = await _userManager.VerifyEmail(user, token);
            }
            return user;
        }

        // GET api/<controller>/forgotpassword/x
        [HttpGet("forgotpassword/{username}")]
        [EnableRateLimiting(AccountRateLimiting.AccountEmailPolicy)]
        public async Task<bool> ForgotPassword(string username)
        {
            return await _userManager.ForgotPassword(username);
        }

        // GET api/<controller>/forgotusername/x
        [HttpGet("forgotusername/{email}")]
        [EnableRateLimiting(AccountRateLimiting.AccountEmailPolicy)]
        public async Task<bool> ForgotUsername(string email)
        {
            return await _userManager.ForgotUsername(email);
        }

        // POST api/<controller>/reset
        [HttpPost("reset")]
        [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
        public async Task<User> Reset([FromBody] User user, string token)
        {
            if (ModelState.IsValid)
            {
                user = await _userManager.ResetPassword(user, token);
            }
            return user;
        }

        // POST api/<controller>/twofactor
        [HttpPost("twofactor")]
        [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
        public async Task<User> TwoFactor([FromBody] User user, string token)
        {
            if (ModelState.IsValid && !string.IsNullOrEmpty(token))
            {
                user = await _userManager.VerifyTwoFactorAsync(user, token);
            }
            else
            {
                user.IsAuthenticated = false;
            }

            return user;
        }

        // GET api/<controller>/validate/x
        [HttpGet("validateuser")]
        public async Task<UserValidateResult> ValidateUser(string username, string email, string password)
        {
            return await _userManager.ValidateUser(username, email, password);
        }

        // GET api/<controller>/validate/x
        [HttpGet("validate/{password}")]
        public async Task<bool> Validate(string password)
        {
            return await _userManager.ValidatePassword(password);
        }

        // GET api/<controller>/token
        [HttpGet("token")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<string> Token()
        {
            var token = "";
            var sitesettings = HttpContext.GetSiteSettings();
            var secret = sitesettings.GetValue("JwtOptions:Secret", "");
            if (!string.IsNullOrEmpty(secret))
            {
                token = _jwtManager.GenerateToken(await _tenantManager.GetAliasAsync(), (ClaimsIdentity)User.Identity, secret, sitesettings.GetValue("JwtOptions:Issuer", ""), sitesettings.GetValue("JwtOptions:Audience", ""), int.Parse(sitesettings.GetValue("JwtOptions:Lifetime", "20")));
            }
            return token;
        }

        // GET api/<controller>/personalaccesstoken
        [HttpGet("personalaccesstoken")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<string> PersonalAccessToken()
        {
            var token = "";
            var sitesettings = HttpContext.GetSiteSettings();
            var secret = sitesettings.GetValue("JwtOptions:Secret", "");
            if (!string.IsNullOrEmpty(secret))
            {
                var lifetime = 525600; // long-lived token set to 1 year
                token = _jwtManager.GenerateToken(await _tenantManager.GetAliasAsync(), (ClaimsIdentity)User.Identity, secret, sitesettings.GetValue("JwtOptions:Issuer", ""), sitesettings.GetValue("JwtOptions:Audience", ""), lifetime);
            }
            return token;
        }

        // GET api/<controller>/authenticate
        [HttpGet("authenticate")]
        public User Authenticate()
        {
            User user = new User { IsAuthenticated = User.Identity.IsAuthenticated, Username = "", UserId = -1, Roles = "" };
            if (user.IsAuthenticated)
            {
                user.Username = User.Identity.Name;
                user.UserId = User.UserId();
                string roles = "";
                foreach (var roleName in User.Roles())
                {
                    roles += roleName + ";";
                }
                if (roles != "") roles = ";" + roles;
                user.Roles = roles;
                user.SecurityStamp = User.SecurityStamp();
            }
            return user;
        }

        // GET api/<controller>/passwordrequirements/5
        [HttpGet("passwordrequirements/{siteid}")]
        public async Task<Dictionary<string, string>> PasswordRequirements(int siteid)
        {
            var requirements = new Dictionary<string, string>();

            var site = await _sites.GetSiteAsync(siteid);
            if (site != null && (site.AllowRegistration || User.IsInRole(RoleNames.Registered)))
            {
                // get password settings
                var sitesettings = HttpContext.GetSiteSettings();
                requirements = sitesettings.Where(item => item.Key.StartsWith("IdentityOptions:Password:"))
                    .ToDictionary(item => item.Key, item => item.Value);
            }

            return requirements;
        }

        // GET: api/<controller>/passkey?id=x
        [HttpGet("passkey")]
        [Authorize]
        public async Task<IEnumerable<UserPasskey>> GetPasskeys(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == id)
            {
                return await _userManager.GetPasskeys(id, alias.SiteId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Passkey Get Attempt {UserId} {SiteId}", id, alias.SiteId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // PUT api/<controller>/passkey
        [HttpPut("passkey")]
        [Authorize]
        public async Task UpdatePasskey([FromBody] UserPasskey passkey)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (ModelState.IsValid)
            {
                if (await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == passkey.UserId)
                {
                    // passkey name is prefixed with SiteId for multi-tenancy
                    passkey.Name = $"{alias.SiteId}:" + passkey.Name;
                    await _userManager.UpdatePasskey(passkey);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Passkey Put Attempt {PassKey}", passkey);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Passkey Put Attempt {PassKey}", passkey);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // DELETE api/<controller>/passkey?id=x&credential=y
        [HttpDelete("passkey")]
        [Authorize]
        public async Task DeletePasskey(int id, string credential)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == id)
            {
                await _userManager.DeletePasskey(id, Base64Url.DecodeFromChars(credential));
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized User Passkey Delete Attempt {UserId} {Credential}", id, credential);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET: api/<controller>/login?id=x
        [HttpGet("login")]
        [Authorize]
        public async Task<IEnumerable<UserLogin>> GetLogins(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == id)
            {
                return await _userManager.GetLogins(id, alias.SiteId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized External Login Get Attempt {UserId} {SiteId}", id, alias.SiteId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // PUT api/<controller>/login
        [HttpPost("login")]
        [EnableRateLimiting(AccountRateLimiting.SignInPolicy)]
        public async Task<User> AddLogin([FromBody] User user, string token, string type, string key, string name)
        {
            if (ModelState.IsValid)
            {
                user = await _userManager.AddLogin(user, token, type, key, name);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized External Login Post Attempt {Username} {Token}", user.Username, token);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                user = null;
            }
            return user;
        }

        // DELETE api/<controller>/login?id=x&provider=y&key=z
        [HttpDelete("login")]
        [Authorize]
        public async Task DeleteLogin(int id, string provider, string key)
        {
            var alias = await _tenantManager.GetAliasAsync();

            if (await _userPermissions.IsAuthorizedAsync(User, alias.SiteId, EntityNames.User, -1, PermissionNames.Write, RoleNames.Admin) || (await _userPermissions.GetUserAsync(User)).UserId == id)
            {
                await _userManager.DeleteLogin(id, provider, key);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized External Login Delete Attempt {UserId} {Provider} {Key}", id, provider, key);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET api/<controller>/loginlink/x?y
        [HttpGet("loginlink/{email}")]
        [EnableRateLimiting(AccountRateLimiting.AccountEmailPolicy)]
        public async Task<bool> SendLoginLink(string email, string returnurl)
        {
            return await _userManager.SendLoginLink(email, returnurl);
        }
    }
}
