using Stomme.Engine.Features.Services;
using Stomme.Engine.Tenants.Features;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Tenants.Extensions;

/// <summary>
/// Extensions for <see cref="IModule"/> that installs the <see cref="TenantsFeature"/> feature.
/// </summary>
[UsedImplicitly]
public static class ModuleExtensions
{
    /// <summary>
    /// Installs and configures the <see cref="TenantsFeature"/> feature.
    /// </summary>
    [UsedImplicitly]
    public static IModule UseTenants(this IModule module, Action<TenantsFeature>? configure = null)
    {
        module.Configure(configure);
        return module;
    }

    extension(TenantsFeature feature)
    {
        /// <summary>
        /// Installs and configures the <see cref="TenantManagementFeature"/> feature.
        /// </summary>
        [UsedImplicitly]
        public TenantsFeature UseTenantManagement(Action<TenantManagementFeature>? configure = null)
        {
            feature.Module.Configure(configure);
            return feature;
        }
    }
}