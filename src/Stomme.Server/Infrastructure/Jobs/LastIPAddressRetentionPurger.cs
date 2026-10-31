#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Repository;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Clears a user's recorded <c>LastIPAddress</c> once the sign-in that recorded it is older than the site's
    /// <c>LastIPAddressRetention</c> (BL-275, the website's privacy review: a period the privacy policy can promise).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is written at every sign-in, with <c>LastLoginOn</c> (<c>StommeSiteAuthenticationBuilderExtensions</c>),
    /// so its age is the age of that sign-in. Past the period it is set to empty - the value the host already writes when
    /// no address is known. <c>LastLoginOn</c> stays: it is when, not where, and the user administration shows it.
    /// </para>
    /// <para>
    /// A user is the TENANT's, a setting is the SITE's: the pass takes the users holding a role on the site it is run
    /// for, so a person on two sites has their address cleared by the shorter of the two periods. A value of 0 clears every
    /// recorded address at the next run.
    /// </para>
    /// </remarks>
    public sealed class LastIPAddressRetentionPurger(IUserRepository users, IUserRoleRepository userRoles) : IRetentionPurger
    {
        public const string RetentionSettingKey = "LastIPAddressRetention";

        public const int DefaultRetentionDays = 90;

        public string SettingKey => RetentionSettingKey;

        public int DefaultDays => DefaultRetentionDays;

        public string DisplayName => "Last IP Addresses";

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, days));
            var userIds = (await userRoles.GetUserRolesAsync(siteId)).Select(role => role.UserId).Distinct().ToList();
            var cleared = 0;
            foreach (var userId in userIds)
            {
                var user = await users.GetUserAsync(userId);
                // Recorded at the last sign-in, or at registration for a user who has never signed in.
                if (user is null || string.IsNullOrEmpty(user.LastIPAddress) || (user.LastLoginOn ?? user.CreatedOn) >= cutoff)
                {
                    continue;
                }

                user.LastIPAddress = string.Empty;
                await users.UpdateUserAsync(user);
                cleared++;
            }

            return cleared;
        }
    }
}
