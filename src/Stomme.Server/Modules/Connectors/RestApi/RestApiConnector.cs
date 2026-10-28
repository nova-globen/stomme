#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.RestApi
{
    /// <summary>
    /// The generic REST connector (BL-96 increment 1, lifted from the predecessor platform's `rest`): the endpoint is an absolute http(s)
    /// URL; the Bearer scheme's <c>token</c> credential is sent as a bearer token unless a custom Authorization header is
    /// set; a test issues a GET (optionally at the <c>path</c> setting) and treats a 2xx as reachable; the <c>read</c>
    /// operation issues a request and reads a JSON array (or a named array property) into records; the <c>send</c> operation
    /// issues one and answers a single record, the status code and the response body as text (BL-95, 2026-10-03).
    /// </summary>
    /// <remarks>
    /// <b>What changed in the port (D110, BL-96 spec default 1).</b> Every request goes through the client the HOST's egress
    /// hands the connector (<see cref="IConnectorEgress.CreateHttpClient"/>): private, link-local, loopback and metadata
    /// addresses are refused where the bytes go, redirects are off, one attempt within the timeout. the predecessor platform's per-connection
    /// <c>proxy_url</c> and <c>danger_accept_any_certificate</c> settings and its handler-factory constructor are DROPPED -
    /// a proxy and a certificate policy are the host's, not a way for a site admin to step around the egress. A refusal by
    /// the policy is not swallowed into a Fail: it propagates so the runner categorises it <c>DeniedByPolicy</c>; a
    /// timeout propagates as the cancellation it is, categorised <c>TimedOut</c>.
    /// </remarks>
    public sealed class RestApiConnector : IConnector, IConnectorExtractor
    {
        public string ConnectorType => "rest";

        public string DisplayName => "REST API";

        public AuthenticationType AuthType => AuthenticationType.Bearer;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("path", "Health check path", ConnectorConfigFieldType.Text, Required: false,
                Description: "Optional path appended to the endpoint for the reachability probe (e.g. /health)."),
            new ConnectorConfigField("headers", "Request headers", ConnectorConfigFieldType.Multiline, Required: false,
                Description: "Optional headers sent on every request, one per line as 'Name: value'."),
        });

        /// <summary>The single generic HTTP read operation; its parameters model verb/path/body/records shape.</summary>
        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("read", "Read", "Issues an HTTP request and reads a JSON array (or a named array property) into records.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("method", "HTTP method", ConnectorConfigFieldType.Select, Required: false, Description: "The HTTP verb to issue.")
                    {
                        Default = "GET",
                        Options = new[] { new ConnectorConfigFieldOption("GET"), new ConnectorConfigFieldOption("POST"), new ConnectorConfigFieldOption("PUT"), new ConnectorConfigFieldOption("DELETE") },
                    },
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: false, Description: "Optional path appended to the endpoint (e.g. /v1/devices)."),
                    new ConnectorConfigField("body", "Request body", ConnectorConfigFieldType.Multiline, Required: false, Description: "Optional JSON request body for POST/PUT."),
                    new ConnectorConfigField("content_type", "Content type", ConnectorConfigFieldType.Text, Required: false, Description: "Media type of the request body (defaults to application/json).")
                    {
                        Default = "application/json",
                    },
                    new ConnectorConfigField("headers", "Request headers", ConnectorConfigFieldType.Multiline, Required: false, Description: "Optional headers for this operation, one per line as 'Name: value'."),
                    new ConnectorConfigField("records_path", "Records path", ConnectorConfigFieldType.Text, Required: false, Description: "JSON property holding the array of rows (defaults to the top-level array, else values/value/data)."),
                }),
            },
            // BL-95's residue "an HTTP step that SENDS through a connection's credentials" (2026-10-03): a request whose answer
            // is not rows - creating an item, posting an event - as ONE record, the status and the response body as text.
            new ConnectorOperation("send", "Send", "Issues an HTTP request and answers one record: the status code and the response body as text.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("method", "HTTP method", ConnectorConfigFieldType.Select, Required: false, Description: "The HTTP verb to issue.")
                    {
                        Default = "POST",
                        Options = new[] { new ConnectorConfigFieldOption("POST"), new ConnectorConfigFieldOption("PUT"), new ConnectorConfigFieldOption("PATCH"), new ConnectorConfigFieldOption("DELETE"), new ConnectorConfigFieldOption("GET") },
                    },
                    new ConnectorConfigField("path", "Path", ConnectorConfigFieldType.Text, Required: false, Description: "Optional path appended to the endpoint (e.g. /v1/events)."),
                    new ConnectorConfigField("body", "Request body", ConnectorConfigFieldType.Multiline, Required: false, Description: "The request body (not sent with GET)."),
                    new ConnectorConfigField("content_type", "Content type", ConnectorConfigFieldType.Text, Required: false, Description: "Media type of the request body (defaults to application/json).")
                    {
                        Default = "application/json",
                    },
                    new ConnectorConfigField("headers", "Request headers", ConnectorConfigFieldType.Multiline, Required: false, Description: "Optional headers for this request, one per line as 'Name: value'."),
                }),
            },
        };

        /// <summary>The <c>send</c> operation's name; its one record carries <see cref="StatusField"/> and <see cref="BodyField"/>.</summary>
        public const string SendOperation = "send";

        public const string StatusField = "status";

        public const string BodyField = "body";

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryTarget(context.Endpoint, context.Settings.TryGetValue("path", out var path) ? path : null, out var target, out var problem))
            {
                return ConnectionTestResult.Fail(problem);
            }

            Dictionary<string, string> headers;
            try
            {
                headers = HeaderLines.Resolve(context.Settings, operationParameters: null);
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            Authorize(request, context.Credentials, headers);
            HeaderLines.Apply(request, headers);

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode
                    ? ConnectionTestResult.Ok($"HTTP {(int)response.StatusCode}.")
                    : ConnectionTestResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message); // the remote's failure; a policy refusal propagates
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!TryTarget(context.Endpoint, context.Parameters.TryGetValue("path", out var path) ? path : null, out var target, out var problem))
            {
                throw new InvalidOperationException(problem);
            }

            var send = string.Equals(context.OperationName, SendOperation, StringComparison.OrdinalIgnoreCase);
            var method = context.Parameters.TryGetValue("method", out var verb) && !string.IsNullOrWhiteSpace(verb) ? verb.Trim().ToUpperInvariant() : send ? "POST" : "GET";
            var headers = HeaderLines.Resolve(context.Settings, context.Parameters);

            using var client = context.Egress.CreateHttpClient(context.Timeout);
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            Authorize(request, context.Credentials, headers);

            if (method != "GET" && context.Parameters.TryGetValue("body", out var body) && !string.IsNullOrWhiteSpace(body))
            {
                var contentType = context.Parameters.TryGetValue("content_type", out var mediaType) && !string.IsNullOrWhiteSpace(mediaType) ? mediaType.Trim() : "application/json";
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypes.Parse(contentType, "content_type parameter");
            }

            HeaderLines.Apply(request, headers);

            string payload;
            int status;
            using (var response = await client.SendAsync(request, cancellationToken))
            {
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
                status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {status} {response.ReasonPhrase}.");
                }
            }

            if (send)
            {
                yield return new ConnectorRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { [StatusField] = (long)status, [BodyField] = payload });
                yield break;
            }

            foreach (var record in JsonRecords.Parse(payload, context.Parameters))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }

        /// <summary>The Bearer scheme's <c>token</c> as a bearer token, unless a custom Authorization header was given.</summary>
        private static void Authorize(HttpRequestMessage request, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> headers)
        {
            if (credentials.TryGetValue("token", out var token) && !string.IsNullOrEmpty(token) && !headers.ContainsKey("Authorization"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        private static bool TryTarget(string endpoint, string? path, out Uri target, out string problem)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                target = null!;
                problem = "Endpoint must be an absolute http(s) URL.";
                return false;
            }

            target = uri;
            if (!string.IsNullOrWhiteSpace(path) && Uri.TryCreate(uri, path, out var relative))
            {
                target = relative;
            }

            problem = string.Empty;
            return true;
        }
    }
}
