using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Tenants.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Tenants;

/// <summary>
/// Resolves the tenant using a pipeline of resolvers.
/// </summary>
public class DefaultTenantResolverPipelineInvoker(
    IOptions<MultitenancyOptions> options,
    ITenantsProvider tenantsProvider,
    IServiceProvider serviceProvider,
    ILogger<DefaultTenantResolverPipelineInvoker> logger) : ITenantResolverPipelineInvoker
{
    public async Task<Tenant?> InvokePipelineAsync(CancellationToken cancellationToken = default)
    {
        var resolutionPipeline = options.Value.TenantResolverPipelineBuilder.Build(serviceProvider);
        var tenants = await tenantsProvider.ListAsync(cancellationToken).ConfigureAwait(false);
        var tenantsDictionary = tenants.ToDictionary(x => x.Id.NormalizeTenantId());
        var context = new TenantResolverContext(tenantsDictionary, cancellationToken);

        foreach (var resolver in resolutionPipeline)
        {
            var result = await resolver.ResolveAsync(context).ConfigureAwait(false);

            if (result.IsResolved)
            {
                var resolvedTenantId = result.ResolveTenantId();

                if (tenantsDictionary.TryGetValue(resolvedTenantId, out var tenant))
                    return tenant;

                logger.LogWarning("Tenant with ID {TenantId} was resolved but could not be found in the tenant store.", resolvedTenantId);
                return null;
            }
        }

        return null;
    }
}