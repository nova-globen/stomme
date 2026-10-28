#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// The Notifications module's per-site settings (BL-78 step 4): plain <see cref="Setting"/> rows on
    /// <see cref="EntityNames.Site"/>, read through <see cref="ISettingRepository"/>. Deliberately NOT in
    /// <c>SettingKeys.cs</c> (D100's NOT CHANGED list) - they are the module's, not the framework's.
    /// </summary>
    public static class NotificationSiteSettingNames
    {
        /// <summary>"True" lets a dispatch address a literal e-mail address (D100 point 4). Absent or anything else: refused.</summary>
        public const string ExternalRecipients = "NotificationExternalRecipients";

        /// <summary>Whole days a COMPLETED request is kept before the hourly pass purges it. Default 30; a non-positive or unparsable value means the default.</summary>
        public const string RequestRetention = "NotificationRequestRetention";

        /// <summary>The UTC instant (round-trip "O" format) the dispatch job's hourly pass last ran for the site - its throttle.</summary>
        public const string LastHourlyPass = "NotificationDispatchHourlyPassOn";
    }

    /// <summary>What the dispatcher and the dispatch pass read and write per site. A port, so the pipeline is testable without the framework store.</summary>
    public interface INotificationSiteSettings
    {
        Task<bool> AllowsExternalRecipientsAsync(int siteId, CancellationToken ct = default);

        Task<TimeSpan> GetRequestRetentionAsync(int siteId, CancellationToken ct = default);

        Task<DateTime?> GetLastHourlyPassAsync(int siteId, CancellationToken ct = default);

        Task SetLastHourlyPassAsync(int siteId, DateTime passedOnUtc, CancellationToken ct = default);
    }

    /// <summary>The production <see cref="INotificationSiteSettings"/>: site <see cref="Setting"/> rows (private - never sent to a browser).</summary>
    public sealed class SettingRepositoryNotificationSiteSettings : INotificationSiteSettings
    {
        private readonly ISettingRepository _settings;

        public SettingRepositoryNotificationSiteSettings(ISettingRepository settings) =>
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        public async Task<bool> AllowsExternalRecipientsAsync(int siteId, CancellationToken ct = default) =>
            string.Equals(
                await _settings.GetSettingValueAsync(EntityNames.Site, siteId, NotificationSiteSettingNames.ExternalRecipients, "False"),
                "True", StringComparison.OrdinalIgnoreCase);

        public async Task<TimeSpan> GetRequestRetentionAsync(int siteId, CancellationToken ct = default)
        {
            var raw = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, NotificationSiteSettingNames.RequestRetention, string.Empty);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
                ? TimeSpan.FromDays(days)
                : TimeSpan.FromDays(NotificationDispatchProcessor.DefaultRequestRetentionDays);
        }

        public async Task<DateTime?> GetLastHourlyPassAsync(int siteId, CancellationToken ct = default)
        {
            var raw = await _settings.GetSettingValueAsync(EntityNames.Site, siteId, NotificationSiteSettingNames.LastHourlyPass, string.Empty);
            // RoundtripKind alone: combined with AdjustToUniversal it THROWS ArgumentException (measured by the installed-host
            // drive - every site pass failed before this). The value is written with a 'Z' ("O" of a UTC DateTime).
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                ? DateTime.SpecifyKind(last.Kind == DateTimeKind.Local ? last.ToUniversalTime() : last, DateTimeKind.Utc)
                : null;
        }

        public async Task SetLastHourlyPassAsync(int siteId, DateTime passedOnUtc, CancellationToken ct = default)
        {
            var value = DateTime.SpecifyKind(passedOnUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
            var existing = await _settings.GetSettingAsync(EntityNames.Site, siteId, NotificationSiteSettingNames.LastHourlyPass);
            if (existing is null)
            {
                await _settings.AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = siteId,
                    SettingName = NotificationSiteSettingNames.LastHourlyPass,
                    SettingValue = value,
                    IsPrivate = true,
                });
                return;
            }

            existing.SettingValue = value;
            await _settings.UpdateSettingAsync(existing);
        }
    }

    /// <summary>Whether a user id names a user of a site - the dispatcher's recipient constraint for <c>RecipientKind.User</c>.</summary>
    public interface INotificationRecipientDirectory
    {
        Task<bool> IsSiteUserAsync(int siteId, int userId, CancellationToken ct = default);
    }

    /// <summary>
    /// The production <see cref="INotificationRecipientDirectory"/>: the user exists, is not deleted, and holds at least one
    /// role on THIS site (Stomme's user table is per tenant; site membership is the site's user-role rows).
    /// </summary>
    public sealed class SiteUserRecipientDirectory : INotificationRecipientDirectory
    {
        private readonly IUserRepository _users;
        private readonly IUserRoleRepository _userRoles;

        public SiteUserRecipientDirectory(IUserRepository users, IUserRoleRepository userRoles)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
        }

        public async Task<bool> IsSiteUserAsync(int siteId, int userId, CancellationToken ct = default)
        {
            if (siteId <= 0 || userId <= 0)
            {
                return false;
            }

            var user = await _users.GetUserAsync(userId);
            if (user is null || user.IsDeleted)
            {
                return false;
            }

            return (await _userRoles.GetUserRolesAsync(userId, siteId)).Any();
        }
    }
}
