using Stomme.Engine.Common.Features;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Attributes;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Tenants.Mediator.Tasks;
using Stomme.Engine.Tenants.Options;
using Stomme.Engine.Tenants.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Tenants.Features;

/// <summary>
/// Configures multi-tenancy features.
/// </summary>
[DependencyOf(typeof(MultitenancyFeature))]
public class TenantsFeature(IModule serviceConfiguration) : FeatureBase(serviceConfiguration)
{
    /// <summary>
    /// Configures the Tenants options.
    /// </summary>
    private Action<MultitenancyOptions> MultitenancyOptions { get; set; } = _ => { };
    
    private Action<TenantsOptions> TenantsOptions { get; set; } = _ => { };
    
    public TenantsFeature ConfigureMultitenancy(Action<MultitenancyOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }
    
    public TenantsFeature ConfigureTenants(Action<TenantsOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }
    
    public void UseConfigurationBasedTenantsProvider(Action<TenantsOptions> configure)
    {
        ConfigureTenants(configure);
        Module.Configure<MultitenancyFeature>(feature => feature.UseTenantsProvider<ConfigurationTenantsProvider>());
    }
    
    public void UseStoreBasedTenantsProvider()
    {
        Module.Configure<MultitenancyFeature>(feature => feature.UseTenantsProvider<StoreTenantsProvider>());
    }

    public override void ConfigureHostedServices()
    {
        Module.ConfigureHostedService<SetupMediatorPipelines>();
    }

    /// <inheritdoc />
    public override void Apply()
    {
        Services.Configure(MultitenancyOptions);
        Services.Configure<TenantsOptions>(options => options.IsEnabled = true);

        Services
            .AddScoped<ITenantResolverPipelineInvoker, DefaultTenantResolverPipelineInvoker>()
            .AddScoped<ITenantResolver, DefaultTenantResolver>();
    }
}