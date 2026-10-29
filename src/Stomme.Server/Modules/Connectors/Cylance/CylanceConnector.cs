#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.Cylance
{
    /// <summary>
    /// BlackBerry Cylance (lifted from the predecessor platform's <c>cylance</c>): the console's bespoke JWT-assertion
    /// exchange (an ApiKeyPair - the application id and secret - plus the tenant id setting feeding the assertion's
    /// <c>tid</c> claim) through <see cref="CylanceTokenClient"/>, the legacy region enum collapsed into the endpoint
    /// (regional hosts such as <c>protectapi-euc1.cylance.com</c> are endpoint overrides), 1-based <c>page</c>/<c>page_size</c>
    /// paging (console cap 200) over <c>page_items</c>/<c>total_pages</c>. Every request through the host's egress.
    /// </summary>
    public sealed class CylanceConnector(TimeProvider? timeProvider = null) : IConnector, IConnectorExtractor
    {
        internal const string DefaultEndpoint = "https://protectapi.cylance.com";
        internal const string DefaultPageSize = "200";
        private const int MaxPageSize = 200;

        private readonly CylanceTokenClient tokenClient = new(timeProvider);

        public string ConnectorType => "cylance";

        public string DisplayName => "Arctic Wolf Aurora Endpoint Security (formerly Cylance)"; // the licensing review's name (BRAND_ASSET_REGISTRY.json); the type id stays cylance

        public AuthenticationType AuthType => AuthenticationType.ApiKeyPair;

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.SecurityVulnerability };

        public Uri? WebsiteUrl => new("https://www.arcticwolf.com/cylance/");

        public Uri? ApiDocumentationUrl => new("https://docs.blackberry.com/en/unified-endpoint-security/blackberry-ues/api-reference/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("tenant_id", "Tenant id", ConnectorConfigFieldType.Text, Required: true, Description: "The Cylance console tenant id (the JWT assertion's tid claim).") { Group = "Authentication" },
            new ConnectorConfigField("api_limit", "Page size", ConnectorConfigFieldType.Integer, Description: "Rows requested per page (the console caps at 200).")
            {
                Group = "Query",
                Default = DefaultPageSize,
            },
        });

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation("devices", "Devices", "Streams devices (/devices/v2).") { Parameters = LimitOnlyParameters() },
            new ConnectorOperation("threats", "Threats", "Streams threats (/threats/v2).") { Parameters = LimitOnlyParameters() },
            new ConnectorOperation("zones", "Zones", "Streams zones (/zones/v2).") { Parameters = LimitOnlyParameters() },
            new ConnectorOperation("custom", "Custom resource", "Streams any paged v2 collection by its path (e.g. /policies/v2).")
            {
                Parameters = new ConnectorConfigSchema(new[]
                {
                    new ConnectorConfigField("path", "Resource path", ConnectorConfigFieldType.Text, Required: true, Description: "The API path to page through, e.g. /users/v2."),
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
                var token = await AcquireTokenAsync(context.Egress, baseUrl, context.Credentials, context.Settings, context.Timeout, cancellationToken);
                using var client = BearerClient.Create(context.Egress, context.Timeout, token, acceptJson: true);
                using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/devices/v2?page=1&page_size=1")), cancellationToken);
                return response.IsSuccessStatusCode
                    ? ConnectionTestResult.Ok($"Cylance responded with {(int)response.StatusCode}.")
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
            var baseUrl = Values.BaseUrl(context.Endpoint, DefaultEndpoint);
            var path = ResolvePath(context.OperationName, context.Parameters);
            var pageSize = PageSize(context.Settings, context.Parameters);
            var token = await AcquireTokenAsync(context.Egress, baseUrl, context.Credentials, context.Settings, context.Timeout, cancellationToken);

            using var client = BearerClient.Create(context.Egress, context.Timeout, token, acceptJson: true);
            var page = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await BearerClient.GetAsync(client, $"{baseUrl}{path}?page={page}&page_size={pageSize}", FailureMessage, cancellationToken);
                var (items, totalPages) = ReadPage(payload);
                foreach (var item in items)
                {
                    yield return FlattenedRows.Flatten(item);
                }

                if (items.Count == 0 || totalPages is null || page >= totalPages)
                {
                    yield break;
                }

                page++;
            }
        }

        private static ConnectorConfigSchema LimitOnlyParameters() => new(new[]
        {
            new ConnectorConfigField("limit", "Page size override", ConnectorConfigFieldType.Integer, Description: "Overrides the connection's configured page size for this operation."),
        });

        private static string ResolvePath(string operationName, IReadOnlyDictionary<string, string> parameters)
        {
            var operation = string.IsNullOrWhiteSpace(operationName) ? "devices" : operationName.ToLowerInvariant();
            return operation switch
            {
                "devices" => "/devices/v2",
                "threats" => "/threats/v2",
                "zones" => "/zones/v2",
                "custom" => CustomPath(parameters),
                _ => throw new InvalidOperationException($"Unknown operation '{operationName}' for the Cylance connector."),
            };
        }

        private static string CustomPath(IReadOnlyDictionary<string, string> parameters)
        {
            var path = Values.Get(parameters, "path") ?? throw new InvalidOperationException("The custom operation requires a 'path' parameter.");
            if (!path.StartsWith('/'))
            {
                throw new InvalidOperationException("The custom path must start with '/' (e.g. /users/v2).");
            }

            return path;
        }

        private static (List<JsonElement> Items, long? TotalPages) ReadPage(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("page_items", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The Cylance response carries no 'page_items' array.");
            }

            var items = new List<JsonElement>();
            foreach (var row in rows.EnumerateArray())
            {
                items.Add(row.Clone());
            }

            long? totalPages = null;
            if (root.TryGetProperty("total_pages", out var total) && total.TryGetInt64(out var pages))
            {
                totalPages = pages;
            }

            return (items, totalPages);
        }

        private Task<string> AcquireTokenAsync(IConnectorEgress egress, string baseUrl, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var appId = Values.Get(credentials, "access-key") ?? throw new InvalidOperationException("The Cylance connection is missing 'access-key' (the application id) - check the connection's credentials.");
            var appSecret = Values.Get(credentials, "secret-key") ?? throw new InvalidOperationException("The Cylance connection is missing 'secret-key' (the application secret) - check the connection's credentials.");
            var tenantId = Values.Get(settings, "tenant_id") ?? throw new InvalidOperationException("The Cylance connection is missing the 'tenant_id' setting.");
            return tokenClient.GetAccessTokenAsync(egress, baseUrl, appId, appSecret, tenantId, timeout, cancellationToken);
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

        private static string FailureMessage(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized => "Cylance rejected the token (401) - check the application id/secret and tenant id.",
            HttpStatusCode.Forbidden => "Cylance denied access (403) - the application integration lacks the required read permissions.",
            _ => $"Cylance request failed with {(int)status} {status}.",
        };
    }

    /// <summary>
    /// Cylance's bespoke token exchange: a short-lived HS256 JWT assertion minted from the application id, secret and tenant,
    /// posted to <c>/auth/v2/token</c> through the egress's client, the returned bearer token cached until shortly before its
    /// expiry (the legacy bridge fetched one token per job and got 401s mid-run once the fixed 30-minute lifetime elapsed).
    /// Not the standard client-credentials grant, so the family's provider does not apply; the JWT is hand-rolled (HMAC-SHA256
    /// and Base64Url) so no JWT package is needed. The cache is per credential set and served only to an egress that admits
    /// the token host, as the family's provider does.
    /// </summary>
    internal sealed class CylanceTokenClient(TimeProvider? timeProvider = null)
    {
        /// <summary>The legacy assertion lifetime; the issued bearer token expires with the assertion.</summary>
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

        private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

        private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
        private readonly ConcurrentDictionary<string, CachedToken> cache = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim gate = new(1, 1);

        public async Task<string> GetAccessTokenAsync(IConnectorEgress egress, string baseUrl, string appId, string appSecret, string tenantId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var tokenUri = new Uri($"{baseUrl}/auth/v2/token");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tokenUri.AbsoluteUri}\n{appId}\n{appSecret}\n{tenantId}")));
            if (TryGetFresh(key, out var cached))
            {
                await AdmitAsync(egress, tokenUri, cancellationToken);
                return cached;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (TryGetFresh(key, out cached))
                {
                    await AdmitAsync(egress, tokenUri, cancellationToken);
                    return cached;
                }

                var token = await FetchAsync(egress, tokenUri, appId, appSecret, tenantId, timeout, cancellationToken);
                cache[key] = token;
                return token.AccessToken;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task AdmitAsync(IConnectorEgress egress, Uri tokenUri, CancellationToken cancellationToken)
        {
            if (await egress.ResolveAndPinAsync(tokenUri.Host, tokenUri.Port, cancellationToken) is null)
            {
                throw new ConnectorEgressDeniedException($"The egress policy refuses the token endpoint host '{tokenUri.Host}'.");
            }
        }

        private bool TryGetFresh(string key, out string accessToken)
        {
            if (cache.TryGetValue(key, out var entry) && entry.ExpiresAt > clock.GetUtcNow())
            {
                accessToken = entry.AccessToken;
                return true;
            }

            accessToken = string.Empty;
            return false;
        }

        private async Task<CachedToken> FetchAsync(IConnectorEgress egress, Uri tokenUri, string appId, string appSecret, string tenantId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var body = new JsonObject { ["auth_token"] = MintAssertion(appId, appSecret, tenantId, now) }.ToJsonString();
            using var client = egress.CreateHttpClient(timeout > TimeSpan.Zero ? timeout : DefaultTimeout);
            using var response = await RetryAfterPolicy.Default.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, tokenUri) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The Cylance token exchange failed with {(int)response.StatusCode} {response.StatusCode}: {Excerpt(payload)}");
            }

            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("access_token", out var token) ||
                token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
            {
                throw new InvalidOperationException($"The Cylance token exchange returned no access_token: {Excerpt(payload)}");
            }

            return new CachedToken(token.GetString()!, now + TokenLifetime - ExpirySkew);
        }

        private static string MintAssertion(string appId, string appSecret, string tenantId, DateTimeOffset now)
        {
            var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
            var payload = new JsonObject
            {
                ["sub"] = appId,
                ["iss"] = "http://cylance.com",
                ["iat"] = now.ToUnixTimeSeconds(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["exp"] = (now + TokenLifetime).ToUnixTimeSeconds(),
                ["tid"] = tenantId,
            };
            var signingInput = $"{header}.{Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()))}";
            var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(signingInput));
            return $"{signingInput}.{Base64Url(signature)}";
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Excerpt(string payload) => payload.Length <= 500 ? payload : payload[..500];

        private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
    }
}
