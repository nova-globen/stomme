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

namespace Stomme.Modules.Connectors.Entuity
{
    /// <summary>
    /// Entuity: one GET per operation over the /api/v2 surface (or
    /// the legacy /api base), the rows read from a keyed array, a bare array or a lone object, the server id and the
    /// incident state as query scopes, a custom endpoint by raw path. Basic credentials; every request through the host's
    /// egress.
    /// </summary>
    public sealed class EntuityConnector : IConnector, IConnectorExtractor
    {
        private static readonly string[] ArrayProbeKeys = { "devices", "servers", "events", "incidents", "views", "objects", "items", "results", "data", "inventory", "credentials", "pollers" };

        public string ConnectorType => "entuity";

        public string DisplayName => "Entuity";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.NetworkInfrastructure };

        public Uri? WebsiteUrl => new("https://www.entuity.com");

        public Uri? ApiDocumentationUrl => null;

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("use_api_v2", "Use API v2", ConnectorConfigFieldType.Boolean, Description: "Target the /api/v2 surface (turn off to use the legacy /api base).")
            {
                Group = "Query",
                Default = "true",
            },
            new ConnectorConfigField("server_id", "Server id", ConnectorConfigFieldType.Text, Description: "Optional Entuity server id scoping the request (serverId).") { Group = "Query" },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("inventory", "Inventory", "Streams the managed-object inventory."),
            new ConnectorOperation("servers", "Servers", "Streams the Entuity servers."),
            new ConnectorOperation("events", "Events", "Streams events."),
            new ConnectorOperation("incidents", "Incidents", "Streams incidents.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("state", "State", ConnectorConfigFieldType.Text, Description: "Optional incident state filter (e.g. open); omit or 'all' for every state.") }),
            },
            new ConnectorOperation("views", "Views", "Streams the configured views."),
            new ConnectorOperation("view-objects", "View objects", "Streams the objects that belong to a view.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("view_id", "View id", ConnectorConfigFieldType.Text, Required: true, Description: "The view whose objects to read.") }),
            },
            new ConnectorOperation("custom", "Custom endpoint", "Reads any Entuity endpoint path you name.")
            {
                Parameters = new ConnectorConfigSchema(new[] { new ConnectorConfigField("path", "Endpoint", ConnectorConfigFieldType.Text, Required: true, Description: "An absolute endpoint path on the server, e.g. /api/v2/objects/123/ports.") }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var serverUrl = ServerUrl(context.Endpoint);
                var apiBase = ApiBase(context.Settings);
                using var client = BearerClient.Create(context.Egress, context.Timeout, BearerClient.Basic(context.Credentials, "Entuity"), acceptJson: true);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{serverUrl}{apiBase}/info")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "Entuity", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"Entuity responded with {(int)response.StatusCode}.")
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
            var serverUrl = ServerUrl(context.Endpoint);
            var apiBase = ApiBase(context.Settings);
            var authorization = BearerClient.Basic(context.Credentials, "Entuity");
            var url = BuildUrl(serverUrl, apiBase, context);

            using var client = BearerClient.Create(context.Egress, context.Timeout, authorization, acceptJson: true);
            foreach (var row in ReadRows(await BearerClient.GetAsync(client, url, FailureMessage, cancellationToken)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return FlattenedRows.Flatten(row);
            }
        }

        private static string BuildUrl(string serverUrl, string apiBase, ConnectorExtractContext context)
        {
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "inventory" : context.OperationName;
            var relativePath = operation.ToLowerInvariant() switch
            {
                "inventory" => $"{apiBase}/inventory",
                "servers" => $"{apiBase}/servers",
                "events" => $"{apiBase}/events",
                "incidents" => $"{apiBase}/incidents",
                "views" => $"{apiBase}/views",
                "view-objects" => $"{apiBase}/views/{Uri.EscapeDataString(Values.Get(context.Parameters, "view_id") ?? throw new InvalidOperationException("The view-objects operation requires a 'view_id' parameter."))}/objects",
                "custom" => CustomPath(Values.Get(context.Parameters, "path") ?? throw new InvalidOperationException("The custom operation requires a 'path' parameter.")),
                _ => throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Entuity connector."),
            };

            var url = new StringBuilder(serverUrl).Append(relativePath);
            var separator = '?';
            if (Values.Get(context.Settings, "server_id") is { } serverId)
            {
                url.Append(separator).Append("serverId=").Append(Uri.EscapeDataString(serverId));
                separator = '&';
            }

            if (string.Equals(operation, "incidents", StringComparison.OrdinalIgnoreCase) && Values.Get(context.Parameters, "state") is { } state && !string.Equals(state, "all", StringComparison.OrdinalIgnoreCase))
            {
                url.Append(separator).Append("state=").Append(Uri.EscapeDataString(state.ToLowerInvariant()));
            }

            return url.ToString();
        }

        private static string CustomPath(string path) => path.StartsWith('/') ? path : $"/{path}";

        private static List<JsonElement> ReadRows(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var rows = new List<JsonElement>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in root.EnumerateArray())
                {
                    rows.Add(row.Clone());
                }

                return rows;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return rows;
            }

            foreach (var key in ArrayProbeKeys)
            {
                if (root.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in array.EnumerateArray())
                    {
                        rows.Add(row.Clone());
                    }

                    return rows;
                }
            }

            rows.Add(root.Clone()); // a lone object (/info) is one record
            return rows;
        }

        private static string ApiBase(IReadOnlyDictionary<string, string> settings) => Values.GetBool(settings, "use_api_v2", defaultValue: true) ? "/api/v2" : "/api";

        private static string ServerUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The Entuity connection needs its server base URL as the endpoint (e.g. https://entuity.example.com).");
            }

            return Values.BaseUrl(endpoint, endpoint);
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Entuity rejected the credentials (401) - check the username/password.",
            HttpStatusCode.Forbidden => "Entuity denied access (403) - the account lacks permission for this resource.",
            _ => $"Entuity request failed with {(int)status} {status}.",
        };
    }
}
