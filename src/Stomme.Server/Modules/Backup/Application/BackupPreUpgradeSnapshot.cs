#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Application
{
    /// <summary>
    /// Before a framework upgrade, a snapshot of the whole installation tagged <c>pre-upgrade</c>, under the SCHEDULED
    /// passphrase (<c>Backup:Schedule:Passphrase</c>) - the upgrade runs unattended, so no operator is there to type one.
    /// </summary>
    /// <remarks>
    /// Fail-closed once the operator has opted in: with <c>Backup:Enabled</c> off it answers "go ahead" (a stock install
    /// upgrades exactly as before); with it on, a missing passphrase, a snapshot already being taken, or a failed snapshot
    /// REFUSES the upgrade - an upgrade the operator believes is reversible must not run when it is not.
    /// </remarks>
    public sealed class BackupPreUpgradeSnapshot(IServiceScopeFactory scopes, BackupOptions options, ILogger<BackupPreUpgradeSnapshot> logger) : IPreUpgradeSnapshot
    {
        public async Task<string?> TakeAsync(CancellationToken cancellationToken)
        {
            if (!options.Enabled)
            {
                return null;
            }

            var passphrase = options.Schedule.ResolvePassphrase();
            if (BackupEngine.PassphraseProblem(passphrase) is not null)
            {
                return $"Backup is enabled but {options.Schedule.SettingName} is not set (12+ characters), so no data snapshot can be taken before the upgrade.";
            }

            using var scope = scopes.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<BackupEngine>();
            if (await engine.IsSnapshotInProgressAsync(BackupScheduleJob.Lease, DateTime.UtcNow))
            {
                return "A backup snapshot is being taken; upgrade when it has finished.";
            }

            try
            {
                var id = await engine.CreateSnapshotAsync(passphrase, RetentionTag.PreUpgrade, cancellationToken);
                logger.LogInformation("Pre-upgrade snapshot {SnapshotId} taken.", id);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The pre-upgrade snapshot failed.");
                return $"The pre-upgrade data snapshot failed ({ex.GetType().Name}); see the log.";
            }
        }
    }
}
