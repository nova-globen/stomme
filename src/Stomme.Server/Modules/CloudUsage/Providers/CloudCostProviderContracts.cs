#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Connectivity;

namespace Stomme.Modules.CloudUsage.Providers
{
    /// <summary>
    /// A cost-fetch window, the connection's transport settings (its endpoint and credential, placed there by the sync from
    /// the Connection and the per-site secret store) and the HOST's egress for the connection's site.
    /// </summary>
    /// <remarks>
    /// The query carries <see cref="Egress"/>, the one way this module reaches
    /// the network - the connectors' own egress over the framework's outbound guard (pinned address, per-site allowlist).
    /// A payload source building its own client over its own socket handler would bypass that guard; the egress source gate
    /// scans this module as it scans the Connectors family.
    /// </remarks>
    public sealed record CloudCostQuery(DateOnly From, DateOnly To, IReadOnlyDictionary<string, string> ConnectionSettings, IConnectorEgress? Egress = null);

    /// <summary>
    /// The transport seam between a cost adapter and the provider's billing API: reads the provider's native billing payload
    /// (JSON) for a window, so the adapters stay SDK-free payload mappers and tests feed them captured samples.
    /// </summary>
    public interface ICloudCostPayloadSource
    {
        Task<string> ReadAsync(CloudCostQuery query, CancellationToken ct = default);
    }

    /// <summary>A per-source cost fetch: the provider's native rows mapped to the Unified Cost Schema.</summary>
    public interface ICloudCostProvider
    {
        CloudProvider Provider { get; }

        Task<IReadOnlyList<CloudCostEntry>> FetchCostsAsync(CloudCostQuery query, CancellationToken ct = default);
    }

    /// <summary>The adapter for a provider; resolving one with none registered throws, trying returns false.</summary>
    public interface ICloudCostProviderRegistry
    {
        ICloudCostProvider Resolve(CloudProvider provider);

        bool TryResolve(CloudProvider provider, [NotNullWhen(true)] out ICloudCostProvider? costProvider);
    }

    /// <summary>
    /// The connection-backed payload source: a Bearer GET -
    /// or a POST with the configured JSON body - against the connection's cost-API endpoint, <c>{from}</c> and <c>{to}</c>
    /// substituted in the endpoint and the body, through the query's host egress and nothing else. A missing endpoint,
    /// credential or egress is a configuration error; a non-success answer an <see cref="HttpRequestException"/>.
    /// </summary>
    public sealed class ConnectorCostPayloadSource : ICloudCostPayloadSource
    {
        public const string EndpointSettingKey = "endpoint";

        public const string CredentialSettingKey = "credential";

        public const string RequestBodySettingKey = "requestBody";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        public async Task<string> ReadAsync(CloudCostQuery query, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!query.ConnectionSettings.TryGetValue(EndpointSettingKey, out var endpoint) || !TryCreateHttpUri(SubstituteWindow(endpoint, query), out var target))
            {
                throw new InvalidOperationException("The cost connection settings carry no absolute http(s) cost-API endpoint ('endpoint').");
            }

            if (!query.ConnectionSettings.TryGetValue(CredentialSettingKey, out var credential) || string.IsNullOrEmpty(credential))
            {
                throw new InvalidOperationException("The cost connection settings carry no credential ('credential') from the site's secret store.");
            }

            var egress = query.Egress ?? throw new InvalidOperationException("A cost fetch reaches the network only through the host's egress, and none was supplied.");
            using var client = egress.CreateHttpClient(RequestTimeout);
            using var request = query.ConnectionSettings.TryGetValue(RequestBodySettingKey, out var body) && !string.IsNullOrWhiteSpace(body)
                ? new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(SubstituteWindow(body, query), Encoding.UTF8, "application/json") }
                : new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        internal static bool TryCreateHttpUri(string? endpoint, out Uri uri)
        {
            uri = null!;
            return Stomme.Shared.Utilities.TryCreateAbsoluteUri(endpoint, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string SubstituteWindow(string template, CloudCostQuery query) =>
            template
                .Replace("{from}", query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{to}", query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }
}
