#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;

namespace Stomme.Modules.Connectors.Shared
{
    /// <summary>The inputs of one OAuth2 client-credentials acquisition: the scheme's <c>token-url</c>, <c>client-id</c> and <c>client-secret</c> fields, and the scope the connector asks for (empty for a scope-less grant, which is then omitted).</summary>
    public sealed record OAuth2TokenRequest(string TokenUrl, string ClientId, string ClientSecret, string Scope)
    {
        /// <summary>The timeout of the token call; the connection's timeout, and a conservative default when unset.</summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>A grant's own fields, sent after the standard ones and part of the grant the cache keys on - Box's names whom the app acts as.</summary>
        public IReadOnlyList<KeyValuePair<string, string>>? Extra { get; init; }
    }

    /// <summary>The inputs of one OAuth2 refresh-token acquisition (the scheme's <c>refresh-token</c> field); the client fields ride along when configured and are omitted when empty - the Flexera family posts a bare grant.</summary>
    public sealed record OAuth2RefreshTokenRequest(string TokenUrl, string RefreshToken)
    {
        public string? ClientId { get; init; }

        public string? ClientSecret { get; init; }

        /// <summary>The scope asked for, sent only when given - Microsoft's v2 endpoint wants one on a refresh; Google's and the Flexera family's grants carry none.</summary>
        public string? Scope { get; init; }

        /// <summary>True to send the client as HTTP Basic rather than in the form - Reddit's token endpoint takes it no other way.</summary>
        public bool ClientAsBasic { get; init; }

        /// <summary>The User-Agent the token call names its caller with, sent only when given - Reddit asks every caller for a descriptive one.</summary>
        public string? UserAgent { get; init; }

        public TimeSpan? Timeout { get; init; }
    }

    /// <summary>The inputs of one OAuth2 account-credentials acquisition - Zoom's server-to-server grant: the account the app belongs to, the client sent as HTTP Basic, never in the form.</summary>
    public sealed record OAuth2AccountCredentialsRequest(string TokenUrl, string AccountId, string ClientId, string ClientSecret)
    {
        public TimeSpan? Timeout { get; init; }
    }

    /// <summary>
    /// The one OAuth2 token acquisition every connector of the family shares (lifted from the predecessor platform's
    /// <c>CachingOAuth2TokenProvider</c>): the grant is posted as a form through the client the HOST's egress hands the
    /// connector, the response binds <c>access_token</c> by its exact wire name (a legacy bridge never bound it and
    /// sent <c>Bearer null</c>), an incomplete request fails loudly before any call (another legacy bridge logged the empty
    /// secret and carried on), and the token is cached until shortly before <c>expires_in</c> elapses.
    /// </summary>
    /// <remarks>
    /// <b>The cache is per credential set, and a cached token is served only to an egress that admits the token host.</b>
    /// The connector is one singleton for every site of the installation, so the cache spans sites: its key is a digest of
    /// the whole grant (URL, grant type, client, secret, scope, refresh token), so two sites share an entry only when they
    /// hold the same credentials - the same principal. What the egress decides is WHERE bytes may go, per site; a site whose
    /// policy refuses the token host (an on-premises issuer another site allowlisted) must not be served the token another
    /// site fetched from it, so a cache hit still asks the requesting egress to resolve and pin the token host - one
    /// resolution, no socket - and is refused as a denial when the egress answers none.
    /// </remarks>
    public sealed class OAuth2TokenProvider
    {
        /// <summary>Tokens are refreshed this long before their reported expiry.</summary>
        private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

        /// <summary>The cache lifetime assumed when the endpoint reports no <c>expires_in</c>.</summary>
        private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

        private readonly TimeProvider timeProvider;
        private readonly RetryAfterPolicy retry;
        private readonly ConcurrentDictionary<string, CachedToken> cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

        public OAuth2TokenProvider(TimeProvider? timeProvider = null, RetryAfterPolicy? retry = null)
        {
            this.timeProvider = timeProvider ?? TimeProvider.System;
            this.retry = retry ?? RetryAfterPolicy.Default;
        }

        /// <summary>A valid access token for the client-credentials grant: cached until shortly before expiry; an <see cref="InvalidOperationException"/> naming the missing field, the status or the missing <c>access_token</c> otherwise.</summary>
        public Task<string> GetAccessTokenAsync(IConnectorEgress egress, OAuth2TokenRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(egress);
            ArgumentNullException.ThrowIfNull(request);
            Require(request.TokenUrl, "token-url");
            Require(request.ClientId, "client-id");
            Require(request.ClientSecret, "client-secret");

            // Scope is omitted when empty - some endpoints (Taegis) take a plain client-credentials grant with no scope parameter at all.
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", request.ClientId),
                new("client_secret", request.ClientSecret),
            };
            if (!string.IsNullOrWhiteSpace(request.Scope))
            {
                form.Add(new("scope", request.Scope));
            }

            if (request.Extra is not null)
            {
                form.AddRange(request.Extra);
            }

            return AcquireAsync(egress, request.TokenUrl, form, request.Timeout, cancellationToken);
        }

