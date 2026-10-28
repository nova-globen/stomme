#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Health.Infrastructure
{
    /// <summary>
    /// Ages out the Hour and Day rollups, drained per site by <see cref="PurgeJob"/> (D104 item 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The Minute series is NOT purged here</b>, and that is deliberate. It is trimmed ON WRITE by the probe
    /// pass, which deletes the row 48 hours behind the one it is writing. A monitoring feature whose storage is
    /// bounded only by a job an operator can DISABLE is not bounded at all — and disabling the purge job is a
    /// perfectly reasonable thing for an operator to do, so the live series must survive it.
    /// </para>
    /// <para>
    /// <b>Alert-state rows are NEVER purged.</b> There is exactly one per subject per scope, so they do not grow
    /// with time, and they carry the history an operator reads to answer "is this chronic?" — the raise count, the
    /// last acknowledgement. Deleting them to save a few rows would throw away the only durable record that
    /// anyone was ever told about an outage.
    /// </para>
    /// <para>
    /// The retention is clamped INSIDE this pass. The job deliberately clamps nothing beyond refusing a negative,
    /// because a bound is a statement about the data: zero days means "purge everything older than now", which is
    /// meaningful for an event log and exactly wrong for the history an admin page exists to show.
    /// </para>
    /// </remarks>
    public sealed class HealthRetentionPurger : IRetentionPurger
    {
        private readonly IHealthRollupRepository _rollups;
        private readonly IUnitOfWork _unitOfWork;

        public HealthRetentionPurger(IHealthRollupRepository rollups, IUnitOfWork unitOfWork)
        {
            _rollups = rollups;
            _unitOfWork = unitOfWork;
        }

        public string SettingKey => HealthSettings.RetentionKey;

        public int DefaultDays => HealthSettings.DefaultRetentionDays;

        public string DisplayName => "Health Rollups";

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-HealthSettings.ClampRetention(days));
            var purged = 0;

            foreach (var window in new[] { HealthRollupWindow.Hour, HealthRollupWindow.Day })
            {
                var rows = await _rollups.ListOlderThanAnywhereAsync(siteId, window, cutoff, cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    await _rollups.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
                    purged++;
                }
            }

            if (purged > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return purged;
        }
    }
}
