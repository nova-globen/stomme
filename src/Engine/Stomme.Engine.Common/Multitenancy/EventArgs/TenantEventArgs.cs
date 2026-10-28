namespace Stomme.Engine.Common.Multitenancy;

public record TenantEventArgs(Tenant Tenant, TenantScope TenantScope, CancellationToken CancellationToken);