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
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.CiscoDnaCenter
{
    /// <summary>
    /// Cisco DNA Center (BL-96 increment 4, lifted from the predecessor platform's <c>ciscodnac</c>): the Basic credentials exchanged for an
    /// <c>X-Auth-Token</c> at the system API, then the Intent API paged by a 1-based offset (or read once for a
    /// single-object endpoint), the prefix not doubled when the custom path already carries it. Every request through the
    /// host's egress (D110).
    /// </summary>
    public sealed class CiscoDnaCenterConnector : IConnector, IConnectorExtractor
    {
        internal const string DefaultPageSize = "500";
        private const string IntentPrefix = "dna/intent/api/v1";

        public string ConnectorType => "ciscodnac";

        public string DisplayName => "Cisco Catalyst Center (formerly Cisco DNA Center)"; // the licensing review's name (BRAND_ASSET_REGISTRY.json); the type id stays ciscodnac

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.NetworkInfrastructure };

        public Uri? WebsiteUrl => new("https://www.cisco.com/site/us/en/products/networking/catalyst-center/index.html");

        public Uri? ApiDocumentationUrl => new("https://developer.cisco.com/docs/dna-center/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per page (the Intent-API 'limit').")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("devices", "Network devices", "Streams the network-device inventory from the Intent API (/dna/intent/api/v1/network-device).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Description: "Optional comma-separated field list to request."),
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Description: "Optional raw query string appended to the request (e.g. family=Switches)."),
                }),
            },
            new ConnectorOperation("custom", "Custom endpoint", "Reads any Intent-API endpoint you name (relative to /dna/intent/api/v1).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Endpoint", ConnectorConfigFieldType.Text, Required: true, Description: "The Intent-API endpoint, e.g. network-device or client-health."),
                    new ConnectorConfigField("paged", "Page with offset/limit", ConnectorConfigFieldType.Boolean, Description: "Loop offset/limit for list endpoints; turn off for single-object endpoints.") { Default = "true" },
                    new ConnectorConfigField("fields", "Fields", ConnectorConfigFieldType.Text, Description: "Optional comma-separated field list to request."),
                    new ConnectorConfigField("filter", "Filter", ConnectorConfigFieldType.Text, Description: "Optional raw query string appended to the request."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = BaseUrl(context.Endpoint);
                var authorization = BearerClient.Basic(context.Credentials, "Cisco DNA Center");
                using var client = context.Egress.CreateHttpClient(context.Timeout);
                await GetTokenAsync(client, baseUrl, authorization, cancellationToken);
                return ConnectionTestResult.Ok("Cisco DNA Center issued an authentication token.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "devices" : context.OperationName;
            string endpoint;
            bool paged;
            if (string.Equals(operation, "devices", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = "network-device";
                paged = true;
            }
            else if (string.Equals(operation, "custom", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = Values.Get(context.Parameters, "path") ?? throw new InvalidOperationException("The custom operation requires a 'path' parameter.");
                paged = Values.GetBool(context.Parameters, "paged", defaultValue: true);
            }
            else
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Cisco DNA Center connector.");
            }

            var baseUrl = BaseUrl(context.Endpoint);
            var dataUrl = DataUrl(baseUrl, endpoint);
            var authorization = BearerClient.Basic(context.Credentials, "Cisco DNA Center");
            var limit = PageSize(context.Settings);
            var fields = Values.Get(context.Parameters, "fields");
            var filter = Values.Get(context.Parameters, "filter");

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            var token = await GetTokenAsync(client, baseUrl, authorization, cancellationToken);
            client.DefaultRequestHeaders.Add("X-Auth-Token", token);

            if (!paged)
            {
                foreach (var row in await ReadRowsAsync(client, BuildUrl(dataUrl, offset: null, limit: null, fields, filter), cancellationToken))
                {
                    yield return FlattenedRows.Flatten(row);
                }

                yield break;
            }

            var offset = 1; // the appliance's offset is 1-based
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await ReadRowsAsync(client, BuildUrl(dataUrl, offset, limit, fields, filter), cancellationToken);
                foreach (var row in rows)
                {
                    yield return FlattenedRows.Flatten(row);
                }

                if (rows.Count == 0 || rows.Count < limit)
                {
                    yield break;
                }

                offset += limit;
            }
        }

        private static string DataUrl(string baseUrl, string endpoint)
        {
            var trimmed = endpoint.Trim().TrimStart('/');
            // The prefix is collapsed when the endpoint already carries it.
            return trimmed.StartsWith(IntentPrefix, StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/{trimmed}" : $"{baseUrl}/{IntentPrefix}/{trimmed}";
        }

        private static string BuildUrl(string dataUrl, int? offset, int? limit, string? fields, string? filter)
        {
            var url = new StringBuilder(dataUrl);
            var separator = '?';
            if (offset is { } o && limit is { } l)
            {
                url.Append(separator).Append("offset=").Append(o).Append("&limit=").Append(l);
                separator = '&';
            }

            if (fields is not null)
            {
                url.Append(separator).Append("fields=").Append(Uri.EscapeDataString(fields));
                separator = '&';
            }

            if (filter is not null)
            {
                url.Append(separator).Append(filter.TrimStart('?', '&')); // appended verbatim: callers write valid query fragments
            }

            return url.ToString();
        }

        private static async Task<List<JsonElement>> ReadRowsAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            var payload = await BearerClient.GetAsync(client, url, FailureMessage, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("response", out var element))
            {
                throw new InvalidOperationException("The Cisco DNA Center response carries no 'response' property.");
            }

            var rows = new List<JsonElement>();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in element.EnumerateArray())
                {
                    rows.Add(row.Clone());
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                rows.Add(element.Clone());
            }

            return rows;
        }

        private static async Task<string> GetTokenAsync(HttpClient client, string baseUrl, System.Net.Http.Headers.AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
        {
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}/dna/system/api/v1/auth/token")) { Headers = { Authorization = authorization } }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Cisco DNA Center did not return an authentication token.");
            }

            return token.GetString()!;
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings)
        {
            var raw = Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (!int.TryParse(raw, out var size) || size <= 0)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not a positive integer.");
            }

            return size;
        }

        private static string BaseUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The Cisco DNA Center connection needs its appliance base URL as the endpoint (e.g. https://dnac.example.com).");
            }

            return Values.BaseUrl(endpoint, endpoint);
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Cisco DNA Center rejected the credentials (401) - check the username/password.",
            HttpStatusCode.Forbidden => "Cisco DNA Center denied access (403) - the account lacks permission for this endpoint.",
            _ => $"Cisco DNA Center request failed with {(int)status} {status}.",
        };
    }
}
