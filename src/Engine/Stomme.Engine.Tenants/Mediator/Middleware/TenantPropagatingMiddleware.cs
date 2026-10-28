using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Mediator.Middleware.Command;
using Stomme.Engine.Mediator.Middleware.Command.Contracts;
using JetBrains.Annotations;

namespace Stomme.Engine.Tenants.Mediator.Middleware;

/// <summary>
/// Middleware that ensures tenant context is propagated through the request pipeline.
/// </summary>
[UsedImplicitly]
public class TenantPropagatingMiddleware(CommandMiddlewareDelegate next, ITenantScopeFactory tenantScopeFactory, ITenantService tenantService) : ICommandMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(CommandContext context)
    {
        if (context.Headers.TryGetValue(TenantHeaders.TenantIdKey, out var tenantIdVal))
        {
            var tenantId = (string)tenantIdVal;
            var tenant = await tenantService.FindAsync(tenantId).ConfigureAwait(false);
            var tenantScope = tenantScopeFactory.CreateScope(tenant);
            await using var _tenantScopeAsyncDisposal = tenantScope.ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}