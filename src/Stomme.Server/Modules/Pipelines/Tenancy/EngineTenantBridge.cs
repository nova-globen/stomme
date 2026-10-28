#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Extensions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Pipelines.Tenancy
{
    /// <summary>
    /// Enters the engine tenant of a Stomme site for the current flow (BL-95 increment (a), the tenant bridge).
    /// The engine keeps its current tenant in an ambient accessor that follows the async flow, and its background
    /// dispatcher carries that tenant's id to the worker as a command header - where the Tenants feature's middleware
    /// restores it ONLY if the engine's tenant service can find the id. So entering a site is two steps, and the split
    /// is load-bearing: <see cref="ResolveAsync"/> finds the tenant (refreshing the service's list once when it is not
    /// there - a site created after boot), and <see cref="Enter"/> pushes it SYNCHRONOUSLY in the caller's own frame.
    /// An ambient value set inside an awaited method never reaches the method's caller (measured: an async "enter"
    /// pushed the tenant and the request that awaited it ran under the default tenant), so the push is never behind
    /// an await. A request enters through <see cref="EngineTenantMiddleware"/>; a job or a test enters through this.
    /// </summary>
    public interface IEngineTenantBridge
    {
        /// <summary>The engine tenant of the site, from the engine's tenant service; an unregistered placeholder when it lists no such site.</summary>
        Task<Tenant> ResolveAsync(int tenantId, int siteId, CancellationToken cancellationToken = default);

        /// <summary>The engine tenant of the current <c>SiteState.Alias</c>; null when there is no alias (the installer state).</summary>
        Task<Tenant?> ResolveCurrentSiteAsync(CancellationToken cancellationToken = default);

        /// <summary>Pushes the tenant for the caller's flow until the returned scope is disposed. Call it in the frame that does the work, never behind an await.</summary>
        IDisposable Enter(Tenant tenant);
    }

    internal sealed class EngineTenantBridge(ITenantAccessor accessor, ITenantService tenants, SiteState siteState, ILogger<EngineTenantBridge> logger) : IEngineTenantBridge
    {
        public async Task<Tenant> ResolveAsync(int tenantId, int siteId, CancellationToken cancellationToken = default)
        {
            var id = EngineTenantIds.For(tenantId, siteId);
            var tenant = await tenants.FindAsync(id, cancellationToken);
            if (tenant is null)
            {
                await tenants.RefreshAsync(cancellationToken);
                tenant = await tenants.FindAsync(id, cancellationToken);
            }

            if (tenant is null)
            {
                // Not a site the engine lists (deleted, or a listing failure): the flow still runs under that id, so
                // what it persists is scoped to the site and never to the default tenant; only a background dispatch
                // would fall back to the default tenant on the worker, and the log says so once per miss.
                logger.LogWarning("Engine tenant {EngineTenantId} is not among the engine's tenants; the flow runs under it unregistered.", id);
                tenant = new Tenant { Id = id, Name = id };
            }

            return tenant;
        }

        public Task<Tenant?> ResolveCurrentSiteAsync(CancellationToken cancellationToken = default)
        {
            var alias = siteState.Alias;
            return alias is null ? Task.FromResult<Tenant?>(null) : ResolveAsync(alias.TenantId, alias.SiteId, cancellationToken)!;
        }

        public IDisposable Enter(Tenant tenant) => accessor.PushContext(tenant);
    }

    /// <summary>
    /// After Stomme's tenant resolution has named the request's site, the rest of the request - components, controllers,
    /// the engine's own services - runs inside that site's engine tenant.
    /// </summary>
    internal sealed class EngineTenantMiddleware(RequestDelegate next)
    {
        public async Task Invoke(HttpContext context)
        {
            var alias = context.GetAlias();
            if (alias is null)
            {
                await next(context);
                return;
            }

            var bridge = context.RequestServices.GetRequiredService<IEngineTenantBridge>();
            var tenant = await bridge.ResolveAsync(alias.TenantId, alias.SiteId, context.RequestAborted);
            using (bridge.Enter(tenant))
            {
                await next(context);
            }
        }
    }
}
