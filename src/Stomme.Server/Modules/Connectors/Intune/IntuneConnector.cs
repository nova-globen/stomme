#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Intune
{
    /// <summary>
    /// Microsoft Intune (lifted from the predecessor platform's <c>intune</c>): OAuth2 client credentials over Microsoft
    /// Graph through the family's one token provider; the <c>managed-devices</c> operation streams
    /// <c>deviceManagement/managedDevices</c> pages following <c>@odata.nextLink</c>. Every request rides the client the
    /// host's egress hands the connector; throttling is retried per Retry-After by the family's policy; an
    /// incomplete credential set fails before any call.
    /// </summary>
    public sealed class IntuneConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://graph.microsoft.com";
        internal const string DefaultScope = "https://graph.microsoft.com/.default";
        internal const string DefaultPageSize = "999";

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "intune";

        public string DisplayName => "Microsoft Intune";

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.MicrosoftCloudDirectory };

        public Uri? WebsiteUrl => new("https://www.microsoft.com/security/business/endpoint-management/microsoft-intune");

        public Uri? ApiDocumentationUrl => new("https://learn.microsoft.com/graph/api/resources/intune-graph-overview");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("scope", "OAuth2 scope", ConnectorConfigFieldType.Text, Description: "The token scope requested from the identity platform.")
            {
                Group = "Authentication",
                Default = DefaultScope,
                HelpText = "Change only for sovereign clouds (e.g. Graph US Government / 21Vianet endpoints).",
            },
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Devices requested per Graph page ($top).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("managed-devices", "Managed devices", "Streams Intune managed devices from Microsoft Graph (deviceManagement/managedDevices).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Description: "Optional OData $filter expression."),
                    new ConnectorConfigField("select", "Columns", ConnectorConfigFieldType.Text, Description: "Optional comma-separated $select list; the full Graph projection when empty."),
                    new ConnectorConfigField("top", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                var probe = GraphCollectionUrl(context.Endpoint, new[] { "$top=1" });
                using var client = BearerClient.Create(context.Egress, context.Timeout, token);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri(probe)), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "Microsoft Graph", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"Microsoft Graph responded with {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail(FailureMessage(response.StatusCode));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message); // a policy refusal propagates; a timeout propagates as the cancellation it is
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "managed-devices", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Intune connector.");
            }

            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
            var url = GraphCollectionUrl(context.Endpoint, BuildQuery(context.Settings, context.Parameters));

            using var client = BearerClient.Create(context.Egress, context.Timeout, token);
            string? next = url;
            while (next is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await BearerClient.GetAsync(client, next, FailureMessage, cancellationToken);
                var (records, nextLink) = GraphPage.Read(payload, next);
                foreach (var record in records)
                {
                    yield return record;
                }

                next = nextLink;
            }
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(
                Values.Get(credentials, "token-url") ?? string.Empty,
                Values.Get(credentials, "client-id") ?? string.Empty,
                Values.Get(credentials, "client-secret") ?? string.Empty,
                Values.GetOrDefault(settings, "scope", DefaultScope))
            { Timeout = timeout }, cancellationToken);

        private static string GraphCollectionUrl(string endpoint, IReadOnlyList<string> query) =>
            $"{Values.BaseUrl(endpoint, DefaultEndpoint)}/v1.0/deviceManagement/managedDevices?{string.Join('&', query)}";

        private static List<string> BuildQuery(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var top = Values.Get(parameters, "top") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            var query = new List<string> { $"$top={Uri.EscapeDataString(top)}" };
            if (Values.Get(parameters, "filter") is { } filter)
            {
                query.Add($"$filter={Uri.EscapeDataString(filter)}");
            }

            if (Values.Get(parameters, "select") is { } select)
            {
                query.Add($"$select={Uri.EscapeDataString(select)}");
            }

            return query;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Microsoft Graph rejected the token (401) - check the tenant's token URL and the client credentials.",
            HttpStatusCode.Forbidden => "Microsoft Graph denied access (403) - grant the app the DeviceManagementManagedDevices.Read.All application permission with admin consent.",
            _ => $"Microsoft Graph request failed with {(int)status} {status}.",
        };
    }
}
