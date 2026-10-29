#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Mcp.Persistence;

namespace Stomme.Modules.Mcp.Infrastructure
{
    /// <summary>
    /// Ages out the MCP audit ledger, drained per site by <c>PurgeJob</c> (on the contributed-pass seam).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It contributes a PASS, it does not ship a job.</b> Retention is the same shape for every module - a per-site
    /// setting in whole days, a delete older than that, a line in the notes - and the framework already does it five
    /// times. A second Job row would be a second schedule an operator can disable without knowing what they switched
    /// off, which is exactly the parallel surface this repository refuses.
    /// </para>
    /// <para>
    /// <b>THE FLOOR IS THE POINT, and it is this pass's to set</b> (the seam clamps nothing, because a bound is a
    /// statement about the DATA). This ledger is a security record: it is where a denied call, a call that needed an
    /// approval nobody can give, and a tool that threw are told apart - the wire shows one answer for all of them.
    /// A per-site setting of <c>0</c> would therefore be a way to erase that record through a text box, from inside
    /// the product, leaving nothing behind that says it happened. So a value below
    /// <see cref="MinimumRetentionDays"/> is READ AS the minimum, and the ledger cannot be emptied by configuration.
    /// An operator who genuinely wants the rows gone deletes them where deleting them leaves a trace of its own.
    /// </para>
    /// <para>
    /// There is no ceiling: an install with a reason to keep years of it may, and the rows are small - a subject, an
    /// outcome, a principal and a hash, never the arguments.
    /// </para>
    /// </remarks>
    public sealed class McpAuditRetentionPurger : IRetentionPurger
    {
        /// <summary>The per-site setting holding the ledger's retention in whole days.</summary>
        public const string RetentionSettingKey = "McpAuditRetention";

        public const int DefaultRetentionDays = 90;

        /// <summary>The shortest retention this pass will apply, whatever the setting says. See the type remarks.</summary>
        public const int MinimumRetentionDays = 7;

        private readonly IMcpAuditRepository _audit;

        public McpAuditRetentionPurger(IMcpAuditRepository audit) => _audit = audit;

        public string SettingKey => RetentionSettingKey;

        public int DefaultDays => DefaultRetentionDays;

        public string DisplayName => "MCP Audit";

        /// <summary><paramref name="days"/>, never below <see cref="MinimumRetentionDays"/>.</summary>
        public static int ClampRetention(int days) => days < MinimumRetentionDays ? MinimumRetentionDays : days;

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-ClampRetention(days));
            var purged = 0;

            foreach (var entry in await _audit.ListOlderThanAsync(siteId, cutoff, cancellationToken).ConfigureAwait(false))
            {
                await _audit.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
                purged++;
            }

            return purged;
        }
    }
}
