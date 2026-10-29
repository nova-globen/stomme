#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Taegis
{
    /// <summary>
    /// Secureworks Taegis XDR: OAuth2 client credentials through
    /// the family's token provider - Taegis takes a scope-less grant, and when the <c>token-url</c> credential is empty it
    /// is derived from the connection endpoint (<c>{endpoint}/auth/api/v2/auth/token</c>). Everything reads through
    /// <c>POST /graphql</c> with the legacy queries verbatim; every request through the host's egress.
    /// </summary>
    public sealed class TaegisConnector(OAuth2TokenProvider? tokenProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://api.ctpx.secureworks.com";
        internal const string TokenPath = "/auth/api/v2/auth/token";
        internal const string DefaultPageSize = "1000";

        private readonly OAuth2TokenProvider tokens = tokenProvider ?? new OAuth2TokenProvider();

        public string ConnectorType => "taegis";

        public string DisplayName => "Sophos Taegis XDR (formerly Secureworks Taegis XDR)"; // the licensing review's name (BRAND_ASSET_REGISTRY.json); the type id stays taegis

        public AuthenticationType AuthType => AuthenticationType.OAuth2ClientCredentials;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri? WebsiteUrl => new("https://www.sophos.com/en-us/products/taegis");

        public Uri? ApiDocumentationUrl => new("https://docs.ctpx.secureworks.com/apis/api_authenticate/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per GraphQL page.")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
            new ConnectorConfigField("tenant_id", "Tenant id", ConnectorConfigFieldType.Text, Description: "Restricts alerts/events to one tenant (partner multi-tenant accounts).")
            {
                Group = "Query",
                HelpText = "Leave empty for single-tenant accounts.",
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("alerts", "Alerts", "Streams alerts (allAlerts) with time/severity/status filters.") { Parameters = FilteredParameters() },
            new ConnectorOperation("events", "Events", "Streams events (allEvents) with time/severity/status filters.") { Parameters = FilteredParameters() },
            new ConnectorOperation("investigations", "Investigations", "Streams investigations (allInvestigations).") { Parameters = LimitOnlyParameters() },
            new ConnectorOperation("endpoint-assets", "Endpoint assets", "Streams endpoint assets (allAssets).") { Parameters = LimitOnlyParameters() },
            new ConnectorOperation("custom", "Custom query", "Runs one custom GraphQL query verbatim (single page).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("query", "GraphQL query", ConnectorConfigFieldType.Multiline, Required: true, Description: "The GraphQL document to POST."),
                    new ConnectorConfigField("variables", "Variables (JSON)", ConnectorConfigFieldType.Multiline, Description: "Optional variables object, passed through verbatim."),
                }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
                var token = await AcquireTokenAsync(context.Egress, baseUrl, context.Credentials, context.Timeout, cancellationToken);
                using var client = BearerClient.Create(context.Egress, context.Timeout, token, acceptJson: true);
                var payload = await PostGraphQlAsync(client, baseUrl, TaegisQueries.EndpointAssets, new JsonObject { ["offset"] = 0, ["limit"] = 1 }, cancellationToken);
                using var document = JsonDocument.Parse(payload);
                EnsureNoGraphQlErrors(document.RootElement);
                return ConnectionTestResult.Ok("Taegis GraphQL responded.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = string.IsNullOrWhiteSpace(context.OperationName) ? "alerts" : context.OperationName.ToLowerInvariant();
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var token = await AcquireTokenAsync(context.Egress, baseUrl, context.Credentials, context.Timeout, cancellationToken);

            using var client = BearerClient.Create(context.Egress, context.Timeout, token, acceptJson: true);
            if (operation == "custom")
            {
                var query = Values.Get(context.Parameters, "query") ?? throw new InvalidOperationException("The custom operation requires a 'query' parameter.");
                var payload = await PostGraphQlAsync(client, baseUrl, query, ParseVariables(context.Parameters), cancellationToken);
                foreach (var row in ExtractRows(payload, rowPath: null))
                {
                    yield return FlattenedRows.Flatten(row);
                }

                yield break;
            }

            var (document, rowPath) = operation switch
            {
                "alerts" => (TaegisQueries.Alerts, new[] { "allAlerts", "alerts" }),
                "events" => (TaegisQueries.Events, new[] { "allEvents", "events" }),
                "investigations" => (TaegisQueries.Investigations, new[] { "allInvestigations", "investigations" }),
                "endpoint-assets" => (TaegisQueries.EndpointAssets, new[] { "allAssets", "assets" }),
                _ => throw new InvalidOperationException($"Unknown operation '{context.OperationName}' for the Taegis connector."),
            };

            var limit = PageSize(context.Settings, context.Parameters);
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await PostGraphQlAsync(client, baseUrl, document, BuildVariables(operation, limit, offset, context.Settings, context.Parameters), cancellationToken);
                var rows = ExtractRows(payload, rowPath);
                foreach (var row in rows)
                {
                    yield return FlattenedRows.Flatten(row);
                }

                // The legacy stop rule: a short (or empty) page ends the scan.
                if (rows.Count < limit)
                {
                    yield break;
                }

                offset += limit;
            }
        }

        private static ConnectorConfigSchema FilteredParameters() => new(new[]
        {
            new ConnectorConfigField("start_time", "Start time", ConnectorConfigFieldType.Text, Description: "Optional ISO-8601 lower bound."),
            new ConnectorConfigField("end_time", "End time", ConnectorConfigFieldType.Text, Description: "Optional ISO-8601 upper bound."),
            new ConnectorConfigField("severity", "Severity filter", ConnectorConfigFieldType.Text, Description: "Optional comma-separated severities (critical,high,medium,low,informational)."),
            new ConnectorConfigField("status", "Status filter", ConnectorConfigFieldType.Text, Description: "Optional comma-separated statuses (open,in_progress,closed)."),
            new ConnectorConfigField("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
        });

        private static ConnectorConfigSchema LimitOnlyParameters() => new(new[]
        {
            new ConnectorConfigField("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
        });

        // Alerts and events wrap everything in the AllAlertsInput/AllEventsInput `in` object (with the optional tenant restriction); investigations page with page/perPage; assets with plain offset/limit.
        private static JsonObject BuildVariables(string operation, int limit, int offset, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            switch (operation)
            {
                case "alerts":
                case "events":
                    var input = new JsonObject { ["limit"] = limit, ["offset"] = offset };
                    if (Values.Get(parameters, "start_time") is { } start)
                    {
                        input["startTime"] = start;
                    }

                    if (Values.Get(parameters, "end_time") is { } end)
                    {
                        input["endTime"] = end;
                    }

                    if (Values.Get(parameters, "severity") is { } severity)
                    {
                        input["severity"] = CsvArray(severity);
                    }

                    if (Values.Get(parameters, "status") is { } status)
                    {
                        input["status"] = CsvArray(status);
                    }

                    if (Values.Get(settings, "tenant_id") is { } tenant)
                    {
                        input["tenantId"] = tenant;
                    }

                    return new JsonObject { ["in"] = input };
                case "investigations":
                    return new JsonObject { ["page"] = (offset / limit) + 1, ["perPage"] = limit };
                default:
                    return new JsonObject { ["offset"] = offset, ["limit"] = limit };
            }
        }

        private static JsonArray CsvArray(string csv)
        {
            var array = new JsonArray();
            foreach (var part in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                array.Add(part);
            }

            return array;
        }

        private static async Task<string> PostGraphQlAsync(HttpClient client, string baseUrl, string query, JsonNode? variables, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
            using var response = await RetryAfterPolicy.Default.SendAsync(client,
                () => new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}/graphql")) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(FailureMessage(response.StatusCode));
            }

            return payload;
        }

        /// <param name="rowPath">The known <c>data</c> path for a first-class operation; null for a custom query, which falls back to the legacy heuristic - the first array found under <c>data</c>, directly or inside its first object.</param>
        private static List<JsonElement> ExtractRows(string payload, string[]? rowPath)
        {
            using var document = JsonDocument.Parse(payload);
            EnsureNoGraphQlErrors(document.RootElement);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The Taegis GraphQL response carries no data object.");
            }

            JsonElement rows;
            if (rowPath is not null)
            {
                var current = data;
                foreach (var segment in rowPath)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    {
                        throw new InvalidOperationException($"The Taegis response carries no '{string.Join('.', rowPath)}' rows.");
                    }
                }

                rows = current;
            }
            else if (!TryFindFirstArray(data, out rows))
            {
                throw new InvalidOperationException("The custom query response contains no row array under data.");
            }

            if (rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The resolved Taegis rows node is not an array.");
            }

            var list = new List<JsonElement>();
            foreach (var row in rows.EnumerateArray())
            {
                list.Add(row.Clone());
            }

            return list;
        }

        private static bool TryFindFirstArray(JsonElement data, out JsonElement rows)
        {
            foreach (var property in data.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    rows = property.Value;
                    return true;
                }

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nested in property.Value.EnumerateObject())
                    {
                        if (nested.Value.ValueKind == JsonValueKind.Array)
                        {
                            rows = nested.Value;
                            return true;
                        }
                    }
                }
            }

            rows = default;
            return false;
        }

        private static void EnsureNoGraphQlErrors(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var message = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : first.GetRawText();
                throw new InvalidOperationException($"Taegis GraphQL returned {errors.GetArrayLength()} error(s): {message}");
            }
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, string baseUrl, IReadOnlyDictionary<string, string> credentials, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Taegis takes a scope-less client-credentials grant; the provider omits an empty scope. A missing token-url is the endpoint's own token path.
            var tokenUrl = Values.Get(credentials, "token-url") ?? $"{baseUrl}{TokenPath}";
            return tokens.GetAccessTokenAsync(egress, new OAuth2TokenRequest(tokenUrl, Values.Get(credentials, "client-id") ?? string.Empty, Values.Get(credentials, "client-secret") ?? string.Empty, string.Empty) { Timeout = timeout }, cancellationToken);
        }

        private static int PageSize(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> parameters)
        {
            var raw = Values.Get(parameters, "limit") ?? Values.GetOrDefault(settings, "api_limit", DefaultPageSize);
            if (!int.TryParse(raw, out var limit) || limit <= 0)
            {
                throw new InvalidOperationException($"The page size '{raw}' is not a positive integer.");
            }

            return limit;
        }

        private static JsonNode? ParseVariables(IReadOnlyDictionary<string, string> parameters)
        {
            if (Values.Get(parameters, "variables") is not { } raw)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("The custom operation's 'variables' parameter is not valid JSON.", ex);
            }
        }

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Taegis rejected the token (401) - check the client id/secret and the environment's token URL.",
            HttpStatusCode.Forbidden => "Taegis denied access (403) - check the client's role and tenant permissions.",
            _ => $"Taegis GraphQL request failed with {(int)status} {status}.",
        };
    }

    /// <summary>The GraphQL documents ported verbatim from the legacy bridge - one per first-class operation. Row locations: <c>data.allAlerts.alerts</c>, <c>data.allInvestigations.investigations</c>, <c>data.allAssets.assets</c>, <c>data.allEvents.events</c>.</summary>
    internal static class TaegisQueries
    {
        public const string Alerts = """
            query allAlerts($in: AllAlertsInput!) {
                allAlerts(in: $in) {
                    alerts {
                        id
                        tenant_id
                        metadata {
                            title
                            description
                            severity
                            confidence
                            created_at
                            updated_at
                        }
                        status
                        resolution_status
                        suppressed
                        entities {
                            type
                            value
                        }
                    }
                    totalCount
                    pageInfo {
                        hasNextPage
                        endCursor
                    }
                }
            }
            """;

        public const string Investigations = """
            query allInvestigations($page: Int, $perPage: Int) {
                allInvestigations(page: $page, perPage: $perPage) {
                    investigations {
                        id
                        tenant_id
                        description
                        key_findings
                        priority
                        status
                        type
                        created_at
                        updated_at
                        alerts_count
                        events_count
                    }
                    totalCount
                    pageInfo {
                        hasNextPage
                        endCursor
                    }
                }
            }
            """;

        public const string EndpointAssets = """
            query allAssets($offset: Int, $limit: Int) {
                allAssets(offset: $offset, limit: $limit) {
                    assets {
                        id
                        hostId
                        hostname
                        sensorVersion
                        osFamily
                        osVersion
                        osDistributor
                        lastSeenAt
                        firstSeenAt
                        ingestTime
                        tags
                    }
                    totalCount
                }
            }
            """;

        public const string Events = """
            query allEvents($in: AllEventsInput!) {
                allEvents(in: $in) {
                    events {
                        id
                        tenant_id
                        event_time
                        resource_id
                        event_type
                        severity
                        original_data
                    }
                    totalCount
                    pageInfo {
                        hasNextPage
                        endCursor
                    }
                }
            }
            """;
    }
}
