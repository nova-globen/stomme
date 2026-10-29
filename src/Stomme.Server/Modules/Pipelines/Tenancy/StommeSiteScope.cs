#nullable enable
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines;
using Stomme.Infrastructure;
using Stomme.Shared;

namespace Stomme.Modules.Pipelines.Tenancy
{
    /// <summary>
    /// The bridge's REVERSE direction: a step that needs Stomme's own site context (the notification dispatcher, the
    /// framework's repositories, the cache keyed by alias) enters the Stomme alias of the ENGINE tenant it runs
    /// under. A request already has one (the middleware entered the engine tenant FROM the alias); a run dispatched to
    /// the engine's worker has only the engine tenant, and the dispatcher refuses in that state ("a job must set the
    /// site's alias before dispatching" - its own words). The rule is the one every headless job here follows: enter
    /// the site through <c>ITenantManager.SetAliasAsync</c>, for the CACHE as much as the path.
    /// </summary>
    /// <remarks>Public because an app's own pipeline step or recurring task needs the same entry, and copied it by hand.</remarks>
    public static class StommeSiteScope
    {
        /// <summary>Enters the step's site when this scope has none; false when there is no engine tenant to enter.</summary>
        public static Task<bool> EnsureSiteAsync(StepExecutionContext context) =>
            EnsureSiteAsync(context.GetService<SiteState>(), context.GetService<ITenantAccessor>(), context.GetService<ITenantManager>());

        /// <summary>
        /// Enters the site of the engine tenant a scope runs under - for a recurring task, which has no step (the
        /// watched mailboxes); false when there is no engine tenant to enter.
        /// </summary>
        public static Task<bool> EnsureSiteAsync(System.IServiceProvider services) =>
            EnsureSiteAsync(services.GetService(typeof(SiteState)) as SiteState, services.GetService(typeof(ITenantAccessor)) as ITenantAccessor,
                services.GetService(typeof(ITenantManager)) as ITenantManager);

        private static async Task<bool> EnsureSiteAsync(SiteState? siteState, ITenantAccessor? tenants, ITenantManager? tenantManager)
        {
            if (siteState?.Alias is not null)
            {
                return true;
            }

            if (!EngineTenantIds.TryParse(tenants?.TenantId, out var tenantId, out var siteId) || tenantManager is null)
            {
                return false;
            }

            await tenantManager.SetAliasAsync(tenantId, siteId).ConfigureAwait(false);
            return true;
        }
    }
}
