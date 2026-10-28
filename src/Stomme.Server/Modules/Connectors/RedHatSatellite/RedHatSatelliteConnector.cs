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

namespace Stomme.Modules.Connectors.RedHatSatellite
{
    /// <summary>
    /// Red Hat Satellite (BL-96 increment 4, lifted from the predecessor platform's <c>satellite</c>): the Foreman, Katello and Foreman-Tasks
    /// surfaces paged by <c>page</c>/<c>per_page</c> (clamped to 1000) using the <c>subtotal</c> metadata, the organization
    /// and search scopes on every call, a custom endpoint by raw path. Basic credentials; every request through the host's
    /// egress (D110).
    /// </summary>
    public sealed class RedHatSatelliteConnector : IConnector, IConnectorExtractor
    {
        internal const string DefaultPerPage = "100";
        private const string Foreman = "/api/v2";
        private const string Katello = "/katello/api";
        private const string ForemanTasks = "/foreman_tasks/api";

        private static readonly Dictionary<string, (string ApiBase, string Segment)> OperationEndpoints = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hosts"] = (Foreman, "hosts"),
            ["hostgroups"] = (Foreman, "hostgroups"),
            ["subnets"] = (Foreman, "subnets"),
            ["organizations"] = (Foreman, "organizations"),
            ["content-views"] = (Katello, "content_views"),
            ["repositories"] = (Katello, "repositories"),
            ["subscriptions"] = (Katello, "subscriptions"),
            ["errata"] = (Katello, "errata"),
            ["tasks"] = (ForemanTasks, "tasks"),
        };

        public string ConnectorType => "satellite";

