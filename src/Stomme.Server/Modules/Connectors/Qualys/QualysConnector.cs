#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Qualys
{
    /// <summary>
    /// Qualys asset management: the QPS host-asset search - an XML
    /// ServiceRequest posted with JSON negotiated back - paged by offset while <c>hasMoreRecords</c> says so, one optional
    /// criteria, the legacy hand-picked projection typed. Basic credentials; every request through the host's egress.
    /// </summary>
    public sealed class QualysConnector : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://qualysapi.qualys.com";
        internal const string DefaultPageSize = "100";
        internal const string RequestedWith = "Stomme";
        private const string SearchPath = "/qps/rest/2.0/search/am/hostasset";

        public string ConnectorType => "qualys";

        public string DisplayName => "Qualys Asset Management";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        // Tag-like: host-asset inventory serves both the security and the asset-management picker groups.
        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability, ConnectorCategory.ItsmAssetManagement };

        public Uri? WebsiteUrl => new("https://www.qualys.com");

        public Uri? ApiDocumentationUrl => new("https://docs.qualys.com/en/gav/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Host assets requested per page (limitResults).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("host-assets", "Host assets", "Streams host assets from the QPS asset-management search, optionally filtered by one criteria.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("filter_field", "Filter field", ConnectorConfigFieldType.Text, Description: "Optional criteria field, e.g. lastVulnScan or tagName."),
                    new ConnectorConfigField("filter_operator", "Filter operator", ConnectorConfigFieldType.Text, Description: "Criteria operator, e.g. GREATER, EQUALS, CONTAINS."),
                    new ConnectorConfigField("filter_value", "Filter value", ConnectorConfigFieldType.Text, Description: "The criteria value."),
                    new ConnectorConfigField("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                using var client = CreateClient(context.Egress, context.Timeout, context.Credentials);
                _ = ReadPage(await PostSearchAsync(client, baseUrl, offset: 1, limit: 1, criteria: null, cancellationToken));
                return ConnectionTestResult.Ok("Qualys QPS responded.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "host-assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Qualys connector.");
            }

            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var limit = PageSize(context.Settings, context.Parameters);
            var criteria = Criteria(context.Parameters);
            using var client = CreateClient(context.Egress, context.Timeout, context.Credentials);
            var offset = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (assets, hasMore) = ReadPage(await PostSearchAsync(client, baseUrl, offset, limit, criteria, cancellationToken));
                foreach (var asset in assets)
                {
                    yield return MapHostAsset(asset);
                }

                if (!hasMore || assets.Count == 0)
                {
                    yield break;
                }

                offset += limit;
            }
        }

        private static HttpClient CreateClient(IConnectorEgress egress, TimeSpan timeout, IReadOnlyDictionary<string, string> credentials)
        {
            var client = BearerClient.Create(egress, timeout, BearerClient.Basic(credentials, "Qualys"), acceptJson: true);
            client.DefaultRequestHeaders.Add("X-Requested-With", RequestedWith); // Qualys rejects API calls without it
            return client;
        }

        private static async Task<string> PostSearchAsync(HttpClient client, string baseUrl, int offset, int limit, (string Field, string Operator, string Value)? criteria, CancellationToken cancellationToken)
        {
            // The QPS ServiceRequest is XML even when the response is negotiated to JSON.
            var request = new XElement("ServiceRequest", new XElement("preferences", new XElement("startFromOffset", offset), new XElement("limitResults", limit)));
            if (criteria is { } c)
            {
                request.Add(new XElement("filters", new XElement("Criteria", new XAttribute("field", c.Field), new XAttribute("operator", c.Operator), c.Value)));
            }

            var body = request.ToString(SaveOptions.DisableFormatting);
            using var response = await RetryAfterPolicy.Default.SendAsync(client,
                () => new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{SearchPath}")) { Content = new StringContent(body, Encoding.UTF8, "text/xml") }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            return payload;
        }

        private static (List<JsonElement> Assets, bool HasMore) ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("ServiceResponse", out var serviceResponse) || serviceResponse.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The Qualys response carries no ServiceResponse object.");
            }

            var assets = new List<JsonElement>();
            if (serviceResponse.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("HostAsset", out var host))
                    {
                        assets.Add(host.Clone());
                    }
                }
            }

            var hasMore = serviceResponse.TryGetProperty("hasMoreRecords", out var more) &&
                (more.ValueKind == JsonValueKind.True || (more.ValueKind == JsonValueKind.String && string.Equals(more.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            return (assets, hasMore);
        }

        // The legacy hand-picked projection, typed; lastCheckedIn comes from the agent info.
        private static ConnectorRecord MapHostAsset(JsonElement host) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = GetLong(host, "id"),
            ["name"] = GetString(host, "name"),
            ["type"] = GetString(host, "type"),
            ["os"] = GetString(host, "os"),
            ["created"] = GetString(host, "created"),
            ["lastVulnScan"] = GetString(host, "lastVulnScan"),
            ["lastCheckedIn"] = GetString(host, "agentInfo", "lastCheckedIn"),
            ["address"] = GetString(host, "address"),
            ["fqdn"] = GetString(host, "fqdn"),
            ["trackingMethod"] = GetString(host, "trackingMethod"),
        });

        private static (string Field, string Operator, string Value)? Criteria(IReadOnlyDictionary<string, string> parameters)
        {
            var field = Values.Get(parameters, "filter_field");
            var op = Values.Get(parameters, "filter_operator");
            var value = Values.Get(parameters, "filter_value");
            if (field is null && op is null && value is null)
            {
                return null;
            }

            if (field is null || op is null || value is null)
            {
                throw new InvalidOperationException("A Qualys filter needs all three of filter_field, filter_operator and filter_value.");
            }

            return (field, op, value);
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "limit") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (!int.TryParse(raw, out var size) || size <= 0)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not a positive integer.");
            }

            return size;
        }

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                _ => null,
            };
        }

        private static long? GetLong(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Qualys rejected the credentials (401) - check the username/password.",
            HttpStatusCode.Forbidden => "Qualys denied access (403) - the account lacks the QPS asset-management API permission.",
            _ => $"Qualys request failed with {(int)status} {status}.",
        };
    }
}
