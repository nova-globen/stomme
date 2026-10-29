#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.FlexNetManager
{
    /// <summary>
    /// FlexNet Manager Suite (BL-96 increment 3, lifted from the predecessor platform's <c>fnms</c>): the single SOAP 1.2 <c>GetCustomView</c>
    /// operation hand-rolled over the egress's client (wrapper and members in the <c>http://tempuri.org/</c> namespace - one
    /// envelope does not justify a WCF dependency), authenticated by the Flexera refresh-token grant through the family's
    /// token provider (region-aware). The legacy NTLM/integrated mode behind a <c>"."</c> sentinel stays a host-gated
    /// deferral; the legacy unbounded pull is bounded by an explicit row-limit parameter.
    /// </summary>
    public sealed class FlexNetManagerConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultRegion = "com";
        internal const string DefaultRowLimit = "100000";
        internal const string SoapAction = "http://tempuri.org/GetCustomView";

        private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
        private static readonly XNamespace Tempuri = "http://tempuri.org/";

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "fnms";

        public string DisplayName => "FlexNet Manager Suite";

        public AuthenticationType AuthType => AuthenticationType.OAuth2RefreshToken;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.ItsmAssetManagement };

        public Uri? WebsiteUrl => new("https://www.flexera.com/products/flexnet-manager");

        // FlexNet Manager Suite's compliance API has no public reference page - ApiDocumentationUrl stays undeclared.

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("region", "Region", ConnectorConfigFieldType.Select, Description: "The Flexera login region issuing the access token.")
            {
                Group = "Authentication",
                Default = DefaultRegion,
                Options = new[] { new ConnectorConfigFieldOption("com", "North America (.com)"), new ConnectorConfigFieldOption("eu", "Europe (.eu)"), new ConnectorConfigFieldOption("au", "Australia (.au)") },
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("custom-view", "Custom view", "Runs a FlexNet Manager Suite custom view and streams its rows.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("view_id", "Custom view id", ConnectorConfigFieldType.Integer, Required: true, Description: "The numeric custom view to execute."),
                    new ConnectorConfigField("search", "Search string", ConnectorConfigFieldType.Text, Description: "Optional search string passed to the view."),
                    new ConnectorConfigField("row_limit", "Row limit", ConnectorConfigFieldType.Integer, Description: "Upper bound on returned rows (the legacy pull was unbounded).") { Default = DefaultRowLimit },
                    new ConnectorConfigField("tenant_uid", "Tenant uid", ConnectorConfigFieldType.Text, Description: "Optional tenant scope for multi-tenant installations."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                // The refresh-token exchange is the probe; the data call needs a concrete custom-view id.
                _ = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                return ConnectionTestResult.Ok("Flexera issued an access token for FlexNet Manager Suite.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!string.IsNullOrWhiteSpace(context.OperationName) && !string.Equals(context.OperationName, "custom-view", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the FlexNet Manager Suite connector.");
            }

            var serviceUrl = ServiceUrl(context.Endpoint);
            var viewId = ViewId(context.Parameters);
            var rowLimit = RowLimit(context.Parameters);
            var search = Values.Get(context.Parameters, "search") ?? string.Empty;
            var tenantUid = Values.Get(context.Parameters, "tenant_uid") ?? string.Empty;
            var token = await AcquireTokenAsync(context.Egress, context.Credentials, context.Settings, context.Timeout, cancellationToken);

            using var client = BearerClient.Create(context.Egress, context.Timeout, token);
            var envelope = BuildEnvelope(viewId, search, rowLimit, tenantUid);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () =>
            {
                var content = new StringContent(envelope, Encoding.UTF8);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{SoapAction}\"");
                return new HttpRequestMessage(HttpMethod.Post, new Uri(serviceUrl)) { Content = content };
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(payload))
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            foreach (var row in ParseRows(payload, response.StatusCode))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }

        private static string BuildEnvelope(int viewId, string search, int rowLimit, string tenantUid)
        {
            // The wrapper and member order mirror the generated WCF contract: customViewID, searchString, rowLimit, tenantUID.
            var envelope = new XElement(Soap + "Envelope",
                new XElement(Soap + "Body",
                    new XElement(Tempuri + "GetCustomView",
                        new XElement(Tempuri + "customViewID", viewId),
                        new XElement(Tempuri + "searchString", search),
                        new XElement(Tempuri + "rowLimit", rowLimit),
                        new XElement(Tempuri + "tenantUID", tenantUid))));
            return new XDocument(envelope).ToString(SaveOptions.DisableFormatting);
        }

        private static List<ConnectorRecord> ParseRows(string payload, HttpStatusCode status)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(payload);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException($"The FlexNet Manager Suite response is not valid SOAP XML ({(int)status} {status}).", ex);
            }

            var fault = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null)
            {
                var reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? fault.Value;
                throw new InvalidOperationException($"FlexNet Manager Suite returned a SOAP fault: {reason.Trim()}");
            }

            var result = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "GetCustomViewResult")
                ?? throw new InvalidOperationException("The FlexNet Manager Suite response carries no GetCustomViewResult.");

            // The result is a serialized DataSet: an inline schema plus a diffgram whose first element wraps the rows.
            var diffgram = result.Elements().FirstOrDefault(e => e.Name.LocalName == "diffgram");
            var dataSet = diffgram?.Elements().FirstOrDefault();
            var records = new List<ConnectorRecord>();
            if (dataSet is null)
            {
                return records;
            }

            foreach (var row in dataSet.Elements())
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var column in row.Elements())
                {
                    fields[column.Name.LocalName] = column.Value;
                }

                records.Add(new ConnectorRecord(fields));
            }

            return records;
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken) =>
            tokens.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest($"https://login.flexera.{Values.GetOrDefault(settings, "region", DefaultRegion)}/oidc/token", Values.Get(credentials, "refresh-token") ?? string.Empty)
            {
                ClientId = Values.Get(credentials, "client-id"),
                ClientSecret = Values.Get(credentials, "client-secret"),
                Timeout = timeout,
            }, cancellationToken);

        private static int ViewId(IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "view_id") ?? throw new InvalidOperationException("The custom-view operation requires a 'view_id' parameter.");
            if (!int.TryParse(raw, out var id) || id <= 0)
            {
                throw new InvalidOperationException($"The view id '{raw}' is not a positive integer.");
            }

            return id;
        }

        private static int RowLimit(IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.GetOrDefault(parameters, "row_limit", DefaultRowLimit);
            if (!int.TryParse(raw, out var limit) || limit <= 0)
            {
                throw new InvalidOperationException($"The row limit '{raw}' is not a positive integer.");
            }

            return limit;
        }

        private static string ServiceUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The FlexNet Manager Suite connection needs the ComplianceAPIService URL as the endpoint (e.g. https://host/ManageSoftServices/ComplianceAPIService/ComplianceAPIService.asmx?orgid=1).");
            }

            var url = endpoint.Trim();
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"The endpoint '{endpoint}' is not an absolute http(s) URL.");
            }

            return url;
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "FlexNet Manager Suite rejected the token (401) - check the refresh token and region (the legacy NTLM/integrated mode is not supported by this connector).",
            HttpStatusCode.Forbidden => "FlexNet Manager Suite denied access (403) - the account lacks access to the custom view.",
            _ => $"FlexNet Manager Suite request failed with {(int)status} {status}.",
        };
    }
}
