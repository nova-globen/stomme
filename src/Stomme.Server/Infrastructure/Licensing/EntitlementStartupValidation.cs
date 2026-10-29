#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Infrastructure.Licensing
{
    /// <summary>
    /// The POST-BUILD validation of the entitlement leg. Called
    /// FIRST in <c>UseStommeAsync</c>: after the container is built (so every module's registrations are visible) and
    /// before <c>app.StartAsync</c> starts <c>FeatureSeeder</c> — whose catch-all would otherwise swallow a failure raised
    /// in the lazily built <c>FeatureRegistry</c> and leave every later flag evaluation to 500.
    /// </summary>
    public static class EntitlementStartupValidation
    {
        /// <summary>
        /// Throws ONE <see cref="InvalidOperationException"/> listing every violation of: (a) the provider rule — zero
        /// <see cref="IEntitlementProvider"/> unless <c>Deployment:Mode=OnPrem</c>, and then exactly one whose runtime type IS
        /// <see cref="OfflineLicenceEntitlementProvider"/> (a module that removes and replaces it fails); (b) descriptor
        /// agreement — every <see cref="IFeatureContributor"/> descriptor for a key (grouped case-insensitively, as the
        /// registry does) must be EQUAL in every field, because the registry keeps the first and silently drops the rest;
        /// byte-identical duplicates are tolerated; (c) every non-null <see cref="FeatureDescriptor.RequiredEntitlement"/> is a
        /// canonical capability (<see cref="LicencePayload.IsCanonicalCapability"/>).
        /// </summary>
        /// <remarks>
        /// Reads contributors, NOT <see cref="IFeatureRegistry"/>: the registry is first-wins and hides exactly the conflicts
        /// this looks for. Resolves inside a created scope (the provider is scoped; ValidateScopes would reject a root resolve).
        /// </remarks>
        public static void Validate(IServiceProvider services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var onPrem = DeploymentMode.Read(configuration) == DeploymentModeKind.OnPrem;
            var violations = new List<string>();

            using var scope = services.CreateScope();

            var providers = scope.ServiceProvider.GetServices<IEntitlementProvider>().ToList();
            var providerNames = string.Join(", ", providers.Select(p => p.GetType().FullName));
            if (!onPrem && providers.Count != 0)
            {
                violations.Add(
                    $"{providers.Count} {nameof(IEntitlementProvider)} registration(s) found ({providerNames}) but " +
                    $"'{DeploymentMode.ConfigKey}' is not OnPrem: no entitlement provider may be registered outside OnPrem.");
            }
            else if (onPrem && (providers.Count != 1 || providers[0].GetType() != typeof(OfflineLicenceEntitlementProvider)))
            {
                violations.Add(
                    $"'{DeploymentMode.ConfigKey}' is OnPrem, so exactly one {nameof(IEntitlementProvider)} of type " +
                    $"{typeof(OfflineLicenceEntitlementProvider).FullName} must be registered; found {providers.Count}" +
                    (providers.Count == 0 ? "." : $": {providerNames}."));
            }

            var descriptors = scope.ServiceProvider.GetServices<IFeatureContributor>()
                .SelectMany(contributor => contributor.Features ?? Array.Empty<FeatureDescriptor>())
                .ToList();

            foreach (var group in descriptors.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var distinct = group.Distinct().ToList();
                if (distinct.Count > 1)
                {
                    violations.Add(
                        $"Feature '{group.Key}' is declared {distinct.Count} different ways (the registry would silently keep " +
                        $"the first): {string.Join("; ", distinct.Select(d => d.ToString()))}.");
                }
            }

            foreach (var descriptor in descriptors
                         .Where(d => d.RequiredEntitlement is not null && !LicencePayload.IsCanonicalCapability(d.RequiredEntitlement))
                         .Distinct())
            {
                violations.Add(
                    $"Feature '{descriptor.Key}' requires entitlement '{descriptor.RequiredEntitlement}', which is not a " +
                    $"canonical capability key ({LicencePayload.CanonicalCapabilityPattern}).");
            }

            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    "Entitlement startup validation failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, violations.Select(v => " - " + v)));
            }
        }
    }
}