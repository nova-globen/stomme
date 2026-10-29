#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Rapid7
{
    /// <summary>
    /// Rapid7 InsightVM (lifted from the predecessor platform's <c>rapid7</c>): the /api/3 collections paged by the API's
    /// own <c>totalPages</c>, assets scoped to at most one of site, asset group or scan, a versioned custom path, the
    /// hypermedia <c>links</c> dropped from every row. Basic credentials; every request through the host's egress.
    /// </summary>
    public sealed class Rapid7Connector : IConnector, IConnectorExtractor
    {
        internal const string DefaultPageSize = "100";
        private const int MaxPageSize = 500;

        public string ConnectorType => "rapid7";

        public string DisplayName => "Rapid7 InsightVM";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri? WebsiteUrl => new("https://www.rapid7.com/products/insightvm/");

        public Uri? ApiDocumentationUrl => new("https://help.rapid7.com/insightvm/en-us/api/index.html");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per page (the console caps at 500).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("assets", "Assets", "Streams assets, optionally scoped to one site, asset group, or scan.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("site_id", "Site id", ConnectorConfigFieldType.Integer, Description: "Scope to one site."),
                    new ConnectorConfigField("asset_group_id", "Asset group id", ConnectorConfigFieldType.Integer, Description: "Scope to one asset group."),
                    new ConnectorConfigField("scan_id", "Scan id", ConnectorConfigFieldType.Integer, Description: "Scope to one scan."),
                    SortField(),
                    LimitField(),
                }),
            },
            new ConnectorOperation("sites", "Sites", "Streams sites.") { Parameters = new ConnectorConfigSchema(new[] { SortField(), LimitField() }) },
            new ConnectorOperation("scans", "Scans", "Streams scans.") { Parameters = new ConnectorConfigSchema(new[] { SortField(), LimitField() }) },
            new ConnectorOperation("vulnerabilities", "Vulnerabilities", "Streams the vulnerability catalogue.") { Parameters = new ConnectorConfigSchema(new[] { SortField(), LimitField() }) },
            new ConnectorOperation("custom", "Custom resource", "Streams any paged /api/3 collection by its path (e.g. /api/3/tags).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Resource path", ConnectorConfigFieldType.Text, Required: true, Description: "The API path to page through, starting with /api/3/."),
                    SortField(),
                    LimitField(),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = BaseUrl(context.Endpoint);
                using var client = BearerClient.Create(context.Egress, context.Timeout, BearerClient.Basic(context.Credentials, "InsightVM"), acceptJson: true);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/api/3/assets?page=0&size=1")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "InsightVM", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"InsightVM responded with {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail(FailureMessage(response.StatusCode));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var baseUrl = BaseUrl(context.Endpoint);
            var authorization = BearerClient.Basic(context.Credentials, "InsightVM");
            var path = ResolvePath(context.OperationName, context.Parameters);
            var size = PageSize(context.Settings, context.Parameters);
            var sort = Values.Get(context.Parameters, "sort");

            using var client = BearerClient.Create(context.Egress, context.Timeout, authorization, acceptJson: true);
            var page = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"{baseUrl}{path}?page={page}&size={size}";
                if (sort is not null)
                {
                    url += $"&sort={Uri.EscapeDataString(sort)}";
                }

                var (resources, totalPages) = ReadPage(await BearerClient.GetAsync(client, url, FailureMessage, cancellationToken));
                foreach (var resource in resources)
                {
                    yield return FlattenedRows.Flatten(resource, skipProperty: "links");
                }

                // The API reports its own page count - trusted over the legacy short-page heuristic.
                page++;
                if (resources.Count == 0 || totalPages is null || page >= totalPages)
                {
                    yield break;
                }
            }
        }

        private static ConnectorConfigField SortField() => new("sort", "Sort", ConnectorConfigFieldType.Text, Description: "Optional sort criteria, e.g. 'id,asc'.");

        private static ConnectorConfigField LimitField() => new("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation.");

        private static string ResolvePath(string operationName, IReadOnlyDictionary<string, string> parameters)
        {
            var operation = string.IsNullOrWhiteSpace(operationName) ? "assets" : operationName.ToLowerInvariant();
            switch (operation)
            {
                case "assets":
                    var scopes = new List<string>();
                    if (Values.Get(parameters, "site_id") is { } site)
                    {
                        scopes.Add($"/api/3/sites/{Uri.EscapeDataString(site)}/assets");
                    }

                    if (Values.Get(parameters, "asset_group_id") is { } group)
                    {
                        scopes.Add($"/api/3/asset_groups/{Uri.EscapeDataString(group)}/assets");
                    }

                    if (Values.Get(parameters, "scan_id") is { } scan)
                    {
                        scopes.Add($"/api/3/scans/{Uri.EscapeDataString(scan)}/assets");
                    }

                    return scopes.Count switch
                    {
                        0 => "/api/3/assets",
                        1 => scopes[0],
                        _ => throw new InvalidOperationException("Scope assets to at most ONE of site_id, asset_group_id, or scan_id."),
                    };
                case "sites":
                    return "/api/3/sites";
                case "scans":
                    return "/api/3/scans";
                case "vulnerabilities":
                    return "/api/3/vulnerabilities";
                case "custom":
                    var path = Values.Get(parameters, "path") ?? throw new InvalidOperationException("The custom operation requires a 'path' parameter.");
                    if (!path.StartsWith("/api/3/", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The custom path must start with /api/3/.");
                    }

                    return path;
                default:
                    throw new InvalidOperationException($"Unknown operation '{operationName}' for the Rapid7 connector.");
            }
        }

        private static (List<JsonElement> Resources, long? TotalPages) ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resources", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The InsightVM response carries no 'resources' array.");
            }

            var resources = new List<JsonElement>();
            foreach (var row in rows.EnumerateArray())
            {
                resources.Add(row.Clone());
            }

            long? totalPages = null;
            if (root.TryGetProperty("page", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object && pageInfo.TryGetProperty("totalPages", out var total) && total.TryGetInt64(out var pages))
            {
                totalPages = pages;
            }

            return (resources, totalPages);
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "limit") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (!int.TryParse(raw, out var size) || size <= 0 || size > MaxPageSize)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not an integer between 1 and {MaxPageSize}.");
            }

            return size;
        }

        private static string BaseUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The InsightVM connection needs its console base URL as the endpoint (self-hosted, no default).");
            }

            return Values.BaseUrl(endpoint, endpoint);
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "InsightVM rejected the credentials (401) - check the username/password (and whether the account requires a 2FA token, which this connector does not yet send).",
            HttpStatusCode.Forbidden => "InsightVM denied access (403) - the account lacks the required permissions.",
            _ => $"InsightVM request failed with {(int)status} {status}.",
        };
    }
}
