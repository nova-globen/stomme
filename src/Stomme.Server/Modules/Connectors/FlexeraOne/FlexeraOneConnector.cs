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

namespace Stomme.Modules.Connectors.FlexeraOne
{
    /// <summary>
    /// Flexera One (BL-96 increment 3, lifted from the predecessor platform's <c>flexeraone</c>): the OAuth2 refresh-token grant through the
    /// family's token provider, the token endpoint from the <c>region</c> setting (<c>login.flexera.{region}/oidc/token</c>)
    /// and the API base from the endpoint (or <c>api.flexera.{region}</c> when empty). The <c>reports</c> operation executes
    /// each configured report and follows next-link paging - BOUNDED with cycle detection (the legacy loop could follow a
    /// cyclic link forever) - and checks the API's <c>error</c> property before the rows (the legacy misreported errors as a
    /// missing <c>values</c> array). Every request through the host's egress (D110).
    /// </summary>
    public sealed class FlexeraOneConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultRegion = "com";
        private const int MaxPages = 10_000;

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "flexeraone";

        public string DisplayName => "Flexera One";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri? WebsiteUrl => new("https://www.flexera.com/flexera-one");

        public Uri? ApiDocumentationUrl => new("https://developer.flexera.com/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Select, Description: "The Flexera One region hosting the organization.")
            {
                Group = "Authentication",
                Default = DefaultRegion,
                Options = new[] { new ConnectorConfigFieldOption("com", "North America (.com)"), new ConnectorConfigFieldOption("eu", "Europe (.eu)"), new ConnectorConfigFieldOption("au", "Australia (.au)") },
            },
            new ConnectorConfigField("org_id", "Organization id", ConnectorConfigFieldType.Text, Required: true, Description: "The Flexera One organization the reports belong to.") { Group = "Query" },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("reports", "Reports", "Executes ITAM reports by number and streams every page of every report.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("report_numbers", "Report numbers", ConnectorConfigFieldType.Text, Required: true, Description: "Comma- or semicolon-separated report numbers to execute."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                // A successful refresh-token exchange is the meaningful reachability probe - the only data endpoint needs a concrete report number.
                _ = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                return ConnectionTestResult.Ok("Flexera One issued an access token.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "reports", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Flexera One connector.");
            }

            var orgId = Values.Get(context.Settings, "org_id") ?? throw new InvalidOperationException("The Flexera One connection is missing the 'org_id' setting.");
            var reportNumbers = ReportNumbers(context.Parameters);
            var baseUrl = ApiBaseUrl(context.Endpoint, context.Settings);
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);

            using var client = BearerClient.Create(context.Egress, context.Timeout, token, acceptJson: true);
            foreach (var reportNumber in reportNumbers)
            {
                var next = (string?)$"{baseUrl}/fnms/v1/orgs/{Uri.EscapeDataString(orgId)}/reports/{Uri.EscapeDataString(reportNumber)}/execute";
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pages = 0;
                while (next is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Bounded and cycle-guarded, unlike the legacy do/while over the raw next link.
                    if (!seen.Add(next) || ++pages > MaxPages)
                    {
                        throw new InvalidOperationException($"Report {reportNumber} paging did not terminate (cyclic or runaway next link).");
                    }

                    var payload = await BearerClient.GetAsync(client, next, FailureMessage, cancellationToken);
                    var (rows, nextLink) = ReadPage(payload);
                    foreach (var row in rows)
                    {
                        yield return FlattenedRows.Flatten(row);
                    }

                    next = nextLink is null ? null : ResolveNextUrl(baseUrl, nextLink);
                }
            }
        }

        private static (List<JsonElement> Rows, string? NextLink) ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The Flexera One response is not a JSON object.");
            }

            // The error property outranks a missing values array: the legacy bridge checked them in the wrong order and reported every API error as "values missing".
            if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
            {
                throw new InvalidOperationException($"Flexera One returned an error: {error.GetRawText()}");
            }

            if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The Flexera One response carries no 'values' array.");
            }

            var rows = new List<JsonElement>();
            foreach (var row in values.EnumerateArray())
            {
                rows.Add(row.Clone());
            }

            string? nextLink = null;
            foreach (var name in new[] { "nextPage", "nextLink", "@odata.nextLink" })
            {
                if (root.TryGetProperty(name, out var link) && link.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(link.GetString()))
                {
                    nextLink = link.GetString();
                    break;
                }
            }

            return (rows, nextLink);
        }

        private static string ResolveNextUrl(string baseUrl, string nextLink) =>
            Stomme.Shared.Utilities.TryCreateAbsoluteUri(nextLink, out var absolute)
                ? $"{baseUrl}{absolute.PathAndQuery}"
                : $"{baseUrl}{(nextLink.StartsWith('/') ? nextLink : "/" + nextLink)}";

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest($"https://login.flexera.{Region(settings)}/oidc/token", Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        private static string Region(IReadOnlyDictionary<string, string> settings) => Values.GetOrDefault(settings, "region", DefaultRegion);

        private static string ApiBaseUrl(string endpoint, IReadOnlyDictionary<string, string> settings) =>
            Values.BaseUrl(endpoint, $"https://api.flexera.{Region(settings)}");

        private static List<string> ReportNumbers(IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "report_numbers") ?? throw new InvalidOperationException("The reports operation requires a 'report_numbers' parameter.");
            var numbers = new List<string>(raw.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (numbers.Count == 0)
            {
                throw new InvalidOperationException("The 'report_numbers' parameter contains no report numbers.");
            }

            return numbers;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Flexera One rejected the token (401) - check the refresh token and region.",
            HttpStatusCode.Forbidden => "Flexera One denied access (403) - the token's account lacks access to the organization or report.",
            _ => $"Flexera One request failed with {(int)status} {status}.",
        };
    }
}
