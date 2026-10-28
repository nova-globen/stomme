#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// BL-172 (D106 residue): a DATA snapshot taken before a framework upgrade touches anything, so a failed upgrade is
    /// reversible - the Updater backs up files only. <see cref="InstallationManager.UpgradeFramework"/> asks every registered
    /// implementation and refuses the upgrade when one answers with a reason.
    /// </summary>
    public interface IPreUpgradeSnapshot
    {
        /// <summary>Null when the upgrade may go ahead (a snapshot was taken, or none is configured); otherwise why it must not.</summary>
        Task<string?> TakeAsync(CancellationToken cancellationToken);
    }
}
