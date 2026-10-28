#nullable enable
using System;
using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>The composition half of D105 "The gate".</summary>
    public static class LicensingServiceCollectionExtensions
    {
        /// <summary>
        /// Composes the offline licence ring ONLY under <c>Deployment:Mode=OnPrem</c>. An absent, empty or <c>Hosted</c> mode
        /// registers NOTHING — the descriptor list is identical to a stock install's (pinned by
        /// <c>DeploymentModeCompositionTests</c>), so FeatureManagement's entitlement leg stays permissive. Any other value
        /// throws here, at composition, naming <c>Deployment:Mode</c> (<see cref="DeploymentMode.Read"/>).
        /// </summary>
        /// <remarks>
        /// No count check lives here: this runs after the module scan, so it cannot see a module registering a provider
        /// later, and a count of one would not prove the provider is ours. That is
        /// <see cref="EntitlementStartupValidation"/>'s job, post-build.
        /// </remarks>
        public static IServiceCollection AddStommeLicensing(this IServiceCollection services, IConfigurationRoot configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            if (DeploymentMode.Read(configuration) != DeploymentModeKind.OnPrem)
            {
                return services;
            }

            // The clock is resolved, not registered: a TryAddSingleton(TimeProvider.System) here was MEASURED to move the
            // host's TimeProvider registration — the framework TryAdds one later in AddStomme (beside DataProtection), so
            // ours would pre-empt it and the OnPrem descriptor list would differ from Hosted by more than licensing. A test
            // or host that registers its own TimeProvider (before or after this call) still wins; none → the system clock.
            services.AddSingleton(sp => new LicenceState(sp.GetService<TimeProvider>() ?? TimeProvider.System));
            services.AddScoped<IEntitlementProvider, OfflineLicenceEntitlementProvider>();
            return services;
        }
    }
}