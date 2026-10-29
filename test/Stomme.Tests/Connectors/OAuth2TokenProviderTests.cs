#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// These tests cover the token
    /// call rides the EGRESS's client (the provider has no client of its own), the field names are the scheme's, and a cached
    /// token is served only to an egress that admits the token host.
    /// </summary>
    public sealed class OAuth2TokenProviderTests
    {
        // The exact wire shape login.microsoftonline.com returns - snake_case names, numeric expires_in.
        private const string MicrosoftTokenJson = """{"token_type":"Bearer","expires_in":3599,"ext_expires_in":3599,"access_token":"tok-1"}""";

        private sealed class TestClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static OAuth2TokenRequest Request(string scope = "https://graph.microsoft.com/.default") =>
            new("https://login.microsoftonline.com/tenant/oauth2/v2.0/token", "client", "secret", scope);

        private static RecordingHandler Stub(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(responder);

        private static RecordingHandler Stub(string payload, HttpStatusCode status = HttpStatusCode.OK) => new(_ => RecordingHandler.Json(payload, status));

        [Fact]
        public async Task GetAccessToken_BindsSnakeCaseAccessToken()
        {
            // Regression for the legacy MsAzureVm bridge, whose token model never bound access_token (Bearer null).
            var token = await new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(Stub(MicrosoftTokenJson)), Request());

            Assert.Equal("tok-1", token);
        }

        [Fact]
        public async Task GetAccessToken_PostsClientCredentialsForm()
        {
            var stub = Stub(MicrosoftTokenJson);
            var egress = new StubEgress(stub);

            await new OAuth2TokenProvider().GetAccessTokenAsync(egress, Request());

            var (request, form) = Assert.Single(stub.Calls);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://login.microsoftonline.com/tenant/oauth2/v2.0/token", request.RequestUri?.ToString());
            Assert.Contains("grant_type=client_credentials", form);
            Assert.Contains("client_id=client", form);
            Assert.Contains("client_secret=secret", form);
            Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", form);
            Assert.Equal(1, egress.Clients); // the token call rides the egress's client - the provider owns none
        }

        [Fact]
        public async Task GetAccessToken_PostsTheGrantsExtraFields_EachSubjectItsOwnToken()
        {
            // Box's Client Credentials Grant names whom the app acts as in two fields of its own; they ride the form after
            // the grant's, and they are part of the grant: another subject never gets the first one's token.
            var issued = 0;
            var stub = Stub(_ => RecordingHandler.Json($$"""{"token_type":"bearer","expires_in":3600,"access_token":"box-{{++issued}}"}"""));
            var egress = new StubEgress(stub);
            var provider = new OAuth2TokenProvider();
            OAuth2TokenRequest Box(string subject) => new("https://api.box.com/oauth2/token", "app", "secret", string.Empty)
            {
                Extra = new System.Collections.Generic.KeyValuePair<string, string>[] { new("box_subject_type", "enterprise"), new("box_subject_id", subject) },
            };

            Assert.Equal("box-1", await provider.GetAccessTokenAsync(egress, Box("1042")));
            Assert.Equal("box-2", await provider.GetAccessTokenAsync(egress, Box("2042"))); // another subject: its own token
            Assert.Equal("box-1", await provider.GetAccessTokenAsync(egress, Box("1042"))); // the first's, from the cache
            Assert.Equal(2, stub.Calls.Count);
            Assert.Equal("grant_type=client_credentials&client_id=app&client_secret=secret&box_subject_type=enterprise&box_subject_id=1042", stub.Calls[0].Body);
        }

        [Fact]
        public async Task GetAccessToken_ServesCachedTokenUntilExpiry()
        {
            var clock = new TestClock(DateTimeOffset.Parse("2026-07-09T10:00:00Z"));
            var issued = 0;
            var stub = Stub(_ => RecordingHandler.Json($$"""{"token_type":"Bearer","expires_in":3600,"access_token":"tok-{{++issued}}"}"""));
            var egress = new StubEgress(stub);
            var provider = new OAuth2TokenProvider(clock);

            var first = await provider.GetAccessTokenAsync(egress, Request());
            var second = await provider.GetAccessTokenAsync(egress, Request());

            Assert.Equal(("tok-1", "tok-1", 1), (first, second, stub.Calls.Count));

            // Past expiry (minus the 60 s skew) the provider refetches.
            clock.Now = clock.Now.AddSeconds(3600);
            var third = await provider.GetAccessTokenAsync(egress, Request());
            Assert.Equal(("tok-2", 2), (third, stub.Calls.Count));
        }

        [Fact]
        public async Task GetAccessToken_CachesPerScope()
        {
            var stub = Stub(MicrosoftTokenJson);
            var egress = new StubEgress(stub);
            var provider = new OAuth2TokenProvider();

            await provider.GetAccessTokenAsync(egress, Request("https://graph.microsoft.com/.default"));
            await provider.GetAccessTokenAsync(egress, Request("https://management.azure.com/.default"));

            Assert.Equal(2, stub.Calls.Count);
        }

        [Fact]
        public async Task GetAccessToken_OmitsEmptyScopeFromTheForm()
        {
            // Scope-less endpoints (Taegis) reject or ignore a scope parameter - an empty scope is not sent.
            var stub = Stub(MicrosoftTokenJson);

            var token = await new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), Request(scope: " "));

            Assert.Equal("tok-1", token);
            Assert.DoesNotContain("scope=", Assert.Single(stub.Calls).Body);
        }

        [Fact]
        public async Task GetAccessToken_EmptyClientSecret_ThrowsWithoutCalling()
        {
            // Regression for the legacy MsIntune bridge, which logged an empty secret but did not fail validation. CHANGED: the field is the scheme's, client-secret.
            var stub = Stub(MicrosoftTokenJson);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), Request() with { ClientSecret = " " }));

            Assert.Contains("client-secret", ex.Message);
            Assert.Empty(stub.Calls);
        }

        [Fact]
        public async Task GetAccessToken_MissingAccessToken_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(Stub("""{"token_type":"Bearer","expires_in":3599}""")), Request()));

            Assert.Contains("access_token", ex.Message);
        }

        [Fact]
        public async Task GetAccessToken_ErrorStatus_ThrowsWithStatusAndBody()
        {
            var stub = Stub("""{"error":"invalid_client","error_description":"AADSTS7000215"}""", HttpStatusCode.Unauthorized);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), Request()));

            Assert.Contains("401", ex.Message);
            Assert.Contains("AADSTS7000215", ex.Message);
        }

        [Fact]
        public async Task RefreshTokenGrant_SendsOnlyTheRefreshTokenWhenClientFieldsAreEmpty()
        {
            // The Flexera family posts a bare refresh_token grant - no client_id/client_secret.
            var stub = Stub(MicrosoftTokenJson);

            var token = await new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), new OAuth2RefreshTokenRequest("https://login.flexera.com/oidc/token", "rt-1"));

            Assert.Equal("tok-1", token);
            var form = Assert.Single(stub.Calls).Body;
            Assert.Contains("grant_type=refresh_token", form);
            Assert.Contains("refresh_token=rt-1", form);
            Assert.DoesNotContain("client_id=", form);
            Assert.DoesNotContain("client_secret=", form);
        }

        [Fact]
        public async Task RefreshTokenGrant_CarriesClientFieldsWhenConfigured()
        {
            var stub = Stub(MicrosoftTokenJson);

            await new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), new OAuth2RefreshTokenRequest("https://login.example.test/token", "rt-1") { ClientId = "client", ClientSecret = "secret" });

            var form = Assert.Single(stub.Calls).Body;
            Assert.Contains("client_id=client", form);
            Assert.Contains("client_secret=secret", form);
        }

        [Fact]
        public async Task RefreshTokenGrant_CachesIndependentlyOfClientCredentials()
        {
            var stub = Stub(MicrosoftTokenJson);
            var egress = new StubEgress(stub);
            var provider = new OAuth2TokenProvider();

            await provider.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest("https://login.example.test/token", "rt-1"));
            await provider.GetAccessTokenAsync(egress, new OAuth2RefreshTokenRequest("https://login.example.test/token", "rt-1"));

            Assert.Single(stub.Calls);
        }

        [Fact]
        public async Task RefreshTokenGrant_MissingRefreshToken_Throws()
        {
            var stub = Stub(MicrosoftTokenJson);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(stub), new OAuth2RefreshTokenRequest("https://login.example.test/token", " ")));

            Assert.Contains("refresh-token", ex.Message);
            Assert.Empty(stub.Calls);
        }

        [Fact]
        public async Task GetAccessToken_RelativeTokenUrl_Throws()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OAuth2TokenProvider().GetAccessTokenAsync(new StubEgress(Stub(MicrosoftTokenJson)), Request() with { TokenUrl = "login.microsoftonline.com/tenant" }));

            Assert.Contains("absolute http(s)", ex.Message);
        }

        [Fact]
        public async Task A_cached_token_is_served_only_to_an_egress_that_admits_the_token_host()
        {
            // The connector is one singleton for every site; the cache spans sites by credential set. A site whose policy refuses
            // the token host is not served the token another site fetched from it: the hit still asks the egress to pin the host.
            var stub = Stub(MicrosoftTokenJson);
            var provider = new OAuth2TokenProvider();
            var admitting = new StubEgress(stub);
            var refusing = new StubEgress(stub) { Pinned = null };

            Assert.Equal("tok-1", await provider.GetAccessTokenAsync(admitting, Request()));
            await Assert.ThrowsAsync<ConnectorEgressDeniedException>(() => provider.GetAccessTokenAsync(refusing, Request()));
            Assert.Equal("tok-1", await provider.GetAccessTokenAsync(admitting, Request()));

            Assert.Single(stub.Calls); // one fetch: the refusal was decided on the pin, no token call, and the entry survived
            Assert.Contains(("login.microsoftonline.com", 443), refusing.Pins);
        }

        [Fact]
        public async Task A_throttled_token_endpoint_is_retried_per_retry_after()
        {
            var calls = 0;
            var stub = Stub(_ =>
            {
                if (calls++ == 0)
                {
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    throttled.Headers.Add("Retry-After", "1");
                    return throttled;
                }

                return RecordingHandler.Json(MicrosoftTokenJson);
            });
            var provider = new OAuth2TokenProvider(retry: new RetryAfterPolicy { Delay = (_, _) => Task.CompletedTask });

            Assert.Equal("tok-1", await provider.GetAccessTokenAsync(new StubEgress(stub), Request()));
            Assert.Equal(2, stub.Calls.Count);
        }

        // Zoom's server-to-server grant: the account in the form, the client as HTTP Basic and never in the form; the
        // account id required before any call; the cache keyed by the client too, so another secret fetches its own token.
        [Fact]
        public async Task The_account_credentials_grant_sends_the_client_as_basic_and_caches_per_client()
        {
            var issued = 0;
            var stub = Stub(_ => RecordingHandler.Json($$"""{"access_token":"zoom-{{++issued}}","token_type":"bearer","expires_in":3599,"scope":"meeting:read"}"""));
            var egress = new StubEgress(stub);
            var provider = new OAuth2TokenProvider();
            OAuth2AccountCredentialsRequest Zoom(string secret) => new("https://zoom.example.test/oauth/token", "acct-1", "client-1", secret);

            Assert.Equal("zoom-1", await provider.GetAccessTokenAsync(egress, Zoom("secret-1")));
            var (request, form) = Assert.Single(stub.Calls);
            Assert.Equal(("grant_type=account_credentials&account_id=acct-1", "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("client-1:secret-1"))),
                (form, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            Assert.Equal("zoom-1", await provider.GetAccessTokenAsync(egress, Zoom("secret-1"))); // cached
            Assert.Equal("zoom-2", await provider.GetAccessTokenAsync(egress, Zoom("secret-2"))); // another client's secret: its own token
            Assert.Equal(2, stub.Calls.Count);

            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(egress, new OAuth2AccountCredentialsRequest("https://zoom.example.test/oauth/token", " ", "client-1", "secret-1")));
            Assert.Contains("'account-id'", missing.Message, StringComparison.Ordinal);
            Assert.Equal(2, stub.Calls.Count);
        }

        // Reddit's refresh: the client as HTTP Basic and never in the form, the caller named by its user agent; a client
        // asked for as Basic and not given refused before anything is sent; the form and no agent otherwise, as before.
        [Fact]
        public async Task A_refresh_can_send_the_client_as_basic_and_name_its_caller()
        {
            var stub = Stub(_ => RecordingHandler.Json("""{"access_token":"reddit-1","token_type":"bearer","expires_in":86400,"scope":"*"}"""));
            var provider = new OAuth2TokenProvider();
            Assert.Equal("reddit-1", await provider.GetAccessTokenAsync(new StubEgress(stub), new OAuth2RefreshTokenRequest("https://www.reddit.example.test/api/v1/access_token", "refresh-1")
            {
                ClientId = "client-1", ClientSecret = "secret-1", ClientAsBasic = true, UserAgent = "web:fika-bot:1.0 (by /u/ada)",
            }));
            var (request, form) = Assert.Single(stub.Calls);
            Assert.Equal(("grant_type=refresh_token&refresh_token=refresh-1", "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("client-1:secret-1")), "web:fika-bot:1.0 (by /u/ada)"),
                (form, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, string.Join(" ", request.Headers.TryGetValues("User-Agent", out var agents) ? agents : Array.Empty<string>())));

            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(new StubEgress(stub), new OAuth2RefreshTokenRequest("https://www.reddit.example.test/api/v1/access_token", "refresh-2") { ClientId = "client-1", ClientAsBasic = true }));
            Assert.Contains("'client-secret'", missing.Message, StringComparison.Ordinal);
            Assert.Single(stub.Calls); // nothing sent

            var plain = Stub(_ => RecordingHandler.Json("""{"access_token":"google-1","token_type":"Bearer","expires_in":3599}"""));
            await provider.GetAccessTokenAsync(new StubEgress(plain), new OAuth2RefreshTokenRequest("https://oauth2.example.test/token", "refresh-3") { ClientId = "client-1", ClientSecret = "secret-1" });
            var (asBefore, asBeforeForm) = Assert.Single(plain.Calls);
            Assert.Equal(("grant_type=refresh_token&refresh_token=refresh-3&client_id=client-1&client_secret=secret-1", (string?)null, false), (asBeforeForm, asBefore.Headers.Authorization?.Scheme, asBefore.Headers.Contains("User-Agent")));
        }
    }
}