        /// <summary>A valid access token for the refresh-token grant, with the same caching and failure semantics.</summary>
        public Task<string> GetAccessTokenAsync(IConnectorEgress egress, OAuth2RefreshTokenRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(egress);
            ArgumentNullException.ThrowIfNull(request);
            Require(request.TokenUrl, "token-url");
            Require(request.RefreshToken, "refresh-token");

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", request.RefreshToken),
            };
            string? basic = null;
            if (request.ClientAsBasic)
            {
                Require(request.ClientId ?? string.Empty, "client-id");
                Require(request.ClientSecret ?? string.Empty, "client-secret");
                basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ClientId + ":" + request.ClientSecret));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.ClientId))
                {
                    form.Add(new("client_id", request.ClientId));
                }

                if (!string.IsNullOrWhiteSpace(request.ClientSecret))
                {
                    form.Add(new("client_secret", request.ClientSecret));
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Scope))
            {
                form.Add(new("scope", request.Scope));
            }

            return AcquireAsync(egress, request.TokenUrl, form, request.Timeout, cancellationToken, basic, request.UserAgent);
        }

        /// <summary>A valid access token for the account-credentials grant (Zoom's server-to-server apps), the client as HTTP Basic, with the same caching and failure semantics.</summary>
        public Task<string> GetAccessTokenAsync(IConnectorEgress egress, OAuth2AccountCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(egress);
            ArgumentNullException.ThrowIfNull(request);
            Require(request.TokenUrl, "token-url");
            Require(request.AccountId, "account-id");
            Require(request.ClientId, "client-id");
            Require(request.ClientSecret, "client-secret");

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "account_credentials"),
                new("account_id", request.AccountId),
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ClientId + ":" + request.ClientSecret));
            return AcquireAsync(egress, request.TokenUrl, form, request.Timeout, cancellationToken, basic);
        }

        private async Task<string> AcquireAsync(IConnectorEgress egress, string tokenUrl, IReadOnlyList<KeyValuePair<string, string>> form, TimeSpan? timeout, CancellationToken cancellationToken, string? basic = null, string? userAgent = null)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(tokenUrl, out var tokenUri) || (tokenUri.Scheme != Uri.UriSchemeHttp && tokenUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"The token-url '{tokenUrl}' is not an absolute http(s) URL.");
            }

            var key = CacheKey(tokenUri, basic is null ? form : form.Append(new KeyValuePair<string, string>("basic", basic)).ToList());
            if (TryGetFresh(key, out var cached))
            {
                await AdmitAsync(egress, tokenUri, cancellationToken);
                return cached;
            }

            var gate = locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (TryGetFresh(key, out cached))
                {
                    await AdmitAsync(egress, tokenUri, cancellationToken);
                    return cached;
                }

                var token = await FetchAsync(egress, tokenUri, form, timeout, basic, userAgent, cancellationToken);
                cache[key] = token;
                return token.AccessToken;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>A cache hit is served only to an egress that admits the token host: the same check a driver connector makes before it dials, one resolution and no socket.</summary>
        private static async Task AdmitAsync(IConnectorEgress egress, Uri tokenUri, CancellationToken cancellationToken)
        {
            var pinned = await egress.ResolveAndPinAsync(tokenUri.Host, tokenUri.Port, cancellationToken);
            if (pinned is null)
            {
                throw new ConnectorEgressDeniedException($"The egress policy refuses the token endpoint host '{tokenUri.Host}'.");
            }
        }

        private bool TryGetFresh(string key, out string accessToken)
        {
            if (cache.TryGetValue(key, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
            {
                accessToken = entry.AccessToken;
                return true;
            }

            accessToken = string.Empty;
            return false;
        }

        private async Task<CachedToken> FetchAsync(IConnectorEgress egress, Uri tokenUri, IReadOnlyList<KeyValuePair<string, string>> form, TimeSpan? timeout, string? basic, string? userAgent, CancellationToken cancellationToken)
        {
            using var client = egress.CreateHttpClient(timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout);
            using var response = await retry.SendAsync(client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, tokenUri) { Content = new FormUrlEncodedContent(form) };
                if (basic is not null)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
                }

                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                }

                return request;
            }, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Token request to '{tokenUri}' failed with {(int)response.StatusCode} {response.StatusCode}: {Excerpt(payload)}");
            }

            TokenResponse? token;
            try
            {
                token = JsonSerializer.Deserialize<TokenResponse>(payload);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Token response from '{tokenUri}' is not valid JSON: {Excerpt(payload)}", ex);
            }

            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                throw new InvalidOperationException($"Token response from '{tokenUri}' carries no access_token: {Excerpt(payload)}");
            }

            var lifetime = token.ExpiresIn is > 0 ? TimeSpan.FromSeconds(token.ExpiresIn.Value) : DefaultLifetime;
            return new CachedToken(token.AccessToken, timeProvider.GetUtcNow() + lifetime - ExpirySkew);
        }

        /// <summary>A digest of the whole grant: the secret never sits in a dictionary key as text, and two grants differing only in it never share an entry.</summary>
        private static string CacheKey(Uri tokenUri, IReadOnlyList<KeyValuePair<string, string>> form)
        {
            var builder = new StringBuilder(tokenUri.AbsoluteUri);
            foreach (var (name, value) in form)
            {
                builder.Append('\n').Append(name).Append('=').Append(value);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"The OAuth2 token request is missing '{field}' - check the connection's credentials.");
            }
        }

        private static string Excerpt(string payload) => payload.Length <= 500 ? payload : payload[..500];

        private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

        private sealed record TokenResponse(
            [property: JsonPropertyName("access_token")] string? AccessToken,
            [property: JsonPropertyName("token_type")] string? TokenType,
            [property: JsonPropertyName("expires_in")]
            [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            long? ExpiresIn);
    }
}
