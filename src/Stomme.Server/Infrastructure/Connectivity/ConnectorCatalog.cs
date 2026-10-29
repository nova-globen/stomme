#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Infrastructure.Connectivity
{
    /// <summary>
    /// The host's connector catalog: every registered <see cref="IConnector"/> by type key,
    /// compared case-insensitively. A type key published twice REFUSES the composition - it throws when the catalog is
    /// first built - because first-registration-wins would let whichever assembly loaded first own a key (the hijack
    /// the MCP name check also refuses). In-tree that is a build gate; an install that composes a duplicate does not
    /// start, and says which key and which two connectors.
    /// </summary>
    public sealed class ConnectorCatalog : IConnectorCatalog
    {
        private readonly Dictionary<string, IConnector> _byType;

        public ConnectorCatalog(IEnumerable<IConnector> connectors)
        {
            ArgumentNullException.ThrowIfNull(connectors);
            _byType = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase);
            foreach (var connector in connectors)
            {
                if (string.IsNullOrWhiteSpace(connector.ConnectorType))
                {
                    throw new InvalidOperationException($"Connector '{connector.GetType().FullName}' declares no type key.");
                }

                if (_byType.TryGetValue(connector.ConnectorType, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Connector type key '{connector.ConnectorType}' is published twice: by {existing.GetType().FullName} and by {connector.GetType().FullName}. A key has one owner (D110).");
                }

                _byType.Add(connector.ConnectorType, connector);
            }
        }

        public IConnector? Find(string connectorType) =>
            !string.IsNullOrWhiteSpace(connectorType) && _byType.TryGetValue(connectorType.Trim(), out var connector) ? connector : null;

        public IReadOnlyCollection<IConnector> All => _byType.Values.OrderBy(connector => connector.ConnectorType, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static class ConnectorCatalogServiceExtensions
    {
        /// <summary>The catalog over every registered <see cref="IConnector"/>; a singleton, built on first resolve, refusing a duplicate key.</summary>
        public static IServiceCollection AddConnectorCatalog(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddSingleton<IConnectorCatalog>(provider => new ConnectorCatalog(provider.GetServices<IConnector>()));
            return services;
        }
    }
}
