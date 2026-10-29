using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// THE INTERACTIVE OIDC LOGIN LOOPBACK, DRIVEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stands up the REAL Stomme host (<see cref="Program.BuildAppAsync"/> + the real middleware pipeline,
    /// transport swapped for TestServer), installs one tenant on <c>localhost</c> with the D64 interactive
    /// OpenIddict authority co-hosted, registers a per-tenant LOOPBACK OIDC client (authorization_code + PKCE)
    /// whose redirect_uri is the site's own <c>signin-oidc</c> callback, points the site's
    /// <c>ExternalLogin:*</c> settings at that same-host authority, then DRIVES the full browser dance over
    /// real HTTP against the running host and asserts each hop:
    /// </para>
    /// <list type="number">
    /// <item><b>Discovery + id_token</b> — a raw authorize→token exchange proves the authority issues an
    /// id_token whose <c>iss</c> is the tenant host (host-inferred, no <c>SetIssuer</c>), <c>aud</c> is the
    /// loopback client and <c>sub</c> is the signed-in user; the discovery document advertises the
    /// authorization/token/userinfo/jwks endpoints.</item>
    /// <item><b>Consumer loopback</b> — <c>/pages/external</c> → challenge → <c>/connect/authorize</c> (code
    /// issued for the cookie-authenticated user) → callback <c>signin-oidc</c> → the OIDC handler exchanges the
    /// code, validates the id_token, calls <c>/connect/userinfo</c>, and <c>ValidateUser</c> LINKS the existing
    /// IdentityUser and writes a <c>UserLogin</c> row, setting the Identity cookie. The final landing is the
    /// return url (not <c>/login?status=…</c>), a fresh Identity cookie is issued, and the <c>UserLogin</c> row
    /// exists — the user ends authenticated on the site.</item>
    /// </list>
    /// <para>
    /// PROVIDER DIMENSION. SQLite is the always-on EF case (the primary drive). PostgreSQL runs additionally
    /// when the local service is reachable. RavenDB is yielded UNCONDITIONALLY (D46/D62 discipline: an EF-only
    /// parity run proves nothing, so an unreachable RavenDB FAILS rather than skips) — this drives the OIDC
    /// login loopback through the Raven OpenIddict stores AND the hand-written Raven IUserStore, the first
    /// time the interactive authority meets the document provider end to end.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class OpenIddictInteractiveLoginTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";
        private const string ClientId = "stomme-loopback";
        private const string ClientSecret = "L00pback!Client$2026";
        private const string HostUsername = "hostoidc";
        private const string HostPassword = "R3nder!Guard$2026";
        private const string HostEmail = "hostoidc@example.com";

        private readonly ITestOutputHelper _output;

        public OpenIddictInteractiveLoginTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { "SQLite" };

            if (PostgresReachable())
            {
                yield return new object[] { "PostgreSQL" };
            }

            yield return new object[] { "RavenDB" };
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Site_logs_in_via_its_own_OpenIddict_authority(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new ScratchInstall(provider);

            // The OIDC handler's server-to-server backchannel (discovery/token/userinfo) must route to the
            // in-memory TestServer, not the real network. Injected via an ISiteOptions<OpenIdConnectOptions>,
            // which runs BEFORE the framework post-configure that builds the ConfigurationManager — so the
            // ConfigurationManager captures this handler. The box is filled after the server starts.
            var backchannelBox = new HttpMessageHandler[1];

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    // TestServer has no real connection, so HttpContext.Connection.RemoteIpAddress is null and
                    // ValidateUser NREs on RemoteIpAddress.ToString() (a real Kestrel host reports 127.0.0.1).
                    // Stamp the loopback address at the head of the pipeline — a test-only shim, no production change.
                    builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    // Route the OIDC handler's server-to-server backchannel (discovery/token/userinfo) to the
                    // in-memory TestServer. An ISiteOptions runs BEFORE the framework post-configure that builds
                    // the ConfigurationManager, so the ConfigurationManager captures this handler.
                    builder.Services.AddSingleton<ISiteOptions<OpenIdConnectOptions>>(
                        new SiteOptions<OpenIdConnectOptions>((options, alias, settings) =>
                        {
                            if (backchannelBox[0] != null)
                            {
                                options.Backchannel = new HttpClient(backchannelBox[0]);
                            }
                        }));
                    // Surface the reason if the callback ever fails, so a red run says why instead of "RemoteFailure".
                    builder.Services.PostConfigure<OpenIdConnectOptions>(AuthenticationProviderTypes.OpenIDConnect, o =>
                    {
                        var innerFailure = o.Events.OnRemoteFailure;
                        o.Events.OnRemoteFailure = ctx =>
                        {
                            _output.WriteLine($"[REMOTE FAILURE] {ctx.Failure}");
                            return innerFailure(ctx);
                        };
                    });
                });

            try
            {
                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();

                await InstallAsync(databaseManager, new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantConnectionString,
                    Aliases = Host,
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = HostUsername,
                    HostPassword = HostPassword,
                    HostEmail = HostEmail,
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }, "authority tenant");

                await app.StartAsync();
                backchannelBox[0] = app.GetTestServer().CreateHandler();

                int siteId = await ConfigureLoopbackAsync(app);

                // A RAW handler to the in-memory server: no auto-redirect and no built-in cookie container, so
                // every hop is asserted explicitly and the cookie jar is the one this test controls.
                var server = app.GetTestServer();
                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };

                // ---- Pre-authenticate the authority session (the Stomme Identity cookie) ---------------
                await LoginAsync(app, client, cookies);
                var allCookies = cookies.GetCookies(new Uri("http://" + Host)).Cast<Cookie>().ToList();
                _output.WriteLine($"[{providerName}] cookies after login: {string.Join(", ", allCookies.Select(c => c.Name))}");
                Assert.True(
                    allCookies.Any(c => c.Name.Contains("Identity.Application")),
                    $"[{providerName}] Identity cookie was not issued by /pages/login — pre-authentication failed.");

                // ===== PART A: raw authorize -> token, capture the id_token ================================
                var idToken = await RawAuthorizeAndExchangeAsync(client, cookies);
                var iss = ReadJwtClaim(idToken, "iss");
                var aud = ReadJwtClaim(idToken, "aud");
                var sub = ReadJwtClaim(idToken, "sub");
                _output.WriteLine($"[{providerName}] id_token iss = {iss}");
                _output.WriteLine($"[{providerName}] id_token aud = {aud}");
                _output.WriteLine($"[{providerName}] id_token sub = {sub}");
                Assert.Contains(Host, iss);           // host-inferred issuer, no SetIssuer
                Assert.Equal(ClientId, aud);          // audience = the loopback client
                Assert.False(string.IsNullOrEmpty(sub));

                // ---- Discovery advertises the interactive endpoints -------------------------------------
                var discovery = await GetJsonAsync(client, cookies, "/.well-known/openid-configuration");
                _output.WriteLine($"[{providerName}] discovery issuer = {discovery?["issuer"]}");
                Assert.Contains(Host, discovery?["issuer"]?.ToString() ?? "");
                Assert.Contains("/connect/authorize", discovery?["authorization_endpoint"]?.ToString() ?? "");
                Assert.Contains("/connect/token", discovery?["token_endpoint"]?.ToString() ?? "");
                Assert.Contains("/connect/userinfo", discovery?["userinfo_endpoint"]?.ToString() ?? "");
                Assert.False(string.IsNullOrEmpty(discovery?["jwks_uri"]?.ToString()));

                // ===== PART A2: the access token alone signs the user in to api/* =================
                // A client holding NO cookie: anonymous without a token, the user with the authority's token, and
                // anonymous again with the token's signature broken - each answered by the API, not the authority.
                using (var cookieless = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) })
                {
                    var anonymous = await AuthenticateAsync(cookieless, null);
                    Assert.False(anonymous?["isAuthenticated"]?.GetValue<bool>());

                    var signedIn = await AuthenticateAsync(cookieless, _lastAccessToken);
                    _output.WriteLine($"[{providerName}] api/User/authenticate with the access token: {signedIn?.ToJsonString()}");
                    Assert.True(signedIn?["isAuthenticated"]?.GetValue<bool>());
                    Assert.Equal(HostUsername, signedIn?["username"]?.GetValue<string>());
                    Assert.Contains(RoleNames.Host, signedIn?["roles"]?.GetValue<string>() ?? "");

                    var tampered = _lastAccessToken[..^4] + (_lastAccessToken.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
                    var refused = await AuthenticateAsync(cookieless, tampered);
                    Assert.False(refused?["isAuthenticated"]?.GetValue<bool>());

                    // a valid token of the same person that was not granted the api scope is not meant for the API
                    var signedInToken = _lastAccessToken;
                    await RawAuthorizeAndExchangeAsync(client, cookies, "openid profile email");
                    Assert.NotEqual(signedInToken, _lastAccessToken);
                    var unscoped = await AuthenticateAsync(cookieless, _lastAccessToken);
                    Assert.False(unscoped?["isAuthenticated"]?.GetValue<bool>());
                }

                // ===== PART B: the FULL consumer loopback through /pages/external =========================
                // Hop 1: /pages/external -> 302 to /connect/authorize
                var hop1 = await SendAsync(client, cookies, HttpMethod.Get, "/pages/external?returnurl=%2F");
                _output.WriteLine($"[{providerName}][hop1] /pages/external -> {(int)hop1.StatusCode} {hop1.Headers.Location}");
                Assert.Equal(HttpStatusCode.Redirect, hop1.StatusCode);
                Assert.Contains("/connect/authorize", hop1.Headers.Location!.ToString());

                // Hop 2: /connect/authorize -> 302 to /signin-oidc?code=...
                var hop2 = await SendAsync(client, cookies, HttpMethod.Get, hop1.Headers.Location!.ToString());
                _output.WriteLine($"[{providerName}][hop2] /connect/authorize -> {(int)hop2.StatusCode} {hop2.Headers.Location}");
                Assert.Equal(HttpStatusCode.Redirect, hop2.StatusCode);
                Assert.Contains("/signin-" + AuthenticationProviderTypes.OpenIDConnect, hop2.Headers.Location!.ToString());
                Assert.Contains("code=", hop2.Headers.Location!.ToString());

                // Hop 3: /signin-oidc?code -> the OIDC handler exchanges code (backchannel), validates the
                // id_token, calls userinfo, link-or-creates via ValidateUser, sets the Identity cookie, and
                // redirects to the return url. A failure would land on /login?status=... instead.
                var hop3 = await SendAsync(client, cookies, HttpMethod.Get, hop2.Headers.Location!.ToString());
                var hop3SetCookies = hop3.Headers.TryGetValues("Set-Cookie", out var sc3) ? sc3.ToList() : new List<string>();
                _output.WriteLine($"[{providerName}][hop3] signin-oidc -> {(int)hop3.StatusCode} {hop3.Headers.Location}");
                Assert.Equal(HttpStatusCode.Redirect, hop3.StatusCode);
                var landing = hop3.Headers.Location!.ToString();
                // Success lands on the return url ("/?reload=post" — the External page appends reload=post); a
                // failure would land on "/login?status=<failure>".
                Assert.DoesNotContain("/login", landing);
                Assert.DoesNotContain("status=", landing);
                Assert.StartsWith("/", landing);

                // the callback re-issues the Identity cookie => the user ends authenticated
                Assert.Contains(hop3SetCookies, c => c.Contains("Identity.Application="));

                // ---- The definitive proof: the UserLogin row was written + LastLoginOn stamped ----------
                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var aliasRepository = sp.GetRequiredService<IAliasRepository>();
                    var alias = await aliasRepository.GetAliasAsync(Host);
                    var tenantManager = sp.GetRequiredService<ITenantManager>();
                    await tenantManager.SetAliasAsync(alias);

                    var identityUsers = sp.GetRequiredService<UserManager<IdentityUser>>();
                    var loginProvider = AuthenticationProviderTypes.OpenIDConnect + ":" + siteId;
                    var linked = await identityUsers.FindByLoginAsync(loginProvider, sub);
                    Assert.NotNull(linked); // UserLogin(provider="oidc:{siteId}", key=sub) exists
                    Assert.Equal(HostUsername, linked!.UserName);

                    var users = sp.GetRequiredService<IUserRepository>();
                    var user = await users.GetUserAsync(HostUsername);
                    Assert.NotNull(user!.LastLoginOn); // set only on ValidateUser's success branch
                    _output.WriteLine($"[{providerName}] linked user {linked.UserName} LastLoginOn={user.LastLoginOn:o}");
                }

                _output.WriteLine($"[{providerName}] INTERACTIVE OIDC LOOPBACK DRIVEN: login succeeded end to end.");

                // ===== PART C: a locked-out account signs in by no route ===========================
                // The same linked account, now locked out (a service account is locked out for good): the external
                // sign-in that just succeeded is refused, landing on the login page with AccessDenied.
                using (var scope = app.Services.CreateScope())
                {
                    var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                    await tenantManager.SetAliasAsync(await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host));
                    var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    var account = await identityUsers.FindByNameAsync(HostUsername);
                    Assert.True((await identityUsers.SetLockoutEnabledAsync(account!, true)).Succeeded);
                    Assert.True((await identityUsers.SetLockoutEndDateAsync(account!, DateTimeOffset.MaxValue)).Succeeded);
                }

                var lockedHop1 = await SendAsync(client, cookies, HttpMethod.Get, "/pages/external?returnurl=%2F");
                var lockedHop2 = await SendAsync(client, cookies, HttpMethod.Get, lockedHop1.Headers.Location!.ToString());
                var lockedHop3 = await SendAsync(client, cookies, HttpMethod.Get, lockedHop2.Headers.Location!.ToString());
                var lockedLanding = lockedHop3.Headers.Location?.ToString() ?? "";
                _output.WriteLine($"[{providerName}][locked] signin-oidc -> {(int)lockedHop3.StatusCode} {lockedLanding}");
                Assert.Contains("/login", lockedLanding);
                Assert.Contains("status=" + ExternalLoginStatus.AccessDenied, lockedLanding);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// Registers the loopback OIDC client in the tenant OpenIddict store and writes the site's
        /// <c>ExternalLogin:*</c> settings pointing at this same host. Returns the site id.
        /// </summary>
        private static async Task<int> ConfigureLoopbackAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var aliasRepository = sp.GetRequiredService<IAliasRepository>();
            var alias = await aliasRepository.GetAliasAsync(Host)
                ?? throw new InvalidOperationException($"No alias resolved for host '{Host}'.");

            var tenantManager = sp.GetRequiredService<ITenantManager>();
            await tenantManager.SetAliasAsync(alias);

            // Relational: create the per-tenant OpenIddict schema (EnsureCreated). Raven: the context is not
            // registered — the DB is created lazily by the session provider on the first store write.
            var context = sp.GetService<TenantOpenIddictDbContext>();
            if (context != null)
            {
                await context.Database.EnsureCreatedAsync();
            }

            var redirectUri = $"http://{Host}/signin-{AuthenticationProviderTypes.OpenIDConnect}";
            await StommeOpenIddictExtensions.RegisterLoopbackClientAsync(
                sp, ClientId, ClientSecret, redirectUri, "api");

            var settings = sp.GetRequiredService<ISettingRepository>();
            var externalLogin = new Dictionary<string, string>
            {
                ["ExternalLogin:ProviderType"] = AuthenticationProviderTypes.OpenIDConnect,
                ["ExternalLogin:ProviderName"] = "Stomme Loopback",
                ["ExternalLogin:Authority"] = $"http://{Host}",
                ["ExternalLogin:ClientId"] = ClientId,
                ["ExternalLogin:ClientSecret"] = ClientSecret,
                ["ExternalLogin:AuthResponseType"] = "code",
                ["ExternalLogin:PKCE"] = "true",
                ["ExternalLogin:Scopes"] = "openid,profile,email",
                ["ExternalLogin:IdentifierClaimType"] = "sub",
                ["ExternalLogin:NameClaimType"] = "name",
                ["ExternalLogin:EmailClaimType"] = "email",
                // loopback / headless drive knobs (all default-preserving in production)
                ["ExternalLogin:RequireHttpsMetadata"] = "false",
                ["ExternalLogin:MapInboundClaims"] = "false",
                ["ExternalLogin:ResponseMode"] = "query",
                // auto-link the existing host user (rather than email-verify) so a UserLogin row is written
                ["ExternalLogin:VerifyUsers"] = "false",
                ["ExternalLogin:CreateUsers"] = "true"
            };
            foreach (var kv in externalLogin)
            {
                await settings.AddSettingAsync(new Setting
                {
                    EntityName = EntityNames.Site,
                    EntityId = alias.SiteId,
                    SettingName = kv.Key,
                    SettingValue = kv.Value,
                    IsPrivate = true
                });
            }

            return alias.SiteId;
        }

        /// <summary>
        /// Pre-authenticates the authority session by POSTing to <c>/pages/login</c>. Antiforgery is satisfied
        /// by generating a matched cookie/request-token pair through the app's shared DataProtection ring and
        /// sending the cookie on this one request; the resulting Identity cookie is captured for the drive.
        /// </summary>
        private async Task LoginAsync(WebApplication app, HttpClient client, CookieContainer cookies)
        {
            string requestToken;
            string antiforgeryCookie;
            using (var scope = app.Services.CreateScope())
            {
                var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
                var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                var tokens = antiforgery.GetAndStoreTokens(httpContext);
                requestToken = tokens.RequestToken!;
                var setCookie = httpContext.Response.Headers.SetCookie.ToString();
                antiforgeryCookie = setCookie.Split(';')[0]; // name=value
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = HostUsername,
                    ["password"] = HostPassword,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(request);
            var setCookieNames = response.Headers.TryGetValues("Set-Cookie", out var scs)
                ? string.Join(", ", scs.Select(s => s.Split('=')[0]))
                : "(none)";
            _output.WriteLine($"[login] POST /pages/login -> {(int)response.StatusCode}; Set-Cookie: {setCookieNames}");
            CaptureCookies(cookies, new Uri("http://" + Host), response);
            // 302 LocalRedirect on success (or on failure — the cookie presence is asserted by the caller).
        }

        /// <summary>
        /// A raw authorization_code + PKCE exchange: GET /connect/authorize (cookie-authenticated) to obtain a
        /// code, then POST /connect/token to redeem it. Returns the id_token so the caller can inspect its iss.
        /// </summary>
        private async Task<string> RawAuthorizeAndExchangeAsync(HttpClient client, CookieContainer cookies, string scope = "openid profile email api")
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var redirectUri = $"http://{Host}/signin-{AuthenticationProviderTypes.OpenIDConnect}";

            var authorizeUrl = "/connect/authorize?" + string.Join('&', new[]
            {
                "client_id=" + Uri.EscapeDataString(ClientId),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "response_type=code",
                "response_mode=query",
                "scope=" + Uri.EscapeDataString(scope),
                "code_challenge=" + Uri.EscapeDataString(challenge),
                "code_challenge_method=S256",
                "state=raw-state",
                "nonce=raw-nonce"
            });

            var authorizeResponse = await SendAsync(client, cookies, HttpMethod.Get, authorizeUrl);
            if (authorizeResponse.StatusCode != HttpStatusCode.Redirect)
            {
                var diag = await authorizeResponse.Content.ReadAsStringAsync();
                var headers = string.Join(" | ", authorizeResponse.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
                _output.WriteLine($"[authorize] status={(int)authorizeResponse.StatusCode} location={authorizeResponse.Headers.Location}");
                _output.WriteLine($"[authorize] headers={headers}");
                _output.WriteLine($"[authorize] body={diag.Substring(0, Math.Min(800, diag.Length))}");
            }
            Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
            var location = authorizeResponse.Headers.Location!.ToString();
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(location).Query);
            var code = query.TryGetValue("code", out var codeValues) ? codeValues.ToString() : null;
            Assert.False(string.IsNullOrEmpty(code), $"authorize did not return a code: {location}");

            var tokenResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code!,
                    ["redirect_uri"] = redirectUri,
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["code_verifier"] = verifier
                })
            });

            var body = await tokenResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
            var json = JsonNode.Parse(body);
            var idToken = json?["id_token"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(idToken), $"token response carried no id_token: {body}");
            _lastAccessToken = json?["access_token"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(_lastAccessToken), $"token response carried no access_token: {body}");
            return idToken!;
        }

        private string _lastAccessToken;

        /// <summary>BL-196: <c>GET api/User/authenticate</c> from a client with NO cookie, carrying only <paramref name="bearer"/> (or nothing).</summary>
        private static async Task<JsonNode> AuthenticateAsync(HttpClient cookieless, string bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/User/authenticate");
            if (bearer != null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            }

            var response = await cookieless.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }

        // ---------- HTTP + cookie plumbing (explicit, no auto-redirect, so each hop is asserted) -----------

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CookieContainer cookies, HttpMethod method, string url)
        {
            var uri = new Uri(new Uri("http://" + Host), url);
            var request = new HttpRequestMessage(method, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }
            var response = await client.SendAsync(request);
            CaptureCookies(cookies, uri, response);
            return response;
        }

        private static async Task<JsonNode> GetJsonAsync(HttpClient client, CookieContainer cookies, string url)
        {
            var response = await SendAsync(client, cookies, HttpMethod.Get, url);
            var body = await response.Content.ReadAsStringAsync();
            return JsonNode.Parse(body)!;
        }

        private static void CaptureCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return;
            }
            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';')[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var name = pair.Substring(0, eq).Trim();
                var value = pair.Substring(eq + 1).Trim();
                try
                {
                    cookies.Add(new Cookie(name, value, "/", uri.Host));
                }
                catch
                {
                    // ignore malformed/deletion cookies
                }
            }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string? ReadJwtClaim(string jwt, string claim)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonNode.Parse(json)?[claim]?.ToString();
        }

        private async Task InstallAsync(IDatabaseManager databaseManager, InstallConfig config, string label)
        {
            var installTask = Task.Run(() => databaseManager.InstallAsync(config));
            if (await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) != installTask)
            {
                throw new TimeoutException($"Install of {label} did not finish within {InstallTimeout.TotalMinutes:0} minute(s).");
            }
            var install = await installTask;
            _output.WriteLine($"[install {label}] Success={install.Success} Message={install.Message}");
            Assert.True(install.Success, $"Install of {label} failed: {install.Message}");
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        private static bool PostgresReachable() => PostgresTestEndpoint.IsReachable();

        private static IProvider ResolveProvider(string name) => name switch
        {
            "SQLite" => new SqliteProvider(),
            "PostgreSQL" => new PostgresProvider(),
            "RavenDB" => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        public interface IProvider : IDisposable
        {
            string DatabaseType { get; }
            string TenantConnectionString { get; }
            string OpenIddictConnectionString { get; }
            string? DatabaseTypePreseed { get; }
            void BindToScratch(string scratchDirectory);
        }

        private sealed class SqliteProvider : IProvider
        {
            private string _tenant = null!, _oidc = null!;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantConnectionString => _tenant;
            public string OpenIddictConnectionString => _oidc;
            public string? DatabaseTypePreseed => null;

            public void BindToScratch(string scratch)
            {
                _tenant = $"Data Source={Path.Combine(scratch, "tenant.db")};";
                _oidc = $"Data Source={Path.Combine(scratch, "oidc.db")};";
            }

            public void Dispose()
            {
                // Exactly the two databases this provider handed out - never the process-wide form.
                ScratchSqlite.Release(_tenant);
                ScratchSqlite.Release(_oidc);
            }
        }

        private sealed class PostgresProvider : IProvider
        {
            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();

            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string TenantConnectionString => Build($"stomme_oidclogin_t_{_suffix}");
            public string OpenIddictConnectionString => Build($"stomme_oidclogin_o_{_suffix}");
            public string? DatabaseTypePreseed => null;

            public void BindToScratch(string scratch) { }

            private string Build(string database)
            {
                if (!_databases.Contains(database))
                {
                    _databases.Add(database);
                }
                return PostgresTestEndpoint.ConnectionString(database);
            }

            public void Dispose()
            {
                foreach (var database in _databases)
                {
                    PostgresTestEndpoint.DropDatabase(database);
                }
            }
        }

        private sealed class RavenProvider : IProvider
        {
            private static string BaseUrl =>
                Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();

            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantConnectionString => Build($"stomme_oidclogin_t_{_suffix}");
            public string OpenIddictConnectionString => Build($"stomme_oidclogin_o_{_suffix}");
            public string DatabaseTypePreseed => DatabaseType;

            public void BindToScratch(string scratch) { }

            private string Build(string database)
            {
                if (!_databases.Contains(database))
                {
                    _databases.Add(database);
                }
                return $"Url={BaseUrl};Database={database}";
            }

            public void Dispose()
            {
                try
                {
                    using var store = new DocumentStore { Urls = new[] { BaseUrl } };
                    store.Initialize();
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(
                        new DeleteDatabasesOperation.Parameters
                        {
                            DatabaseNames = _databases.ToArray(),
                            HardDelete = true
                        }));
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private sealed class LoopbackRemoteIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, continuation) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                    return continuation();
                });
                next(app);
            };
        }

        /// <summary>A throwaway content root pre-seeded with OpenIddict:Enabled + the per-tenant OpenIddict connection string.</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall(IProvider provider)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-oidclogin-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["OpenIddict"] = new JsonObject { ["Enabled"] = true },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection_OpenIddict"] = provider.OpenIddictConnectionString
                    }
                };
                if (provider.DatabaseTypePreseed != null)
                {
                    overrides["Database"] = new JsonObject { ["DefaultDBType"] = provider.DatabaseTypePreseed };
                }

                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
