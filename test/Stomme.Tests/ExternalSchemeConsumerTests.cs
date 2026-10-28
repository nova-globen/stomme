using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D65 — the SAML2 + Windows/Negotiate external-login CONSUMERS, DRIVEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stands up the REAL Stomme host (transport swapped for TestServer), installs one tenant on
    /// <c>localhost</c> with <c>Saml2:Enabled</c> AND <c>WindowsAuth:Enabled</c> both true and a file-based
    /// test-IdP metadata, then:
    /// </para>
    /// <list type="number">
    /// <item><b>SAML end to end</b> — a self-hosted test IdP mints a SIGNED SAML assertion, POSTed to the real
    /// Sustainsys ACS endpoint. Sustainsys validates the signature against the IdP metadata, signs the raw
    /// principal into the external cookie, and returns to <c>/signin-saml2</c>, where the D65 provisioning bridge
    /// routes the principal through <c>ValidateUser</c> — creating-or-linking the IdentityUser + writing a
    /// <c>UserLogin</c> row (provider <c>saml2:{siteId}</c>) and setting the Identity cookie. Asserted on the real
    /// DB across every reachable provider.</item>
    /// <item><b>Scheme table + render safety (the biggest risk)</b> — with BOTH schemes registered,
    /// <c>DefaultScheme</c> is STILL the Identity cookie, the "Saml2"/"Negotiate" schemes do not clash with
    /// "oidc"/"oauth2"/the OpenIddict validation scheme, a normal <c>GET /</c> RENDERS 200 with NO global
    /// <c>WWW-Authenticate: Negotiate</c> (Negotiate is not a default challenge), yet the explicit
    /// <c>/pages/windows-login</c> DOES return 401 <c>WWW-Authenticate: Negotiate</c> (the local Negotiate smoke;
    /// real Kerberos defers to a domain-joined host).</item>
    /// </list>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class ExternalSchemeConsumerTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string Host = "localhost";
        private const string HostUsername = "hostsaml";
        private const string HostPassword = "R3nder!Guard$2026";
        private const string HostEmail = "hostsaml@example.com";

        private const string IdpEntityId = "https://stomme-test-idp.example/metadata";
        private const string SpEntityId = "https://stomme/saml";
        private const string SamlUserEmail = "saml.user@example.com";
        private const string SamlUserName = "SAML Test User";

        private readonly ITestOutputHelper _output;

        public ExternalSchemeConsumerTests(ITestOutputHelper output)
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
        public Task Saml_assertion_provisions_user_and_sets_cookie(string providerName)
            => RunSamlProvisioningDriveAsync(providerName, patchRemoteIp: true);

        /// <summary>
        /// D70 item 2 — <c>ValidateUser</c> null-guards <c>RemoteIpAddress</c>. A reverse proxy can strip the
        /// connection IP, leaving <c>HttpContext.Connection.RemoteIpAddress</c> null; the unguarded
        /// <c>.ToString()</c> at the external-login success path then NRE'd and failed the whole login. This
        /// drives the SAME real SAML provisioning flow but WITHOUT the loopback-IP startup filter (so the IP
        /// stays null, TestServer's default) and asserts the login still COMPLETES — the UserLogin row is
        /// written, the Identity cookie is set, and the persisted IP is the empty placeholder rather than a
        /// thrown exception.
        /// </summary>
        [Fact]
        public Task Saml_provisioning_completes_when_remote_ip_is_null()
            => RunSamlProvisioningDriveAsync("SQLite", patchRemoteIp: false);

        private async Task RunSamlProvisioningDriveAsync(string providerName, bool patchRemoteIp)
        {
            using var provider = ResolveProvider(providerName);
            using var cert = SamlTestIdp.CreateSigningCertificate();
            // Saml2 only: Negotiate's request handler throws on TestServer (needs Kestrel), so enabling it here
            // would break every request. The Negotiate smoke runs on a real Kestrel host in its own test.
            using var scratch = new ScratchInstall(provider, cert, IdpEntityId, enableSaml: true, enableWindows: false);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    // D70: patchRemoteIp=false leaves RemoteIpAddress null (TestServer's default), driving the
                    // null-IP path ValidateUser now guards. The existing per-provider drives keep the loopback IP.
                    if (patchRemoteIp)
                    {
                        builder.Services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    }
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
                }, "saml tenant");

                await app.StartAsync();

                int siteId;
                using (var scope = app.Services.CreateScope())
                {
                    var aliasRepository = scope.ServiceProvider.GetRequiredService<IAliasRepository>();
                    var alias = await aliasRepository.GetAliasAsync(Host);
                    siteId = alias.SiteId;
                }

                var server = app.GetTestServer();
                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };

                var acsUrl = $"http://{Host}/Saml2/Acs";

                // ---- Drive the SOLICITED flow: challenge first, then respond with InResponseTo ------------
                // GET /pages/saml-login -> 303 to the IdP with a SAMLRequest + Sustainsys's correlation cookie.
                var challenge = await SendAsync(client, cookies, HttpMethod.Get, "/pages/saml-login?returnurl=%2F");
                _output.WriteLine($"[{providerName}] challenge -> {(int)challenge.StatusCode} {challenge.Headers.Location}");
                Assert.True(
                    challenge.StatusCode == HttpStatusCode.Redirect || challenge.StatusCode == HttpStatusCode.Found
                        || (int)challenge.StatusCode == 303,
                    $"[{providerName}] SAML challenge did not redirect to the IdP (status {(int)challenge.StatusCode}).");
                var challengeQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
                var authnRequestId = SamlTestIdp.ReadAuthnRequestId(challengeQuery["SAMLRequest"]);
                var relayState = challengeQuery["RelayState"].ToString();
                _output.WriteLine($"[{providerName}] AuthnRequest ID={authnRequestId} RelayState={relayState}");
                Assert.False(string.IsNullOrEmpty(authnRequestId), "could not parse the AuthnRequest ID from the challenge.");

                // ---- Mint a SIGNED SAML Response (InResponseTo the challenge) and POST it to the real ACS -
                var samlResponse = SamlTestIdp.BuildSignedResponse(
                    cert, IdpEntityId, SpEntityId, acsUrl, SamlUserEmail, SamlUserName, authnRequestId);
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(samlResponse));

                var acsUri = new Uri(acsUrl);
                var acsRequest = new HttpRequestMessage(HttpMethod.Post, acsUri)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["SAMLResponse"] = encoded,
                        ["RelayState"] = relayState
                    })
                };
                var correlationHeader = cookies.GetCookieHeader(acsUri);
                if (!string.IsNullOrEmpty(correlationHeader))
                {
                    acsRequest.Headers.TryAddWithoutValidation("Cookie", correlationHeader);
                }
                var acsResponse = await client.SendAsync(acsRequest);
                CaptureCookies(cookies, acsUri, acsResponse);
                _output.WriteLine($"[{providerName}] ACS -> {(int)acsResponse.StatusCode} {acsResponse.Headers.Location}");
                if ((int)acsResponse.StatusCode != 302 && (int)acsResponse.StatusCode != 303 && acsResponse.StatusCode != HttpStatusCode.Redirect)
                {
                    var body = await acsResponse.Content.ReadAsStringAsync();
                    _output.WriteLine($"[{providerName}] ACS body: {body.Substring(0, Math.Min(1500, body.Length))}");
                }
                Assert.True(
                    (int)acsResponse.StatusCode == 302 || (int)acsResponse.StatusCode == 303,
                    $"[{providerName}] ACS did not accept the signed assertion (status {(int)acsResponse.StatusCode}).");
                var externalCookieSet = acsResponse.Headers.TryGetValues("Set-Cookie", out var acsCookies)
                    && acsCookies.Any(c => c.Contains(".Stomme.Saml2.External"));
                Assert.True(externalCookieSet, $"[{providerName}] ACS did not set the SAML external cookie.");

                // ---- Follow the redirect to the /signin-saml2 provisioning callback ---------------------
                var callbackUrl = acsResponse.Headers.Location!.ToString();
                Assert.Contains("/signin-saml2", callbackUrl);
                var callback = await SendAsync(client, cookies, HttpMethod.Get, callbackUrl);
                var callbackSetCookies = callback.Headers.TryGetValues("Set-Cookie", out var cbCookies) ? cbCookies.ToList() : new List<string>();
                _output.WriteLine($"[{providerName}] callback -> {(int)callback.StatusCode} {callback.Headers.Location}");

                var landing = callback.Headers.Location?.ToString() ?? "";
                Assert.True(
                    callback.StatusCode == HttpStatusCode.Redirect || callback.StatusCode == HttpStatusCode.Found,
                    $"[{providerName}] callback did not redirect (status {(int)callback.StatusCode}, location '{landing}').");
                Assert.DoesNotContain("/login", landing);
                Assert.DoesNotContain("status=", landing);
                Assert.Contains(callbackSetCookies, c => c.Contains("Identity.Application="));

                // ---- The definitive proof: the UserLogin row was written --------------------------------
                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var aliasRepository = sp.GetRequiredService<IAliasRepository>();
                    var alias = await aliasRepository.GetAliasAsync(Host);
                    var tenantManager = sp.GetRequiredService<ITenantManager>();
                    await tenantManager.SetAliasAsync(alias);

                    var identityUsers = sp.GetRequiredService<UserManager<IdentityUser>>();
                    var loginProvider = AuthenticationProviderTypes.Saml2 + ":" + siteId;
                    var linked = await identityUsers.FindByLoginAsync(loginProvider, SamlUserEmail);
                    Assert.NotNull(linked); // UserLogin(provider="saml2:{siteId}", key=nameid) exists

                    var users = sp.GetRequiredService<IUserRepository>();
                    var user = await users.GetUserAsync(linked!.UserName);
                    Assert.NotNull(user);
                    Assert.NotNull(user!.LastLoginOn); // set only on ValidateUser's success branch
                    _output.WriteLine($"[{providerName}] SAML-provisioned user {linked.UserName} LastLoginOn={user.LastLoginOn:o} LastIPAddress='{user.LastIPAddress}'");

                    if (!patchRemoteIp)
                    {
                        // D70: with no connection IP the guarded ValidateUser persists the empty placeholder
                        // instead of NRE-ing. Reaching this assertion at all proves the login did not throw.
                        Assert.Equal("", user.LastIPAddress ?? "");
                    }
                }

                _output.WriteLine($"[{providerName}] SAML CONSUMER DRIVEN: signed assertion -> ValidateUser -> UserLogin + cookie.");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Saml_scheme_is_gated_default_unchanged_and_render_safe()
        {
            using var provider = new SqliteProvider();
            using var cert = SamlTestIdp.CreateSigningCertificate();
            using var scratch = new ScratchInstall(provider, cert, IdpEntityId, enableSaml: true, enableWindows: false);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
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
                }, "saml scheme-table tenant");

                await app.StartAsync();

                // ---- Scheme table: SAML2 registered, DEFAULT unchanged, no clash with oidc/oauth2 --------
                var schemeProvider = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
                var authOptions = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.Equal(Constants.AuthenticationScheme, authOptions.DefaultScheme); // biggest risk: unchanged

                var all = (await schemeProvider.GetAllSchemesAsync()).Select(s => s.Name).ToList();
                _output.WriteLine("[schemes] " + string.Join(", ", all));
                Assert.Contains(StommeExternalSchemesExtensions.Saml2Scheme, all);        // "Saml2"
                Assert.Contains(StommeExternalSchemesExtensions.Saml2ExternalScheme, all);
                Assert.Contains(AuthenticationProviderTypes.OpenIDConnect, all);            // "oidc" still there
                Assert.Contains(AuthenticationProviderTypes.OAuth2, all);                   // "oauth2" still there
                Assert.Contains(Constants.AuthenticationScheme, all);
                Assert.Equal(all.Count, all.Distinct().Count());                            // no scheme name collides

                var server = app.GetTestServer();
                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };

                // ---- Render safety: a normal GET / still serves 200 with SAML2 registered ----------------
                var home = await SendAsync(client, cookies, HttpMethod.Get, "/");
                _output.WriteLine($"[render] GET / -> {(int)home.StatusCode}");
                Assert.Equal(HttpStatusCode.OK, home.StatusCode);

                // ---- SAML challenge really builds an AuthnRequest to the IdP (302 with SAMLRequest) ------
                var samlChallenge = await SendAsync(client, cookies, HttpMethod.Get, "/pages/saml-login?returnurl=%2F");
                _output.WriteLine($"[saml] GET /pages/saml-login -> {(int)samlChallenge.StatusCode} {samlChallenge.Headers.Location}");
                Assert.True(
                    (int)samlChallenge.StatusCode == 302 || (int)samlChallenge.StatusCode == 303, // Sustainsys uses SeeOther (303)
                    $"SAML challenge did not redirect to the IdP (status {(int)samlChallenge.StatusCode}).");
                Assert.Contains("SAMLRequest", samlChallenge.Headers.Location!.ToString());

                _output.WriteLine("[saml scheme table] DEFAULT unchanged; SAML2 gated-registered; render safe; challenge builds AuthnRequest.");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Login_scheme_flags_are_surfaced_to_the_client_only_when_enabled()
        {
            // D70 item 3 — DRIVE the channel that gates the login buttons. The client login UI cannot read the
            // installation-wide Saml2/WindowsAuth appsettings directly; ServerSiteService surfaces them into the
            // site settings that reach PageState.Site.Settings (the SAME channel ExternalLogin:* already uses).
            // GET /api/Site/{id} runs that exact service over HTTP, so this asserts the flag the login page's
            // `@if (_allowsamllogin)` gates on is delivered as "true" only when enabled — driven end to end.
            //
            // Honest limit (same as D64): the literal interactive button render is NOT asserted here — the login
            // module renders as an interactive Blazor island that a plain HTTP GET does not prerender, so proving
            // the pixel needs a browser circuit this harness does not host. The button itself is a trivial
            // per-flag `@if` over this surfaced setting; the challenge endpoints it targets are driven above
            // (SAML end-to-end) and on Kestrel (Windows 401). Windows is left OFF here (Negotiate throws on
            // TestServer); its flag travels the identical path.
            var enabled = await GetSiteSettingsOverHttpAsync(enableSaml: true);
            _output.WriteLine($"[enabled] Saml2:Enabled={enabled.GetValueOrDefault("Saml2:Enabled")} " +
                $"Saml2:ProviderName={enabled.GetValueOrDefault("Saml2:ProviderName")} " +
                $"WindowsAuth:Enabled={enabled.GetValueOrDefault("WindowsAuth:Enabled")}");
            Assert.Equal("true", enabled.GetValueOrDefault("Saml2:Enabled"));
            Assert.Equal("SAML", enabled.GetValueOrDefault("Saml2:ProviderName"));
            // WindowsAuth is OFF in this boot but the flag is still surfaced (false), so the client can gate on it.
            Assert.Equal("false", enabled.GetValueOrDefault("WindowsAuth:Enabled"));
            Assert.Equal("Windows", enabled.GetValueOrDefault("WindowsAuth:ProviderName"));

            var disabled = await GetSiteSettingsOverHttpAsync(enableSaml: false);
            _output.WriteLine($"[disabled] Saml2:Enabled={disabled.GetValueOrDefault("Saml2:Enabled")}");
            Assert.Equal("false", disabled.GetValueOrDefault("Saml2:Enabled")); // no button when the scheme is off
        }

        private async Task<Dictionary<string, string?>> GetSiteSettingsOverHttpAsync(bool enableSaml)
        {
            using var provider = new SqliteProvider();
            using var cert = SamlTestIdp.CreateSigningCertificate();
            using var scratch = new ScratchInstall(provider, cert, IdpEntityId, enableSaml: enableSaml, enableWindows: false);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
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
                }, $"login-button tenant (saml={enableSaml})");

                await app.StartAsync();

                int siteId;
                using (var scope = app.Services.CreateScope())
                {
                    var alias = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasAsync(Host);
                    siteId = alias.SiteId;
                }

                var server = app.GetTestServer();
                var cookies = new CookieContainer();
                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + Host) };

                var response = await SendAsync(client, cookies, HttpMethod.Get, $"/api/Site/{siteId}");
                _output.WriteLine($"[api site saml={enableSaml}] GET /api/Site/{siteId} -> {(int)response.StatusCode}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var json = await response.Content.ReadAsStringAsync();
                var site = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
                Assert.NotNull(site);
                // Web API JSON casing is not assumed — find the settings object case-insensitively.
                var settingsPair = site!.FirstOrDefault(p => string.Equals(p.Key, "settings", StringComparison.OrdinalIgnoreCase));
                var settings = settingsPair.Value?.AsObject();
                Assert.NotNull(settings);

                var result = new Dictionary<string, string?>();
                foreach (var kv in settings!)
                {
                    result[kv.Key] = kv.Value?.GetValue<string>();
                }
                return result;
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Negotiate_smoke_and_render_safety_on_kestrel()
        {
            // Negotiate's request handler needs Kestrel (IConnectionItemsFeature), so this drives a REAL Kestrel
            // host on a loopback port with BOTH schemes enabled — the faithful "registered-but-not-default"
            // render-safety + the local Negotiate smoke (401 WWW-Authenticate: Negotiate without creds). Full
            // Kerberos requires a domain-joined host (sanctioned deferral).
            var port = GetFreePort();
            var hostWithPort = $"localhost:{port}";

            using var provider = new SqliteProvider();
            using var cert = SamlTestIdp.CreateSigningCertificate();
            using var scratch = new ScratchInstall(provider, cert, IdpEntityId, enableSaml: true, enableWindows: true);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseUrls($"http://{hostWithPort}"));

            try
            {
                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
                await InstallAsync(databaseManager, new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantConnectionString,
                    Aliases = hostWithPort, // alias is keyed on Host.Value, which includes the port on Kestrel
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
                }, "negotiate kestrel tenant");

                await app.StartAsync();

                // ---- Scheme table with BOTH registered: DEFAULT still the Identity cookie ----------------
                var schemeProvider = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
                var authOptions = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.Equal(Constants.AuthenticationScheme, authOptions.DefaultScheme);
                var all = (await schemeProvider.GetAllSchemesAsync()).Select(s => s.Name).ToList();
                _output.WriteLine("[kestrel schemes] " + string.Join(", ", all));
                Assert.Contains(StommeExternalSchemesExtensions.NegotiateScheme, all);  // "Negotiate"
                Assert.Contains(StommeExternalSchemesExtensions.Saml2Scheme, all);       // "Saml2"
                Assert.Equal(all.Count, all.Distinct().Count());

                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{hostWithPort}") };

                // ---- Render safety: with Negotiate REGISTERED-but-not-default, a normal GET / still 200 ---
                var home = await client.GetAsync("/");
                _output.WriteLine($"[kestrel render] GET / -> {(int)home.StatusCode}; WWW-Authenticate: {string.Join(",", home.Headers.WwwAuthenticate.Select(h => h.Scheme))}");
                Assert.Equal(HttpStatusCode.OK, home.StatusCode);
                Assert.DoesNotContain(home.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate"); // NOT a global challenge

                // ---- Negotiate smoke: the EXPLICIT window returns 401 WWW-Authenticate: Negotiate ---------
                var windows = await client.GetAsync("/pages/windows-login");
                _output.WriteLine($"[kestrel negotiate] GET /pages/windows-login -> {(int)windows.StatusCode}; WWW-Authenticate: {string.Join(",", windows.Headers.WwwAuthenticate.Select(h => h.Scheme))}");
                Assert.Equal(HttpStatusCode.Unauthorized, windows.StatusCode);
                Assert.Contains(windows.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");

                _output.WriteLine("[negotiate] render safe with both registered; explicit challenge -> 401 WWW-Authenticate: Negotiate.");
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ================= the self-hosted test IdP (signed-assertion minter) =========================
        private static class SamlTestIdp
        {
            private const string Saml2Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
            private const string Saml2Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
            private const string Saml2Metadata = "urn:oasis:names:tc:SAML:2.0:metadata";
            private const string EmailClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
            private const string NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
            private const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
            private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";

            public static X509Certificate2 CreateSigningCertificate()
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=stomme-test-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                // Round-trip through PFX so the private key is persisted/exportable for signing.
                return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            }

            public static string BuildMetadata(X509Certificate2 certificate, string idpEntityId)
            {
                var publicCert = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
                return
                    $"<EntityDescriptor xmlns=\"{Saml2Metadata}\" entityID=\"{idpEntityId}\">" +
                    "<IDPSSODescriptor protocolSupportEnumeration=\"" + Saml2Protocol + "\">" +
                    "<KeyDescriptor use=\"signing\">" +
                    "<KeyInfo xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><X509Data><X509Certificate>" +
                    publicCert +
                    "</X509Certificate></X509Data></KeyInfo>" +
                    "</KeyDescriptor>" +
                    "<SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\" " +
                    "Location=\"https://stomme-test-idp.example/sso\"/>" +
                    "</IDPSSODescriptor></EntityDescriptor>";
            }

            public static string BuildSignedResponse(
                X509Certificate2 certificate, string idpEntityId, string spEntityId, string acsUrl, string email, string name, string inResponseTo)
            {
                var now = DateTime.UtcNow;
                var issueInstant = now.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var notOnOrAfter = now.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var notBefore = now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var responseId = "_" + Guid.NewGuid().ToString("N");
                var assertionId = "_" + Guid.NewGuid().ToString("N");
                var inResponseToAttr = string.IsNullOrEmpty(inResponseTo) ? "" : $" InResponseTo=\"{inResponseTo}\"";

                var xml =
                    $"<samlp:Response xmlns:samlp=\"{Saml2Protocol}\" xmlns:saml=\"{Saml2Assertion}\" " +
                    $"ID=\"{responseId}\"{inResponseToAttr} Version=\"2.0\" IssueInstant=\"{issueInstant}\" Destination=\"{acsUrl}\">" +
                    $"<saml:Issuer>{idpEntityId}</saml:Issuer>" +
                    "<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>" +
                    $"<saml:Assertion ID=\"{assertionId}\" Version=\"2.0\" IssueInstant=\"{issueInstant}\">" +
                    $"<saml:Issuer>{idpEntityId}</saml:Issuer>" +
                    "<saml:Subject>" +
                    $"<saml:NameID Format=\"urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress\">{email}</saml:NameID>" +
                    "<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\">" +
                    $"<saml:SubjectConfirmationData{inResponseToAttr} NotOnOrAfter=\"{notOnOrAfter}\" Recipient=\"{acsUrl}\"/>" +
                    "</saml:SubjectConfirmation></saml:Subject>" +
                    $"<saml:Conditions NotBefore=\"{notBefore}\" NotOnOrAfter=\"{notOnOrAfter}\">" +
                    $"<saml:AudienceRestriction><saml:Audience>{spEntityId}</saml:Audience></saml:AudienceRestriction>" +
                    "</saml:Conditions>" +
                    $"<saml:AuthnStatement AuthnInstant=\"{issueInstant}\" SessionIndex=\"{assertionId}\">" +
                    "<saml:AuthnContext><saml:AuthnContextClassRef>" +
                    "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport" +
                    "</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement>" +
                    "<saml:AttributeStatement>" +
                    $"<saml:Attribute Name=\"{EmailClaimType}\"><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>" +
                    $"<saml:Attribute Name=\"{NameClaimType}\"><saml:AttributeValue>{name}</saml:AttributeValue></saml:Attribute>" +
                    "</saml:AttributeStatement>" +
                    "</saml:Assertion></samlp:Response>";

                var document = new XmlDocument { PreserveWhitespace = true };
                document.LoadXml(xml);
                SignAssertion(document, assertionId, certificate);
                return document.OuterXml;
            }

            /// <summary>
            /// Inflates a HTTP-Redirect-binding <c>SAMLRequest</c> (base64 + raw DEFLATE) and returns the
            /// AuthnRequest's <c>ID</c>, so the response can carry a matching <c>InResponseTo</c> (solicited flow).
            /// </summary>
            public static string? ReadAuthnRequestId(string? samlRequestParam)
            {
                if (string.IsNullOrEmpty(samlRequestParam))
                {
                    return null;
                }
                var deflated = Convert.FromBase64String(samlRequestParam);
                using var input = new MemoryStream(deflated);
                using var inflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(inflate, Encoding.UTF8);
                var requestXml = reader.ReadToEnd();
                var document = new XmlDocument();
                document.LoadXml(requestXml);
                return document.DocumentElement?.GetAttribute("ID");
            }

            private static void SignAssertion(XmlDocument document, string assertionId, X509Certificate2 certificate)
            {
                var nsManager = new XmlNamespaceManager(document.NameTable);
                nsManager.AddNamespace("saml", Saml2Assertion);
                var assertion = (XmlElement)document.SelectSingleNode("//saml:Assertion", nsManager)!;
                var issuer = (XmlElement)document.SelectSingleNode("//saml:Assertion/saml:Issuer", nsManager)!;

                var signedXml = new SignedXml(assertion) { SigningKey = certificate.GetRSAPrivateKey() };
                signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
                signedXml.SignedInfo!.SignatureMethod = RsaSha256;

                var reference = new Reference("#" + assertionId) { DigestMethod = Sha256 };
                reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
                reference.AddTransform(new XmlDsigExcC14NTransform());
                signedXml.AddReference(reference);

                var keyInfo = new KeyInfo();
                keyInfo.AddClause(new KeyInfoX509Data(certificate));
                signedXml.KeyInfo = keyInfo;

                signedXml.ComputeSignature();
                var signature = signedXml.GetXml();
                // SAML schema: the ds:Signature must sit immediately after the Assertion's Issuer.
                assertion.InsertAfter(document.ImportNode(signature, true), issuer);
            }
        }

        // ========================= harness (modelled on OpenIddictInteractiveLoginTests) ==============

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
                var value = pair.Substring(eq + 1).Trim();
                if (string.IsNullOrEmpty(value) || value == "deleted")
                {
                    continue;
                }
                try
                {
                    cookies.Add(new Cookie(pair.Substring(0, eq).Trim(), value, "/", uri.Host));
                }
                catch
                {
                    // ignore malformed/deletion cookies
                }
            }
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
            string? DatabaseTypePreseed { get; }
            void BindToScratch(string scratchDirectory);
        }

        private sealed class SqliteProvider : IProvider
        {
            private string _tenant = null!;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantConnectionString => _tenant;
            public string? DatabaseTypePreseed => null;
            public void BindToScratch(string scratch) => _tenant = $"Data Source={Path.Combine(scratch, "tenant.db")};";
            public void Dispose()
            {
                // This provider's own database, never the process-wide ClearAllPools (BL-104).
                ScratchSqlite.Release(_tenant);
            }
        }

        private sealed class PostgresProvider : IProvider
        {
            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();
            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string TenantConnectionString => Build($"stomme_saml_t_{_suffix}");
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
            private static string BaseUrl => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();
            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantConnectionString => Build($"stomme_saml_t_{_suffix}");
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
                        new DeleteDatabasesOperation.Parameters { DatabaseNames = _databases.ToArray(), HardDelete = true }));
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
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

        /// <summary>
        /// A throwaway content root pre-seeded with a file-based test-IdP metadata and config-gated
        /// Saml2/WindowsAuth. NOTE: the Negotiate handler is an IAuthenticationRequestHandler that THROWS on
        /// TestServer (needs IConnectionItemsFeature/Kestrel), so a TestServer boot must NOT enable WindowsAuth
        /// — the Negotiate smoke runs on a real Kestrel host instead.
        /// </summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstall(IProvider provider, X509Certificate2 idpCertificate, string idpEntityId,
                bool enableSaml, bool enableWindows)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-saml-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);

                var metadataPath = Path.Combine(ContentRoot, "test-idp-metadata.xml");
                System.IO.File.WriteAllText(metadataPath, SamlTestIdp.BuildMetadata(idpCertificate, idpEntityId));

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new System.Text.Json.Nodes.JsonObject
                {
                    ["Saml2"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Enabled"] = enableSaml,
                        ["SpEntityId"] = SpEntityId,
                        ["IdpEntityId"] = idpEntityId,
                        ["IdpMetadataLocation"] = metadataPath,
                        ["AllowUnsolicited"] = true
                    },
                    ["WindowsAuth"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Enabled"] = enableWindows
                    }
                };
                if (provider.DatabaseTypePreseed != null)
                {
                    overrides["Database"] = new System.Text.Json.Nodes.JsonObject { ["DefaultDBType"] = provider.DatabaseTypePreseed };
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
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only - BL-104
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
