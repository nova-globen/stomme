#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.IdentityAudit.Persistence;

namespace Stomme.Modules.IdentityAudit.Infrastructure
{
    /// <summary>
    /// Ages out the identity audit ledger, drained per site by <c>PurgeJob</c> (the contributed-pass seam).
    /// </summary>
    /// <remarks>
    /// <b>The floor is the point</b> - the MCP ledger's argument: this is where a lockout, a password reset or a
    /// role grant is recorded, so a per-site setting of <c>0</c> must not be a way to erase that record through a text
    /// box. A value below <see cref="MinimumRetentionDays"/> is read AS the minimum. There is no ceiling. Erasure of a
    /// person's rows under a right-to-erasure request is a separate decision, not this pass's.
    /// </remarks>
    public sealed class IdentityAuditRetentionPurger(IIdentityAuditRepository audit) : IRetentionPurger
    {
        public const string RetentionSettingKey = "IdentityAuditRetention";

        public const int DefaultRetentionDays = 365;

        public const int MinimumRetentionDays = 30;

        public string SettingKey => RetentionSettingKey;

        public int DefaultDays => DefaultRetentionDays;

        public string DisplayName => "Identity Audit";

        public static int ClampRetention(int days) => days < MinimumRetentionDays ? MinimumRetentionDays : days;

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-ClampRetention(days));
            var purged = 0;
            foreach (var entry in await audit.ListOlderThanAsync(siteId, cutoff, cancellationToken).ConfigureAwait(false))
            {
                await audit.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
                purged++;
            }

            return purged;
        }
    }
}
