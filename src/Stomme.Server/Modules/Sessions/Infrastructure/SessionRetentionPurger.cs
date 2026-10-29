#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Sessions.Persistence;

namespace Stomme.Modules.Sessions.Infrastructure
{
    /// <summary>
    /// Deletes ENDED sessions - expired or revoked - once they have been over for the retention period, drained per site
    /// by <c>PurgeJob</c> (a contributed-pass seam). An active session is never touched: retention counts from
    /// the moment a session ended, not from when it began.
    /// </summary>
    /// <remarks>
    /// A floor of one day: an ended row is what makes a revoked cookie's NEXT request fail with "session ended" rather
    /// than "no such session" - both refuse, but only the row tells the user's list of sessions and the audit what happened.
    /// </remarks>
    public sealed class SessionRetentionPurger(IUserSessionRepository sessions) : IRetentionPurger
    {
        public const string RetentionSettingKey = "SessionRetention";

        public const int DefaultRetentionDays = 30;

        public const int MinimumRetentionDays = 1;

        public string SettingKey => RetentionSettingKey;

        public int DefaultDays => DefaultRetentionDays;

        public string DisplayName => "Sessions";

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(MinimumRetentionDays, days));
            var purged = 0;
            foreach (var session in await sessions.ListEndedBeforeAsync(siteId, cutoff, cancellationToken).ConfigureAwait(false))
            {
                await sessions.DeleteAsync(session, cancellationToken).ConfigureAwait(false);
                purged++;
            }

            return purged;
        }
    }
}
