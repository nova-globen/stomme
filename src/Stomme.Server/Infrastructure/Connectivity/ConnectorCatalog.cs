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
    /// compared case-insensitively. A type key published twice is REJECTED on every side and the host keeps working: neither
    /// connector is registered under it, the collision is logged at Error naming the key and both owners, and
    /// <see cref="Conflicts"/> lists it - the MCP contributors' rule (BL-105), because first-registration-wins would let
    /// whichever assembly loaded first own a key. **CORRECTION 2026-10-30 (BL-251, BL-239's H#5):** a duplicate used to THROW
    /// when the catalog was first built - on the first request that needed it, so the host started and every connections call
    /// answered 500 with the cause only in the event log. In-tree a duplicate is still a build failure
    /// (<c>ConnectorCatalogueMetadataTests</c> counts the catalogue).
    /// </summary>
    public sealed class ConnectorCatalog : IConnectorCatalog
    {
        private readonly Dictionary<string, IConnector> _byType;

        public ConnectorCatalog(IEnumerable<IConnector> connectors)
            : this(connectors, logger: null)
        {
        }

        public ConnectorCatalog(IEnumerable<IConnector> connectors, Microsoft.Extensions.Logging.ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(connectors);
            _byType = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();
            foreach (var group in connectors.GroupBy(connector => connector.ConnectorType?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var owners = group.ToList();
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    throw new InvalidOperationException($"Connector '{owners[0].GetType().FullName}' declares no type key.");
                }

                if (owners.Count == 1)
                {
                    _byType.Add(group.Key, owners[0]);
                    continue;
                }

                var message = $"Connector type key '{group.Key}' is published twice: by {string.Join(" and by ", owners.Select(owner => owner.GetType().FullName))}. A key has one owner, so neither is registered under it.";
                conflicts.Add(message);
                if (logger is not null)
                {
                    Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, "{Conflict}", message);
                }
            }

            Conflicts = conflicts;
        }

        /// <summary>The type keys refused because two connectors publish them, each naming its owners.</summary>
        public IReadOnlyList<string> Conflicts { get; }
        public IConnector? Find(string connectorType) =>
            !string.IsNullOrWhiteSpace(connectorType) && _byType.TryGetValue(connectorType.Trim(), out var connector) ? connector : null;

        public IReadOnlyCollection<IConnector> All => _byType.Values.OrderBy(connector => connector.ConnectorType, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static class ConnectorCatalogServiceExtensions
    {
        /// <summary>The catalog over every registered <see cref="IConnector"/>; a singleton, built on first resolve, rejecting a duplicate key on every side.</summary>
        public static IServiceCollection AddConnectorCatalog(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddSingleton<IConnectorCatalog>(provider => new ConnectorCatalog(provider.GetServices<IConnector>(), provider.GetService<Microsoft.Extensions.Logging.ILogger<ConnectorCatalog>>()));
            return services;
        }
    }
}
