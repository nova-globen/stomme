using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Common.Multitenancy;

public class DefaultTenantFinder(IServiceScopeFactory scopeFactory) : ITenantFinder
{
    public async Task<Tenant?> FindByIdAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var _scopeAsyncDisposal = scope.ConfigureAwait(false);
        var tenantsProvider = scope.ServiceProvider.GetRequiredService<ITenantsProvider>();
        return await tenantsProvider.FindByIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }
}