        public string DisplayName => "Red Hat Satellite";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.NetworkInfrastructure };

        public Uri? WebsiteUrl => new("https://www.redhat.com/en/technologies/management/satellite");

        public Uri? ApiDocumentationUrl => new("https://docs.redhat.com/en/documentation/red_hat_satellite");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("per_page", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per page (per_page; 1-1000).")
            {
                Group = "Query",
                Default = DefaultPerPage,
            },
            new ConnectorConfigField("organization_id", "Organization id", ConnectorConfigFieldType.Text, Description: "Optional organization scope (organization_id).") { Group = "Query" },
            new ConnectorConfigField("search", "Search", ConnectorConfigFieldType.Text, Description: "Optional Foreman search DSL applied to every operation (search).") { Group = "Query" },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("hosts", "Hosts", "Streams managed hosts (Foreman)."),
            new ConnectorOperation("hostgroups", "Host groups", "Streams host groups (Foreman)."),
            new ConnectorOperation("subnets", "Subnets", "Streams subnets (Foreman)."),
            new ConnectorOperation("organizations", "Organizations", "Streams organizations (Foreman)."),
            new ConnectorOperation("content-views", "Content views", "Streams content views (Katello)."),
            new ConnectorOperation("repositories", "Repositories", "Streams repositories (Katello)."),
            new ConnectorOperation("subscriptions", "Subscriptions", "Streams subscriptions (Katello)."),
            new ConnectorOperation("errata", "Errata", "Streams errata (Katello)."),
            new ConnectorOperation("tasks", "Tasks", "Streams tasks (Foreman-Tasks)."),
            new ConnectorOperation("custom", "Custom endpoint", "Reads any Satellite endpoint path you name.")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Endpoint", ConnectorConfigFieldType.Text, Required: true, Description: "An absolute endpoint path on the server, e.g. /api/v2/media or /katello/api/products."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var serverUrl = ServerUrl(context.Endpoint);
                using var client = BearerClient.Create(context.Egress, context.Timeout, BearerClient.Basic(context.Credentials, "Red Hat Satellite"), acceptJson: true);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{serverUrl}{Foreman}/status")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? await JsonAnswers.NotJsonAsync(response, "Red Hat Satellite", cancellationToken) is { } notJson ? ConnectionTestResult.Fail(notJson) : ConnectionTestResult.Ok($"Red Hat Satellite responded with {(int)response.StatusCode}.")
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
            var authorization = BearerClient.Basic(context.Credentials, "Red Hat Satellite");
            var endpoint = EndpointUrl(serverUrl, context);
            var perPage = PerPage(context.Settings);
            var orgId = Values.Get(context.Settings, "organization_id");
            var search = Values.Get(context.Settings, "search");

            using var client = BearerClient.Create(context.Egress, context.Timeout, authorization, acceptJson: true);
            var page = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rows, subtotal) = ReadPage(await BearerClient.GetAsync(client, BuildUrl(endpoint, page, perPage, orgId, search), FailureMessage, cancellationToken));
                foreach (var row in rows)
                {
                    yield return FlattenedRows.Flatten(row);
                }

                var totalPages = subtotal > 0 ? (int)Math.Ceiling(subtotal / (double)perPage) : page;
                if (page >= totalPages || rows.Count < perPage || rows.Count == 0)
                {
                    yield break;
                }

                page++;
            }
        }

        private static string EndpointUrl(string serverUrl, ConnectorExtractContext context)
        {
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "hosts" : context.OperationName;
            if (string.Equals(operation, "custom", StringComparison.OrdinalIgnoreCase))
            {
                var path = Values.Get(context.Parameters, "path") ?? throw new InvalidOperationException("The custom operation requires a 'path' parameter.");
                return serverUrl + (path.StartsWith('/') ? path : $"/{path}");
            }

            if (!OperationEndpoints.TryGetValue(operation, out var endpoint))
            {
                throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Red Hat Satellite connector.");
            }

            return $"{serverUrl}{endpoint.ApiBase}/{endpoint.Segment}";
        }

        private static string BuildUrl(string endpoint, int page, int perPage, string? orgId, string? search)
        {
            var url = new StringBuilder(endpoint).Append(endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?').Append("page=").Append(page).Append("&per_page=").Append(perPage);
            if (orgId is not null)
            {
                url.Append("&organization_id=").Append(Uri.EscapeDataString(orgId));
            }

            if (search is not null)
            {
                url.Append("&search=").Append(Uri.EscapeDataString(search));
            }

            return url.ToString();
        }

        private static (List<JsonElement> Rows, int Subtotal) ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var rows = new List<JsonElement>();
            if (root.ValueKind != JsonValueKind.Object)
            {
                if (root.ValueKind == JsonValueKind.Array)
                {
                    CopyRows(root, rows);
                }

                return (rows, rows.Count);
            }

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                CopyRows(results, rows);
                return (rows, ReadInt(root, "subtotal") ?? ReadInt(root, "total") ?? rows.Count);
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                CopyRows(data, rows);
                return (rows, ReadInt(root, "total") ?? rows.Count);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    CopyRows(property.Value, rows);
                    return (rows, rows.Count);
                }
            }

            if (root.TryGetProperty("id", out _))
            {
                rows.Add(root.Clone());
            }

            return (rows, rows.Count);
        }

        private static void CopyRows(JsonElement array, List<JsonElement> rows)
        {
            foreach (var row in array.EnumerateArray())
            {
                rows.Add(row.Clone());
            }
        }

        private static int? ReadInt(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

        private static int PerPage(IReadOnlyDictionary<string, string> settings)
        {
            var raw = Values.GetOrDefault(settings, "per_page", DefaultPerPage);
            if (!int.TryParse(raw, out var size) || size <= 0)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not a positive integer.");
            }

            return Math.Clamp(size, 1, 1000);
        }

        private static string ServerUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("The Red Hat Satellite connection needs its server base URL as the endpoint (e.g. https://satellite.example.com).");
            }

            return Values.BaseUrl(endpoint, endpoint);
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Red Hat Satellite rejected the credentials (401) - check the username/password.",
            HttpStatusCode.Forbidden => "Red Hat Satellite denied access (403) - the account lacks permission for this resource.",
            _ => $"Red Hat Satellite request failed with {(int)status} {status}.",
        };
    }
}
