using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Policy;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Managers
{
    public interface IUserManager
    {
        Task<User> GetUserAsync(int userid, int siteid);
        Task<User> GetUserAsync(string username, int siteid);
        Task<User> GetUserAsync(string username, string email, int siteid);
        Task<User> AddUser(User user);
        Task<User> UpdateUser(User user);
        Task DeleteUser(int userid, int siteid);
        Task<User> LoginUser(User user, bool setCookie, bool isPersistent);
        Task LogoutUserEverywhere(User user);
        Task<User> VerifyEmail(User user, string token);
        Task<bool> ForgotPassword(string username);
        Task<bool> ForgotUsername(string email);
        Task<User> ResetPassword(User user, string token);
        Task<User> VerifyTwoFactorAsync(User user, string token);
        Task<UserValidateResult> ValidateUser(string username, string email, string password);
        Task<bool> ValidatePassword(string password);
        Task<Dictionary<string, string>> ImportUsers(int siteId, string filePath, bool notify);
        Task<List<UserPasskey>> GetPasskeys(int userId, int siteId);
        Task UpdatePasskey(UserPasskey passkey);
        Task DeletePasskey(int userId, byte[] credentialId);
        Task<List<UserLogin>> GetLogins(int userId, int siteId);
        Task<User> AddLogin(User user, string token, string type, string key, string name);
        Task DeleteLogin(int userId, string provider, string key);
        Task<bool> SendLoginLink(string email, string returnurl);
    }

    public class UserManager : IUserManager
    {
        private readonly IUserRepository _users;
        private readonly IRoleRepository _roles;
        private readonly IUserRoleRepository _userRoles;
        private readonly UserManager<IdentityUser> _identityUserManager;
        private readonly SignInManager<IdentityUser> _identitySignInManager;
        private readonly ITenantManager _tenantManager;
        private readonly INotificationRepository _notifications;
        private readonly IFolderRepository _folders;
        private readonly IProfileRepository _profiles;
        private readonly ISettingRepository _settings;
        private readonly ISiteRepository _sites;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ICacheManager _cache;
        private readonly IStringLocalizer<UserManager> _localizer;
        private readonly Stomme.Erasure.IErasureQueue _erasureQueue;

        public UserManager(IUserRepository users, IRoleRepository roles, IUserRoleRepository userRoles, UserManager<IdentityUser> identityUserManager, SignInManager<IdentityUser> identitySignInManager, ITenantManager tenantManager, INotificationRepository notifications, IFolderRepository folders, IProfileRepository profiles, ISettingRepository settings, ISiteRepository sites, ISyncManager syncManager, ILogManager logger, ICacheManager cache, IStringLocalizer<UserManager> localizer, Stomme.Erasure.IErasureQueue erasureQueue, Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder identityAudit = null, Stomme.Modules.Sessions.Application.ISessionRegistry sessions = null)
        {
            _identityAudit = identityAudit;
            _sessions = sessions;
            _users = users;
            _roles = roles;
            _userRoles = userRoles;
            _identityUserManager = identityUserManager;
            _identitySignInManager = identitySignInManager;
            _tenantManager = tenantManager;
            _notifications = notifications;
            _folders = folders;
            _profiles = profiles;
            _settings = settings;
            _sites = sites;
            _syncManager = syncManager;
            _logger = logger;
            _cache = cache;
            _localizer = localizer;
            _erasureQueue = erasureQueue;
        }

        // BL-120: the identity audit ledger. Optional, so a composition without the module (a minimal test) still builds one.
        private readonly Stomme.Modules.IdentityAudit.Application.IIdentityAuditRecorder _identityAudit;
        private readonly Stomme.Modules.Sessions.Application.ISessionRegistry _sessions;

        private Task AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent evt, int? userId, string username, string detail = null, int? siteId = null) =>
            _identityAudit?.RecordAsync(evt, userId, username, detail, siteId) ?? Task.CompletedTask;

        public async Task<User> GetUserAsync(int userid, int siteid)
        {
            return await _cache.GetCacheAsync(await _tenantManager.GetAliasAsync(), $"User:{userid}", async entry =>
            {
                User user = await _users.GetUserAsync(userid);
                if (user != null)
                {
                    user.SiteId = siteid;
                    user.Roles = await GetUserRolesAsync(user.UserId, user.SiteId);
                    var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                    if (identityuser != null)
                    {
                        user.SecurityStamp = identityuser.SecurityStamp;
                        user.EmailConfirmed = identityuser.EmailConfirmed;
                    }
                    user.Settings = (await _settings.GetSettingsAsync(EntityNames.User, user.UserId))
                        .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                }
                return user;
            });
        }

        public async Task<User> GetUserAsync(string username, int siteid)
        {
            User user = await _users.GetUserAsync(username);
            if (user != null)
            {
                user = await GetUserAsync(user.UserId, siteid);
            }
            return user;
        }

        public async Task<User> GetUserAsync(string username, string email, int siteid)
        {
            User user = await _users.GetUserAsync(username, email);
            if (user != null)
            {
                user = await GetUserAsync(user.UserId, siteid);
            }
            return user;
        }

        private async Task<string> GetUserRolesAsync(int userId, int siteId)
        {
            string roles = "";
            List<UserRole> userroles = (await _userRoles.GetUserRolesAsync(userId, siteId)).ToList();
            foreach (UserRole userrole in userroles)
            {
                if (Utilities.IsEffectiveAndNotExpired(userrole.EffectiveDate, userrole.ExpiryDate))
                {
                    roles += userrole.Role.Name + ";";
                    if (userrole.Role.Name == RoleNames.Host && !userroles.Any(item => item.Role.Name == RoleNames.Admin))
                    {
                        roles += RoleNames.Admin + ";";
                    }
                    if (userrole.Role.Name == RoleNames.Host && !userroles.Any(item => item.Role.Name == RoleNames.Registered))
                    {
                        roles += RoleNames.Registered + ";";
                    }
                }
            }
            if (roles != "") roles = ";" + roles;
            return roles;
        }

        public async Task<User> AddUser(User user)
        {
            User User = null;
            var alias = await _tenantManager.GetAliasAsync();
            bool succeeded = false;
            string errors = "";

            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser == null)
            {
                if (string.IsNullOrEmpty(user.Password))
                {
                    // generate password based on random date and punctuation ie. Jan-23-1981+14:43:12!
                    Random rnd = new Random();
                    var date = DateTime.UtcNow.AddDays(-rnd.Next(50 * 365)).AddHours(rnd.Next(0, 24)).AddMinutes(rnd.Next(0, 60)).AddSeconds(rnd.Next(0, 60));
                    user.Password = date.ToString("MMM-dd-yyyy+HH:mm:ss", CultureInfo.InvariantCulture) + (char)rnd.Next(33, 47);
                }
                identityuser = new IdentityUser();
                identityuser.UserName = user.Username;
                identityuser.Email = user.Email;
                identityuser.EmailConfirmed = user.EmailConfirmed;
                var result = await _identityUserManager.CreateAsync(identityuser, user.Password);
                succeeded = result.Succeeded;
                if (!succeeded)
                {
                    errors = string.Join(", ", result.Errors.Select(e => e.Description));
                }
            }
            else
            {
                succeeded = true;
                if (!user.IsAuthenticated)
                {
                    // validate if the user already exists for the site
                    succeeded = string.IsNullOrEmpty((await GetUserAsync(user.Username, user.SiteId)).Roles);
                    if (succeeded)
                    {
                        // a user is registering for a new site - ensure their password is valid
                        var result = await _identitySignInManager.CheckPasswordSignInAsync(identityuser, user.Password, false);
                        succeeded = result.Succeeded;
                        if (!succeeded)
                        {
                            errors = "User Already Exists In Installation But Cannot Be Added To A Site Because The Password Provided Is Not Valid";
                        }
                        user.EmailConfirmed = succeeded;
                    }
                    else
                    {
                        errors = "User Already Exists In Site";
                    }
                }
            }

            if (succeeded)
            {
                user.DisplayName = (user.DisplayName == null) ? user.Username : user.DisplayName;
                user.LastLoginOn = null;
                user.LastIPAddress = "";
                User = await _users.AddUserAsync(user);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.User, User.UserId, SyncEventActions.Create);
            }
            else
            {
                _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Create, "Unable To Add User {Username} - {Errors}", user.Username, errors);
            }

            if (User != null)
            {
                string siteName = (await _sites.GetSiteAsync(user.SiteId)).Name;
                if (!user.EmailConfirmed && bool.Parse(await _settings.GetSettingValueAsync(EntityNames.Site, alias.SiteId, "LoginOptions:RequireConfirmedEmail", "true")))
                {
                    string token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                    string url = alias.Protocol + alias.Name + "/login?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                    string subject = _localizer["VerificationEmailSubject"];
                    subject = subject.Replace("[SiteName]", siteName);
                    string body = _localizer["VerificationEmailBody"].Value;
                    body = body.Replace("[UserDisplayName]", user.DisplayName);
                    body = body.Replace("[URL]", url);
                    body = body.Replace("[SiteName]", siteName);
                    var notification = new Notification(alias.SiteId, User, subject, body);
                    await _notifications.AddNotificationAsync(notification);
                }
                else
                {
                    if (!user.SuppressNotification)
                    {
                        string url = alias.Protocol + alias.Name + "/login";
                        string subject = _localizer["NoVerificationEmailSubject"];
                        subject = subject.Replace("[SiteName]", siteName);
                        string body = _localizer["NoVerificationEmailBody"].Value;
                        body = body.Replace("[UserDisplayName]", user.DisplayName);
                        body = body.Replace("[URL]", url);
                        body = body.Replace("[SiteName]", siteName);
                        body = body.Replace("[Username]", user.Username);
                        var notification = new Notification(alias.SiteId, User, subject, body);
                        await _notifications.AddNotificationAsync(notification);
                    }
                }

                User.Password = ""; // remove sensitive information
                _logger.Log(user.SiteId, LogLevel.Information, this, LogFunction.Create, "User Added {User}", User);
                await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.UserCreated, User.UserId, User.Username, siteId: user.SiteId);
            }
            else
            {
                user.Password = ""; // remove sensitive information
                _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Create, "Unable To Add User {User}", user);
            }

            return User;
        }

        public async Task<User> UpdateUser(User user)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                var alias = await _tenantManager.GetAliasAsync();

                if (!string.IsNullOrEmpty(user.Password))
                {
                    var validator = new PasswordValidator<IdentityUser>();
                    var result = await validator.ValidateAsync(_identityUserManager, null, user.Password);
                    if (result.Succeeded)
                    {
                        identityuser.PasswordHash = _identityUserManager.PasswordHasher.HashPassword(identityuser, user.Password);
                        await _identityUserManager.UpdateAsync(identityuser);
                        await _identityUserManager.UpdateSecurityStampAsync(identityuser); // will force user to sign in again
                    }
                    else
                    {
                        _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Update, "Unable To Update User {Username}. Password Does Not Meet Complexity Requirements.", user.Username);
                        return null;
                    }
                }

                if (user.Email != identityuser.Email)
                {
                    identityuser.Email = user.Email;
                    await _identityUserManager.UpdateAsync(identityuser); // security stamp not updated
                }

                if (bool.Parse(await _settings.GetSettingValueAsync(EntityNames.Site, alias.SiteId, "LoginOptions:RequireConfirmedEmail", "true")) && !user.IsDeleted)
                {
                    if (user.EmailConfirmed)
                    {
                        if (!identityuser.EmailConfirmed)
                        {
                            var emailConfirmationToken = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                            await _identityUserManager.ConfirmEmailAsync(identityuser, emailConfirmationToken);

                            string url = alias.Protocol + alias.Name + "/login?name=" + user.Username;
                            string body = "Dear " + user.DisplayName + ",\n\nThe Email Address For Your User Account Has Been Verified. You Can Now Login With Your Username And Password Using The Link Displayed Below:\n\n" + url + "\n\nThank You!";
                            var notification = new Notification(user.SiteId, user, "User Account Verification", body);
                            await _notifications.AddNotificationAsync(notification);
                        }
                    }
                    else
                    {
                        identityuser.EmailConfirmed = false;
                        await _identityUserManager.UpdateAsync(identityuser); // security stamp not updated

                        string token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                        string url = alias.Protocol + alias.Name + "/login?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                        string body = "Dear " + user.DisplayName + ",\n\nIn Order To Verify The Email Address Associated To Your User Account Please Click The Link Displayed Below:\n\n" + url + "\n\nThank You!";
                        var notification = new Notification(user.SiteId, user, "User Account Verification", body);
                        await _notifications.AddNotificationAsync(notification);
                    }
                }

                user = await _users.UpdateUserAsync(user);
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Reload);
                user.Password = ""; // remove sensitive information
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "User Updated {User}", user);
            }
            else
            {
                _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Update, "Unable To Update User {Username}. User Does Not Exist.", user.Username);
                user = null;
            }

            return user;
        }

        public async Task DeleteUser(int userid, int siteid)
        {
            // BL-120: the removal is recorded FIRST, while the account still resolves to a name.
            await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.UserDeleted, userid, (await _users.GetUserAsync(userid, false))?.Username, siteId: siteid);

            // D112: mark and queue. The identity subject (the authority's Subject claim, the identity user's id) is
            // captured NOW - once the identity user is deleted below it cannot be - and the erasure job runs every
            // participant (settings, notifications, visitors, logs, pages, access, tokens, sessions, the audit's
            // retention, the metering pseudonymisation...) on its next tick, idempotently, writing the receipt.
            var subjectUser = await _users.GetUserAsync(userid, false);
            var identitySubject = subjectUser is null ? null : (await _identityUserManager.FindByNameAsync(subjectUser.Username))?.Id;
            await _erasureQueue.EnqueueAsync(new Stomme.Erasure.ErasureSubject(siteid, userid, identitySubject, subjectUser?.Username));

            // remove user roles for site
            foreach (UserRole userrole in (await _userRoles.GetUserRolesAsync(userid, siteid)).ToList())
            {
                await _userRoles.DeleteUserRoleAsync(userrole.UserRoleId);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Role Deleted {UserRole}", userrole);
            }

            // remove user folder for site
            var folder = await _folders.GetFolderAsync(siteid, $"Users/{userid}/");
            if (folder != null)
            {
                if (Directory.Exists(await _folders.GetFolderPathAsync(folder)))
                {
                    Directory.Delete(await _folders.GetFolderPathAsync(folder), true);
                }
                await _folders.DeleteFolderAsync(folder.FolderId);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Folder Deleted {Folder}", folder);
            }

            // delete user if they are not a member of any other sites
            if (!(await _userRoles.GetUserRolesAsync(userid, -1)).Any())
            {
                // get identity user
                var user = await _users.GetUserAsync(userid, false);
                IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    // delete identity user
                    var result = await _identityUserManager.DeleteAsync(identityuser);
                    if (result != null)
                    {
                        // delete user
                        await _users.DeleteUserAsync(userid);
                        await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, userid, SyncEventActions.Delete);
                        await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, userid, SyncEventActions.Reload);
                        _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Deleted {UserId}", userid, result.ToString());
                    }
                    else
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Error Deleting User {UserId}", userid);
                    }
                }
            }
        }

        public async Task<User> LoginUser(User user, bool setCookie, bool isPersistent)
        {
            user.IsAuthenticated = false;

            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                var result = await _identitySignInManager.CheckPasswordSignInAsync(identityuser, user.Password, true);
                if (result.Succeeded)
                {
                    var lastIPAddress = user.LastIPAddress ?? ""; // preserve IP Address

                    user = await GetUserAsync(user.Username, user.SiteId);
                    if (!user.IsDeleted)
                    {
                        // get site settings
                        var settings = await _settings.GetSettingsAsync(EntityNames.Site, user.SiteId);

                        // check 2 factor authentication
                        if (_settings.GetSettingValue(settings, "LoginOptions:TwoFactor", "false") == "required" || user.TwoFactorRequired)
                        {
                            string siteName = (await _sites.GetSiteAsync(user.SiteId)).Name;
                            var token = await _identityUserManager.GenerateTwoFactorTokenAsync(identityuser, "Email");
                            user.TwoFactorCode = token;
                            user.TwoFactorExpiry = DateTime.UtcNow.AddMinutes(10);
                            await _users.UpdateUserAsync(user);
                            string subject = _localizer["TwoFactorEmailSubject"];
                            subject = subject.Replace("[SiteName]", siteName);
                            string body = _localizer["TwoFactorEmailBody"].Value;
                            body = body.Replace("[UserDisplayName]", user.DisplayName);
                            body = body.Replace("[SiteName]", siteName);
                            body = body.Replace("[Token]", token);
                            var notification = new Notification(user.SiteId, user, subject, body);
                            await _notifications.AddNotificationAsync(notification);

                            _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Verification Notification Sent For {Username}", user.Username);
                            user.TwoFactorRequired = true;
                        }
                        else
                        {
                            // check email confirmation
                            if (!bool.Parse(_settings.GetSettingValue(settings, "LoginOptions:RequireConfirmedEmail", "true")) || await _identityUserManager.IsEmailConfirmedAsync(identityuser))
                            {
                                // ensure user is registered for site
                                if (UserSecurity.ContainsRole(user.Roles, RoleNames.Registered))
                                {
                                    user.IsAuthenticated = true;
                                    user.LastLoginOn = DateTime.UtcNow;
                                    user.LastIPAddress = lastIPAddress;
                                    await _users.UpdateUserAsync(user);
                                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Successful For {Username} From IP Address {IPAddress}", user.Username, lastIPAddress);
                                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignIn, user.UserId, user.Username, siteId: user.SiteId);

                                    await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, "Login");

                                    if (setCookie)
                                    {
                                        await _identitySignInManager.SignInAsync(identityuser, isPersistent);
                                    }
                                }
                                else
                                {
                                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Denied - User {Username} Is Not An Active Member Of Site {SiteId}", user.Username, user.SiteId);
                                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInDenied, user.UserId, user.Username, "not a member of this site", user.SiteId);
                                }
                            }
                            else
                            {
                                _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Denied - User Email Address Not Verified For {Username}", user.Username);
                                await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInDenied, user.UserId, user.Username, "e-mail address not verified", user.SiteId);

                                // send verification email
                                var alias = await _tenantManager.GetAliasAsync();
                                string siteName = (await _sites.GetSiteAsync(user.SiteId)).Name;
                                string token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                                string url = alias.Protocol + alias.Name + "/login?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                                string subject = _localizer["VerificationEmailSubject"];
                                subject = subject.Replace("[SiteName]", siteName);
                                string body = _localizer["VerificationEmailBody"].Value;
                                body = body.Replace("[UserDisplayName]", user.DisplayName);
                                body = body.Replace("[URL]", url);
                                body = body.Replace("[SiteName]", siteName);
                                var notification = new Notification(alias.SiteId, user, subject, body);
                                await _notifications.AddNotificationAsync(notification);
                            }
                        }
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Failed - Account Deleted {Username}", user.Username);
                        await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInDenied, user.UserId, user.Username, "account deleted", user.SiteId);
                        user = new User { SiteId = user.SiteId, Username = user.Username, IsAuthenticated = false };
                    }
                }
                else
                {
                    if (result.IsLockedOut)
                    {
                        user = await GetUserAsync(user.Username, user.SiteId);
                        var alias = await _tenantManager.GetAliasAsync();
                        string siteName = (await _sites.GetSiteAsync(user.SiteId)).Name;
                        string token = await _identityUserManager.GeneratePasswordResetTokenAsync(identityuser);
                        string url = alias.Protocol + alias.Name + "/reset?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                        string subject = _localizer["UserLockoutEmailSubject"];
                        subject = subject.Replace("[SiteName]", siteName);
                        string body = _localizer["UserLockoutEmailBody"].Value;
                        body = body.Replace("[UserDisplayName]", user.DisplayName);
                        body = body.Replace("[URL]", url);
                        body = body.Replace("[SiteName]", siteName);
                        var notification = new Notification(user.SiteId, user, subject, body);
                        await _notifications.AddNotificationAsync(notification);
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Lockout Notification Sent For {Username}", user.Username);
                        await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.LockedOut, user.UserId, user.Username, siteId: user.SiteId);
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Failed {Username}", user.Username);
                        await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInFailed, (await GetUserAsync(user.Username, user.SiteId))?.UserId, user.Username, siteId: user.SiteId);
                    }
                    user = new User { SiteId = user.SiteId, Username = user.Username, IsAuthenticated = false };
                }
            }
            else
            {
                // An account that does not exist. Recorded WITHOUT the name typed - it may be a password in the wrong field.
                await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.SignInFailed, null, null, "unknown account", user.SiteId);
            }

            return user;
        }
        public async Task LogoutUserEverywhere(User user)
        {
            var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                await _identityUserManager.UpdateSecurityStampAsync(identityuser);
                if (_sessions != null && user.UserId > 0)
                {
                    // BL-119: the stamp reaches a cookie on its next validation; the session rows end NOW, so the
                    // owner's list is true at once and no cached check outlives the request.
                    await _sessions.RevokeAllAsync(await _tenantManager.GetAliasAsync(), user.UserId, user.UserId, "signed out everywhere");
                }
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Reload);
            }
        }

        public async Task<User> VerifyEmail(User user, string token)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                var result = await _identityUserManager.ConfirmEmailAsync(identityuser, token);
                if (result.Succeeded)
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Email Verified For {Username}", user.Username);
                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.EmailVerified, (await GetUserAsync(user.Username, user.SiteId))?.UserId, user.Username, siteId: user.SiteId);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Email Verification Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    user = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Email Verification Failed For {Username}And Token {Token}", user.Username, token);
                user = null;
            }
            return user;
        }


        /// <summary>
        /// Whether this site offers self-service password and username recovery (BL-90).
        /// </summary>
        /// <remarks>
        /// The two recovery endpoints are anonymous by necessity and were ungated: an operator who did not want
        /// them (a site whose accounts are provisioned centrally, or one where mail is not configured) could hide
        /// the LINKS - <c>LoginOptions:AllowSiteLogin</c> does that - but the endpoints still minted a reset token
        /// and queued mail for anyone who guessed a username. The per-site setting
        /// <c>LoginOptions:AllowPasswordRecovery</c> turns them off at the SERVER; absent or unparseable means
        /// TRUE, so a site that never sets it behaves exactly as before.
        /// </remarks>
        private async Task<bool> PasswordRecoveryAllowedAsync()
        {
            var alias = await _tenantManager.GetAliasAsync();
            var settings = await _settings.GetSettingsAsync(EntityNames.Site, alias.SiteId);
            var value = _settings.GetSettingValue(settings, "LoginOptions:AllowPasswordRecovery", "true");
            return !bool.TryParse(value, out var allowed) || allowed;
        }
        public async Task<bool> ForgotPassword(string username)
        {
            if (!await PasswordRecoveryAllowedAsync())
            {
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Recovery Is Disabled For This Site");
                return false;
            }

            if (!string.IsNullOrEmpty(username))
            {
                IdentityUser identityuser = await _identityUserManager.FindByNameAsync(username);
                if (identityuser != null)
                {
                    string token = await _identityUserManager.GeneratePasswordResetTokenAsync(identityuser);

                    var alias = await _tenantManager.GetAliasAsync();
                    var user = await GetUserAsync(username, alias.SiteId);
                    string url = alias.Protocol + alias.Name + "/reset?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                    string siteName = (await _sites.GetSiteAsync(alias.SiteId)).Name;
                    string subject = _localizer["ForgotPasswordEmailSubject"];
                    subject = subject.Replace("[SiteName]", siteName);
                    string body = _localizer["ForgotPasswordEmailBody"].Value;
                    body = body.Replace("[UserDisplayName]", user.DisplayName);
                    body = body.Replace("[URL]", url);
                    body = body.Replace("[SiteName]", siteName);
                    var notification = new Notification((await _tenantManager.GetAliasAsync()).SiteId, user, subject, body);
                    await _notifications.AddNotificationAsync(notification);

                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset Notification Sent For {Username}", user.Username);
                    return true;
                }
            }

            // The LOG says what happened; the CALLER is told the same thing either way. These endpoints carry no
            // [Authorize] - neither the controller nor the action - so returning the lookup result made
            // GET api/User/forgotpassword/{username} an anonymous account-existence oracle: `true` meant the
            // account was there. Driven in RecoveryEndpointDisclosureTests (BL-149). It compounds with BL-121,
            // which records that nothing rate-limits these endpoints: an oracle plus unlimited attempts is
            // enumeration at scale rather than one question.
            _logger.Log(LogLevel.Error, this, LogFunction.Security, "Password Reset Notification Failed For {Username}", username);
            return true;
        }

        public async Task<bool> ForgotUsername(string email)
        {
            if (!await PasswordRecoveryAllowedAsync())
            {
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "Username Recovery Is Disabled For This Site");
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(email))
                {
                    IdentityUser identityuser = await _identityUserManager.FindByEmailAsync(email);
                    if (identityuser != null)
                    {
                        var alias = await _tenantManager.GetAliasAsync();
                        var user = await GetUserAsync(identityuser.UserName, alias.SiteId);
                        string url = alias.Protocol + alias.Name + "/login?name=" + user.Username;
                        string siteName = (await _sites.GetSiteAsync(alias.SiteId)).Name;
                        string subject = _localizer["ForgotUsernameEmailSubject"];
                        subject = subject.Replace("[SiteName]", siteName);
                        string body = _localizer["ForgotUsernameEmailBody"].Value;
                        body = body.Replace("[UserDisplayName]", user.DisplayName);
                        body = body.Replace("[URL]", url);
                        body = body.Replace("[SiteName]", siteName);
                        var notification = new Notification((await _tenantManager.GetAliasAsync()).SiteId, user, subject, body);
                        await _notifications.AddNotificationAsync(notification);

                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "Forgot Username Notification Sent For {Email}", user.Email);
                        return true;
                    }
                }

                // Same answer whatever the lookup found - see the note in ForgotPassword (BL-149).
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Forgot Username Notification Failed For {Email}", email);
                return true;
            }
            catch (Exception ex)
            {
                // email may not be unique. Still the same answer: a duplicate address is itself a fact about the
                // site's accounts, and this used to be the one input that answered differently from a miss.
                _logger.Log(LogLevel.Error, this, LogFunction.Security, ex, "Forgot Username Notification Failed For {Email}", email);
                return true;
            }
        }

        public async Task<User> ResetPassword(User user, string token)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                // note that ResetPasswordAsync checks password complexity rules
                var result = await _identityUserManager.ResetPasswordAsync(identityuser, token, user.Password);
                if (result.Succeeded)
                {
                    user = await _users.GetUserAsync(user.Username);
                    await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Update);
                    await _syncManager.AddSyncEventAsync(await _tenantManager.GetAliasAsync(), EntityNames.User, user.UserId, SyncEventActions.Reload);
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset For {Username}", user.Username);
                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.PasswordReset, (await GetUserAsync(user.Username, user.SiteId))?.UserId, user.Username, siteId: user.SiteId);
                    user.Password = "";
                }
                else
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.PasswordResetFailed, (await GetUserAsync(user.Username, user.SiteId))?.UserId, user.Username, siteId: user.SiteId);
                    user = null;
                }
            }
            else
            {
                // The token is NOT logged. It is a live credential for changing this account's password, and the
                // site Log table is readable by every administrator (BL-149). Whether one was supplied is enough
                // to diagnose the failure.
                _logger.Log(LogLevel.Error, this, LogFunction.Security,
                    "Password Reset Failed For {Username} - Token Supplied: {TokenSupplied}",
                    user.Username, !string.IsNullOrEmpty(token));
                user = null;
            }
            return user;
        }

        public async Task<User> VerifyTwoFactorAsync(User user, string token)
        {
            user = await _users.GetUserAsync(user.Username);
            if (user != null)
            {
                var alias = await _tenantManager.GetAliasAsync();
                var twoFactorRequired = (await _settings.GetSettingValueAsync(EntityNames.Site, alias.SiteId, "LoginOptions:TwoFactor", "false")) == "required" || user.TwoFactorRequired;
                if (twoFactorRequired && user.TwoFactorCode == token && DateTime.UtcNow < user.TwoFactorExpiry)
                {
                    user.IsAuthenticated = true;
                }
            }
            return user;
        }

        public async Task<UserValidateResult> ValidateUser(string username, string email, string password)
        {
            var validateResult = new UserValidateResult { Succeeded = true };

            //validate username
            var allowedChars = _identityUserManager.Options.User.AllowedUserNameCharacters;
            if (string.IsNullOrWhiteSpace(username) || (!string.IsNullOrEmpty(allowedChars) && username.Any(c => !allowedChars.Contains(c))))
            {
                validateResult.Succeeded = false;
                validateResult.Errors.Add("Message.Username.Invalid", string.Empty);
            }

            //validate password
            var passwordValidator = new PasswordValidator<IdentityUser>();
            var passwordResult = await passwordValidator.ValidateAsync(_identityUserManager, null, password);
            if (!passwordResult.Succeeded)
            {
                validateResult.Succeeded = false;
                validateResult.Errors.Add("Message.Password.Invalid", string.Empty);
            }

            return validateResult;
        }

        public async Task<bool> ValidatePassword(string password)
        {
            var validator = new PasswordValidator<IdentityUser>();
            var result = await validator.ValidateAsync(_identityUserManager, null, password);
            return result.Succeeded;
        }

        public async Task<Dictionary<string, string>> ImportUsers(int siteId, string filePath, bool notify)
        {
            var success = true;
            int rows = 0;
            int users = 0;

            if (System.IO.File.Exists(filePath))
            {
                var roles = (await _roles.GetRolesAsync(siteId)).ToList();
                var profiles = (await _profiles.GetProfilesAsync(siteId)).ToList();

                try
                {
                    string row = "";
                    using (var reader = new StreamReader(filePath))
                    {
                        // header row
                        if (reader.Peek() > -1)
                        {
                            row = reader.ReadLine();
                        }

                        if (!string.IsNullOrEmpty(row.Trim()))
                        {
                            var header = row.Replace("\"", "").Split('\t');
                            if (header[0].Trim() == "Email")
                            {
                                for (int index = 4; index < header.Length - 1; index++)
                                {
                                    if (!string.IsNullOrEmpty(header[index].Trim()) && !profiles.Any(item => item.Name == header[index].Trim()))
                                    {
                                        _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import Contains Profile Name {Profile} Which Does Not Exist", header[index]);
                                        success = false;
                                    }
                                }
                            }
                            else
                            {
                                _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import File Is Not In Correct Format. Please Use Template Provided.");
                                success = false;
                            }

                            if (success)
                            {
                                // detail rows
                                while (reader.Peek() > -1)
                                {
                                    row = reader.ReadLine();
                                    rows++;

                                    if (!string.IsNullOrEmpty(row.Trim()))
                                    {
                                        var values = row.Replace("\"", "").Split('\t');

                                        // user
                                        var email = (values.Length > 0) ? values[0].Trim() : "";
                                        var username = (values.Length > 1) ? values[1].Trim() : "";
                                        var displayname = (values.Length > 2) ? values[2].Trim() : "";

                                        var user = await _users.GetUserAsync(username, email);
                                        if (user == null)
                                        {
                                            user = new User();
                                            user.SiteId = siteId;
                                            user.Email = values[0];
                                            user.Username = (!string.IsNullOrEmpty(username)) ? username : user.Email;
                                            user.DisplayName = (!string.IsNullOrEmpty(displayname)) ? displayname : user.Username;
                                            user.EmailConfirmed = true;
                                            user.SuppressNotification = !notify;
                                            user = await AddUser(user);
                                            if (user == null)
                                            {
                                                _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import Error Importing User {Email} {Username} {DisplayName}", email, username, displayname);
                                                success = false;
                                            }
                                        }
                                        else
                                        {
                                            if (!string.IsNullOrEmpty(displayname))
                                            {
                                                user.DisplayName = displayname;
                                                user.Password = "";
                                                user = await UpdateUser(user);
                                            }
                                        }

                                        var rolenames = (values.Length > 3) ? values[3].Trim() : "";
                                        if (user != null && !string.IsNullOrEmpty(rolenames))
                                        {
                                            // roles (comma delimited)
                                            foreach (var rolename in rolenames.Split(','))
                                            {
                                                var role = roles.FirstOrDefault(item => item.Name == rolename.Trim());
                                                if (role == null)
                                                {
                                                    role = new Role();
                                                    role.SiteId = siteId;
                                                    role.Name = rolename.Trim();
                                                    role.Description = rolename.Trim();
                                                    role = await _roles.AddRoleAsync(role);
                                                    roles.Add(role);
                                                }
                                                if (role != null)
                                                {
                                                    var userrole = await _userRoles.GetUserRoleAsync(user.UserId, role.RoleId, false);
                                                    if (userrole == null)
                                                    {
                                                        userrole = new UserRole();
                                                        userrole.UserId = user.UserId;
                                                        userrole.RoleId = role.RoleId;
                                                        await _userRoles.AddUserRoleAsync(userrole);
                                                    }
                                                }
                                            }
                                        }

                                        if (user != null && values.Length > 4)
                                        {
                                            // profiles
                                            var settings = await _settings.GetSettingsAsync(EntityNames.User, user.UserId);
                                            for (int index = 4; index < values.Length - 1; index++)
                                            {
                                                if (header.Length > index && !string.IsNullOrEmpty(values[index].Trim()))
                                                {
                                                    var profile = profiles.FirstOrDefault(item => item.Name == header[index].Trim());
                                                    if (profile != null)
                                                    {
                                                        var setting = settings.FirstOrDefault(item => item.SettingName == profile.Name);
                                                        if (setting == null)
                                                        {
                                                            setting = new Setting();
                                                            setting.EntityName = EntityNames.User;
                                                            setting.EntityId = user.UserId;
                                                            setting.SettingName = profile.Name;
                                                            setting.SettingValue = values[index].Trim();
                                                            await _settings.AddSettingAsync(setting);
                                                        }
                                                        else
                                                        {
                                                            if (setting.SettingValue != values[index].Trim())
                                                            {
                                                                setting.SettingValue = values[index].Trim();
                                                                await _settings.UpdateSettingAsync(setting);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }

                                        users++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            success = false;
                            _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import File Contains No Header Row");
                        }
                    }

                    _logger.Log(LogLevel.Information, this, LogFunction.Create, "User Import: {Rows} Rows Processed, {Users} Users Imported", rows, users);
                }
                catch (Exception ex)
                {
                    success = false;
                    _logger.Log(LogLevel.Error, this, LogFunction.Create, ex, "Error Importing User Import File {SiteId} {FilePath} {Notify}", siteId, filePath, notify);
                }
            }
            else
            {
                success = false;
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import File Does Not Exist {FilePath}", filePath);
            }

            // return results
            var result = new Dictionary<string, string>();
            result.Add("Success", success.ToString());
            result.Add("Users", users.ToString());

            return result;
        }

        public async Task<List<UserPasskey>> GetPasskeys(int userId, int siteId)
        {
            var passkeys = new List<UserPasskey>();
            var user = await _users.GetUserAsync(userId);
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    var userpasskeys = await _identityUserManager.GetPasskeysAsync(identityuser);
                    foreach (var userpasskey in userpasskeys)
                    {
                        // passkey name is prefixed with SiteId for multi-tenancy
                        if (userpasskey.Name.StartsWith($"{siteId}:"))
                        {
                            passkeys.Add(new UserPasskey { CredentialId = userpasskey.CredentialId, Name = userpasskey.Name.Split(':')[1], UserId = userId });
                        }
                    }
                }
            }
            return passkeys;
        }

        public async Task UpdatePasskey(UserPasskey passkey)
        {
            var user = await _users.GetUserAsync(passkey.UserId);
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    var userPasskeyInfo = await _identityUserManager.GetPasskeyAsync(identityuser, passkey.CredentialId);
                    if (userPasskeyInfo != null)
                    {
                        userPasskeyInfo.Name = passkey.Name;
                        await _identityUserManager.AddOrUpdatePasskeyAsync(identityuser, userPasskeyInfo);
                    }
                }
            }
        }

        public async Task DeletePasskey(int userId, byte[] credentialId)
        {
            var user = await _users.GetUserAsync(userId);
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    await _identityUserManager.RemovePasskeyAsync(identityuser, credentialId);
                }
            }
        }

        public async Task<List<UserLogin>> GetLogins(int userId, int siteId)
        {
            var logins = new List<UserLogin>();
            var user = await _users.GetUserAsync(userId);
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    var userlogins = await _identityUserManager.GetLoginsAsync(identityuser);
                    foreach (var userlogin in userlogins)
                    {
                        if (userlogin.LoginProvider.EndsWith(":" + siteId.ToString()))
                        {
                            logins.Add(new UserLogin { Provider = userlogin.LoginProvider, Key = userlogin.ProviderKey, Name = userlogin.ProviderDisplayName });
                        }
                    }
                }
            }
            return logins;
        }

        public async Task<User> AddLogin(User user, string token, string type, string key, string name)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                var result = await _identityUserManager.ConfirmEmailAsync(identityuser, token);
                if (result.Succeeded)
                {
                    // make LoginProvider multi-tenant aware
                    type += ":" + user.SiteId.ToString();
                    await _identityUserManager.AddLoginAsync(identityuser, new UserLoginInfo(type, key, name));
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "External Login Linkage Successful For {Username} And Provider {Provider}", user.Username, type);
                    await AuditAsync(Stomme.Models.IdentityAudit.IdentityAuditEvent.ExternalLoginLinked, user.UserId, user.Username, type, user.SiteId);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "External Login Linkage Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    user = null;
                }
            }
            return user;
        }


        public async Task DeleteLogin(int userId, string provider, string key)
        {
            var user = await _users.GetUserAsync(userId);
            if (user != null)
            {
                var identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    await _identityUserManager.RemoveLoginAsync(identityuser, provider, key);
                }
            }
        }

        public async Task<bool> SendLoginLink(string email, string returnurl)
        {
            try
            {
                if (!string.IsNullOrEmpty(email))
                {
                    IdentityUser identityuser = await _identityUserManager.FindByEmailAsync(email);
                    if (identityuser != null)
                    {
                        var token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);

                        var alias = await _tenantManager.GetAliasAsync();
                        var user = await GetUserAsync(identityuser.UserName, alias.SiteId);
                        string url = alias.Protocol + alias.Name + "/pages/loginlink?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token) + "&returnurl=" + WebUtility.UrlEncode(returnurl);
                        string siteName = (await _sites.GetSiteAsync(alias.SiteId)).Name;
                        string subject = _localizer["LoginLinkEmailSubject"];
                        subject = subject.Replace("[SiteName]", siteName);
                        string body = _localizer["LoginLinkEmailBody"].Value;
                        body = body.Replace("[UserDisplayName]", user.DisplayName);
                        body = body.Replace("[URL]", url);
                        body = body.Replace("[SiteName]", siteName);
                        var notification = new Notification((await _tenantManager.GetAliasAsync()).SiteId, user, subject, body);
                        await _notifications.AddNotificationAsync(notification);

                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "Login Link Notification Sent To {Email}", user.Email);
                        return true;
                    }
                }

                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Login Link Notification Failed For {Email}", email);
                return false;
            }
            catch (Exception ex)
            {
                // email may not be unique
                _logger.Log(LogLevel.Error, this, LogFunction.Security, ex, "Login Link Notification Failed For {Email}", email);
                return false;
            }
        }
    }
}
