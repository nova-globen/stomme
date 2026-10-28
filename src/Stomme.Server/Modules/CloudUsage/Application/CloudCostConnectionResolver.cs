#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Connectivity.Persistence;
using Stomme.Modules.Connectivity.Application;
using Stomme.Shared;

namespace Stomme.Modules.CloudUsage.Application
{
    /// <summary>A connection as the cost sync needs it: its type, endpoint and settings, and the credential it sends.</summary>
    /// <param name="Secret">The connection's <c>token</c> field from the per-site secret store; null when it holds none that can be read.</param>
    public sealed record ResolvedConnection(Guid ConnectionId, int SiteId, string ConnectorType, string Endpoint, IReadOnlyDictionary<string, string> Settings, string? Secret);

    public interface ICloudCostConnectionResolver
    {
        Task<ResolvedConnection?> ResolveAsync(Guid connectionId, CancellationToken ct = default);
    }

    /// <summary>
    /// the predecessor platform's Web adapter (<c>ConnectivityCloudCostConnectionResolver</c>) on Stomme's Connectivity (BL-97 increment 3):
    /// the connection through the REQUEST's site - another site's connection is not found - and its credential through the
    /// per-site secret store, never a log or a view.
    /// </summary>
    /// <remarks>
    /// the predecessor platform sent one vault secret as a Bearer token to every cost API. Here the credential is the connection's <c>token</c>
    /// field - the Bearer scheme the Azure and SaaS cost connectors declare. An AWS (key pair, SigV4) or GCP connection holds
    /// no such field and resolves with no secret, which the sync refuses by name; the signing is a booked follow-up.
    /// </remarks>
    public sealed class CloudCostConnectionResolver(IConnectionRepository connections, ConnectionSecrets secrets, SiteState siteState) : ICloudCostConnectionResolver
    {
        public const string CredentialField = "token";

        public async Task<ResolvedConnection?> ResolveAsync(Guid connectionId, CancellationToken ct = default)
        {
            if (siteState.Alias?.SiteId is not { } siteId)
            {
                return null;
            }

            var connection = await connections.GetByIdAsync(connectionId, ct);
            if (connection is null || connection.SiteId != siteId)
            {
                return null;
            }

            var resolved = await secrets.ResolveAsync(siteId, connection, ct);
            var secret = resolved.Values.TryGetValue(CredentialField, out var token) && !string.IsNullOrEmpty(token) ? token : null;
            return new ResolvedConnection(connection.Id, siteId, connection.ConnectorType, connection.Endpoint.Value, connection.Settings.GetSettings(), secret);
        }
    }

    /// <summary>The cost connector type each source is fetched through (BL-96's family); a connection of another type is refused.</summary>
    public static class CloudCostConnectorTypes
    {
        public static string For(CloudProvider provider) => provider switch
        {
            CloudProvider.Azure => "azurecost",
            CloudProvider.Aws => "awscost",
            CloudProvider.Gcp => "gcpcost",
            CloudProvider.Datadog => "datadogcost",
            CloudProvider.Snowflake => "snowflakecost",
            CloudProvider.GitHub => "githubcost",
            CloudProvider.Microsoft365 => "m365cost",
            CloudProvider.Salesforce => "salesforcecost",
            CloudProvider.GenericRest => "rest",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a known cloud provider."),
        };
    }
}
