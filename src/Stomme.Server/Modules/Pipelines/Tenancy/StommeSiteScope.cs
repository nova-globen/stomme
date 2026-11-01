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
        /// <summary>The run property naming the user who started a run by hand (BL-280).</summary>
        public const string StartedByUserProperty = "stomme:startedByUserId";

        /// <summary>The published version's property naming the user who published it (BL-280).</summary>
        public const string PublishedByUserProperty = "stomme:publishedByUserId";

        /// <summary>
        /// Enters the step's site when this scope has none - and, in a run with no request behind it, acts as the run's user
        /// (BL-280, the owner's answer: whoever started it by hand, else whoever published the version it runs). So a step's
        /// <c>[Requires]</c> command is decided by that user's rights on the site, read from the stored rows each time; a run
        /// with neither recorded acts as no one, as before. False when there is no engine tenant to enter.
        /// </summary>
        public static async Task<bool> EnsureSiteAsync(StepExecutionContext context)
        {
            var siteState = context.GetService<SiteState>();
            if (!await EnsureSiteAsync(siteState, context.GetService<ITenantAccessor>(), context.GetService<ITenantManager>()).ConfigureAwait(false))
            {
                return false;
            }

            var run = context.GetService<Stomme.Infrastructure.Security.RunPrincipal>();
            var accessor = context.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            if (run is { IsSet: false } && accessor?.HttpContext is null && siteState?.Alias?.SiteId is > 0 and var siteId
                && RunUser(context) is > 0 and var userId)
            {
                run.ActAs(siteId, userId);
            }

            return true;
        }

        /// <summary>Who the run acts as: the user who started it by hand, else the version's publisher; null when neither is recorded.</summary>
        public static int? RunUser(StepExecutionContext context)
        {
            var execution = context.PipelineExecutionContext;
            if (execution.Properties.TryGetValue(StartedByUserProperty, out var startedBy) && AsUserId(startedBy) is { } starter)
            {
                return starter;
            }

            return execution.Pipeline.CustomProperties.TryGetValue(PublishedByUserProperty, out var publishedBy) ? AsUserId(publishedBy) : null;
        }

        private static int? AsUserId(object? value) =>
            int.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

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
