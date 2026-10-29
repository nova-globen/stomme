#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace NovaGloben.Connector.Template
{
    /// <summary>
    /// The template connector: an HTTP system reached with an API key. Copy this folder, rename the type key (it is stored
    /// on every Connection row - never change it once shipped), declare the scheme and the settings, and write the test.
    /// </summary>
    /// <remarks>
    /// <para><b>The three rules of a Stomme connector.</b></para>
    /// <list type="number">
    /// <item>The network is the egress's: <see cref="IConnectorEgress.CreateHttpClient"/> for HTTP, <see cref="IConnectorEgress.ResolveAndPinAsync"/> before a driver dials. Never a client, handler, socket, DNS lookup or process of your own.</item>
    /// <item>A refusal by the host's policy is not yours to report: ask <see cref="IConnectorEgress.IsDenial"/> and let it propagate - the runner categorises it <c>DeniedByPolicy</c>. A driver whose host pins to nothing throws <see cref="ConnectorEgressDeniedException"/>. Your own words go in a <see cref="ConnectionTestResult.Fail"/> and stay inside the host.</item>
    /// <item>Credentials come by the scheme's field names (<see cref="AuthenticationScheme.FieldsFor"/>), never from the endpoint or a setting.</item>
    /// </list>
    /// </remarks>
    public sealed class TemplateConnector : IConnector
    {
        public string ConnectorType => "template-http";

        public string DisplayName => "Template HTTP API";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? ApiDocumentationUrl => new("https://example.com/docs");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("path", "Health check path", ConnectorConfigFieldType.Text, Required: false, Description: "Optional path appended to the endpoint for the reachability probe."),
            new ConnectorConfigField("api_key_header", "API key header", ConnectorConfigFieldType.Text, Required: false, Description: "The header the key is sent in.") { Default = "X-Api-Key" },
        });

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!Uri.TryCreate(context.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return ConnectionTestResult.Fail("Endpoint must be an absolute http(s) URL.");
            }

            var target = uri;
            if (context.Settings.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path) && Uri.TryCreate(uri, path, out var relative))
            {
                target = relative;
            }

            var headerName = context.Settings.TryGetValue("api_key_header", out var configured) && !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : "X-Api-Key";

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (context.Credentials.TryGetValue("api-key", out var key) && !string.IsNullOrEmpty(key))
            {
                request.Headers.TryAddWithoutValidation(headerName, key);
            }

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode
                    ? ConnectionTestResult.Ok($"HTTP {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message); // the remote's failure; the host's refusal propagates
            }
        }
    }
}
