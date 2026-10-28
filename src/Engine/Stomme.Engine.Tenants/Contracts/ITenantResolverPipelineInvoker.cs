using Stomme.Engine.Common.Multitenancy;

namespace Stomme.Engine.Tenants;

public interface ITenantResolverPipelineInvoker
{
    Task<Tenant?> InvokePipelineAsync(CancellationToken cancellationToken = default);
}