#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Application;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Metering.Infrastructure
{
    /// <summary>
    /// Takes the person out of the usage ledgers - AI usage and metered usage - once they are older than the site's
    /// <c>MeteringUsageRetention</c>, when METERING IS OFF (BL-275).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second pass.</b> With <c>Metering:Enabled</c> on, <c>UsageRollupJob</c> already deletes ledger rows
    /// past that retention, after folding them into rollups that carry no user id. But the recorders are registered
    /// unconditionally, and the switch is OFF by default: a stock install that uses the AI assistant writes a ledger
    /// row per call - its user id, provider, model, tokens, cost - and, with the rollup job switched off, nothing ever
    /// aged it out (found 2026-10-30, reading the registration and the switch). <c>PurgeJob</c> runs on every install, so this pass closes that gap.
    /// </para>
    /// <para>
    /// <b>The user id is NULLED, the row stays</b> - account deletion's rule (<see cref="MeteringUserErasure"/>): with
    /// metering off there are no rollups, so the ledger IS the count, and a privacy period promises the person is gone,
    /// not that the site forgets how much it used. With metering on, this pass does nothing and the rollup job's deletion
    /// stands. The retention is read with metering's own floor (<see cref="SettingRepositoryMeteringSiteSettings.MinimumUsageRetentionDays"/>),
    /// so both paths agree on the period.
    /// </para>
    /// </remarks>
    public sealed class MeteringLedgerRetentionPurger(
        IServiceProvider services,
        IUsageRecordRepository usage,
        IAiUsageRecordRepository aiUsage,
        IUnitOfWork unitOfWork) : IRetentionPurger
    {
        public string SettingKey => MeteringSiteSettingNames.UsageRetention;

        public int DefaultDays => SettingRepositoryMeteringSiteSettings.DefaultUsageRetentionDays;

        public string DisplayName => "Usage Ledger User Ids";

        public async Task<int> PurgeAsync(int siteId, int days, CancellationToken cancellationToken = default)
        {
            if (MeteringSwitch.IsEnabled(HostConfiguration.Resolve(services)))
            {
                return 0; // the rollup job ages the ledgers out, and keeps their counts in rollups
            }

            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(days, SettingRepositoryMeteringSiteSettings.MinimumUsageRetentionDays));
            var forgotten = 0;
            foreach (var row in (await usage.ListOlderThanAsync(siteId, cutoff, cancellationToken)).Where(row => row.UserId is not null))
            {
                row.ForgetUser();
                await usage.UpdateAsync(row, cancellationToken);
                forgotten++;
            }

            foreach (var row in (await aiUsage.ListOlderThanAsync(siteId, cutoff, cancellationToken)).Where(row => row.UserId is not null))
            {
                row.ForgetUser();
                await aiUsage.UpdateAsync(row, cancellationToken);
                forgotten++;
            }

            if (forgotten > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return forgotten;
        }
    }
}
