using Stomme.FeatureManagement.Application;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Infrastructure;

/// <summary>
/// The fallback <see cref="IFeatureContextProvider"/>: anonymous, no roles. Registered with
/// <c>TryAdd</c> so a host that knows its principal (Stomme's server registers an HTTP-user-backed
/// provider) wins. Keeps the module evaluable in a background job or a test with no principal in scope.
/// </summary>
internal sealed class AnonymousFeatureContextProvider : IFeatureContextProvider
{
    public FeatureContext GetContext() => FeatureContext.Anonymous;
}

/// <summary>
/// The fallback <see cref="ITenantContext"/>: the default tenant, no system scope. Registered with
/// <c>TryAdd</c> so the host's real tenancy adapter wins. Backlog item 01 did not wire an
/// <see cref="ITenantContext"/> over Stomme's Alias/Tenant model, so this keeps the flag store usable
/// (single logical tenant) until it does.
/// </summary>
internal sealed class DefaultTenantContext : ITenantContext
{
    public TenantId? CurrentTenantId => TenantId.Default;

    public bool IsSystemScope => false;
}
