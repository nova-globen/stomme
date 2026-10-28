namespace Stomme.Engine.Common.Multitenancy;

public record TenantDeactivatedEventArgs(Tenant Tenant, TenantScope TenantScope, CancellationToken CancellationToken) : TenantEventArgs(Tenant, TenantScope, CancellationToken);