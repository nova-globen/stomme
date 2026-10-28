#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Makes a declared permission key reach every site on the BOOT path, rather than whenever something
    /// happens to read that site's module definitions (BL-103).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes has teeth because the bridge fails closed.</b>
    /// <c>ReconcileDeclaredKeyPermissionsAsync</c> — the thing that seeds a declared key's permission rows for a
    /// site — runs INSIDE <c>GetModuleDefinitionsAsync</c>, whose result is cached per alias at
    /// <c>TimeSpan.MaxValue</c>. So a key shipped by a newer release has no rows on an existing site until
    /// something reads that site's module definitions, and a key with no rows is DENIED to everyone, including
    /// administrators. The feature is invisible until an admin page happens to load, and there is no error to
    /// explain it.
    /// </para>
    /// <para>
    /// <b>The fix is to stop relying on a side effect of a cached read.</b> This reconciler asks each site for
    /// its module definitions once on the boot/upgrade path, which performs exactly the reconciliation that was
    /// already there — eagerly, before any request, and for every site rather than the ones someone visits. It
    /// adds no new seeding logic: doing that would mean two implementations of "what rows does this key need",
    /// which is how the two drift apart.
    /// </para>
    /// <para>
    /// <b>A deliberate revocation survives this, and that is not an accident.</b> Revoking a key is an
    /// Administrators DENY row rather than the absence of rows, precisely because absence is the state this
    /// reconciliation re-seeds. Deleting the rows would hand the key back on the next boot.
    /// </para>
    /// <para>
    /// <b>Known residue:</b> a site created at RUNTIME still seeds on its first module-definition read, because
    /// this runs at boot. That is the behaviour that already existed and the window is small (an admin who has
    /// just created a site is reading its pages), but it is a window, and it closes when site creation grows its
    /// own hook.
    /// </para>
    /// </remarks>
    public sealed class DeclaredPermissionKeyReconciler : ISiteContentReconciler
    {
        public string Name => "Declared permission keys";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            var definitions = context.Services.GetRequiredService<IModuleDefinitionRepository>();
            var logger = context.Services.GetRequiredService<ILogger<DeclaredPermissionKeyReconciler>>();

            // The READ is the reconciliation: GetModuleDefinitionsAsync seeds each declared key's rows for this
            // site as a side effect. Calling it here is what makes that eager instead of incidental.
            var count = (await definitions.GetModuleDefinitionsAsync(context.Site.SiteId)).Count();

            logger.LogDebug(
                "Reconciled declared permission keys for site {SiteId} across {Count} module definition(s).",
                context.Site.SiteId, count);
        }
    }
}
