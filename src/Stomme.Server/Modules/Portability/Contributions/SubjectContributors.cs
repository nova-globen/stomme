#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Portability;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Identity;

namespace Stomme.Modules.Portability.Contributions
{
    /// <summary>
    /// The shipped subject-data contributors (BL-82, D107 item 8): what the framework holds about ONE person on ONE site,
    /// as explicit field ALLOWLISTS - a new column on a model never reaches a package by accident.
    /// </summary>
    /// <remarks>
    /// <b>Never in a package</b>: a password hash, a security stamp, a two-factor code, an authenticator key, a passkey, an
    /// external login's provider KEY (names only), a private setting, and a notification BODY - the framework writes live
    /// bearer tokens into bodies (password reset, sign-in link, e-mail confirmation), so a data package would hand a
    /// requester the subject's reset link (refutation 9).
    /// </remarks>
    internal static class SubjectPayload
    {
        public static PortableEntity Entity(string type, string id, string displayName, object payload) =>
            new(new PortableEntityRef(type, id), displayName, 1, JsonSerializer.Serialize(payload));
    }

    /// <summary>The account: identity fields and flags, the NAMES of linked external logins.</summary>
    public sealed class UserAccountSubjectContributor(IUserRepository users, UserManager<IdentityUser> identity) : ISubjectDataContributor
    {
        public const string Type = "user-account";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            var user = await users.GetUserAsync(scope.UserId);
            if (user is null)
            {
                return Array.Empty<PortableEntity>();
            }

            var identityUser = string.IsNullOrEmpty(user.Username) ? null : await identity.FindByNameAsync(user.Username);
            var providers = identityUser is null
                ? new List<string>()
                : (await identity.GetLoginsAsync(identityUser)).Select(login => login.LoginProvider).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();

            return new[]
            {
                SubjectPayload.Entity(Type, user.UserId.ToString(CultureInfo.InvariantCulture), user.Username ?? string.Empty, new
                {
                    username = user.Username,
                    displayName = user.DisplayName,
                    email = user.Email,
                    emailConfirmed = identityUser?.EmailConfirmed ?? user.EmailConfirmed,
                    timeZoneId = user.TimeZoneId,
                    cultureCode = user.CultureCode,
                    lastLoginOn = user.LastLoginOn,
                    lastIPAddress = user.LastIPAddress,
                    twoFactorRequired = user.TwoFactorRequired,
                    lockoutEnabled = identityUser?.LockoutEnabled,
                    lockoutEnd = identityUser?.LockoutEnd,
                    externalLoginProviders = providers,
                }),
            };
        }
    }

    /// <summary>The person's own settings (<c>EntityName = User</c>) - the non-private ones.</summary>
    public sealed class UserSettingsSubjectContributor(ISettingRepository settings) : ISubjectDataContributor
    {
        public const string Type = "user-setting";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await settings.GetSettingsAsync(EntityNames.User, scope.UserId))
                .Where(setting => !setting.IsPrivate)
                .Select(setting => SubjectPayload.Entity(Type, setting.SettingId.ToString(CultureInfo.InvariantCulture), setting.SettingName ?? string.Empty,
                    new { name = setting.SettingName, value = setting.SettingValue }))
                .ToList();
    }

    /// <summary>The roles the person holds ON THIS SITE - site roles only; a global role (Host) is not a site's to disclose (refutation 11).</summary>
    public sealed class SiteRolesSubjectContributor(IUserRoleRepository userRoles) : ISubjectDataContributor
    {
        public const string Type = "site-role";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await userRoles.GetUserRolesAsync(scope.UserId, scope.SiteId))
                .Where(userRole => userRole.Role is not null && userRole.Role.SiteId == scope.SiteId)
                .Select(userRole => SubjectPayload.Entity(Type, userRole.UserRoleId.ToString(CultureInfo.InvariantCulture), userRole.Role.Name ?? string.Empty,
                    new { role = userRole.Role.Name, effectiveDate = userRole.EffectiveDate, expiryDate = userRole.ExpiryDate }))
                .ToList();
    }

    /// <summary>Notifications to and from the person on this site - METADATA only, never a body (refutation 9).</summary>
    public sealed class NotificationMetadataSubjectContributor(INotificationRepository notifications) : ISubjectDataContributor
    {
        public const string Type = "notification";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            var received = await notifications.GetNotificationsAsync(scope.SiteId, -1, scope.UserId);
            var sent = await notifications.GetNotificationsAsync(scope.SiteId, scope.UserId, -1);
            return received.Concat(sent)
                .GroupBy(notification => notification.NotificationId)
                .Select(group => group.First())
                .Select(notification => SubjectPayload.Entity(Type, notification.NotificationId.ToString(CultureInfo.InvariantCulture), notification.Subject ?? string.Empty, new
                {
                    subject = notification.Subject,
                    fromUserId = notification.FromUserId,
                    toUserId = notification.ToUserId,
                    createdOn = notification.CreatedOn,
                    sendOn = notification.SendOn,
                    deliveredOn = notification.DeliveredOn,
                    isDelivered = notification.IsDelivered,
                    isRead = notification.IsRead,
                }))
                .ToList();
        }
    }

    /// <summary>The person's personal pages on this site (<c>Page.UserId</c>) - what they are, not their content.</summary>
    public sealed class PersonalPagesSubjectContributor(IPageRepository pages) : ISubjectDataContributor
    {
        public const string Type = "personal-page";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await pages.GetPagesAsync(scope.SiteId))
                .Where(page => page.UserId == scope.UserId)
                .Select(page => SubjectPayload.Entity(Type, page.PageId.ToString(CultureInfo.InvariantCulture), page.Name ?? string.Empty,
                    new { path = page.Path, name = page.Name, createdOn = page.CreatedOn }))
                .ToList();
    }
}
