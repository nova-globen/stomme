#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;

// Lifted from the predecessor platform (BL-97 increment 3): the payload mapping is unchanged; only the namespaces moved.
namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// Resolution over the DI-registered <see cref="ICloudCostProvider"/> adapters, keyed by
    /// <see cref="CloudProvider"/>. The infrastructure clouds (Azure/AWS/GCP) register by default; SaaS
    /// sources (Datadog, Snowflake, GitHub, Microsoft 365, Salesforce) and
    /// <see cref="CloudProvider.GenericRest"/> plug in through the same contract (see
    /// <see cref="SaasCostProvider"/>) once their cost connector definitions land. Resolving a provider with
    /// no registered adapter throws; <see cref="TryResolve"/> returns false.
    /// </summary>
    public sealed class CloudCostProviderRegistry : ICloudCostProviderRegistry
    {
        private readonly IReadOnlyDictionary<CloudProvider, ICloudCostProvider> _byProvider;

        public CloudCostProviderRegistry(IEnumerable<ICloudCostProvider> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);
            _byProvider = providers.ToDictionary(provider => provider.Provider);
        }

        public ICloudCostProvider Resolve(CloudProvider provider) =>
            _byProvider.TryGetValue(provider, out var costProvider)
                ? costProvider
                : throw new InvalidOperationException($"No cloud cost provider is registered for '{provider}'.");

        public bool TryResolve(CloudProvider provider, [NotNullWhen(true)] out ICloudCostProvider? costProvider) =>
            _byProvider.TryGetValue(provider, out costProvider);
    }
}
