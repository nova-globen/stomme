#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A folder site is its own OpenIddict authority at <c>https://host/folder/</c> (D116), so a native app signs that site's
    /// user in with authorization code + PKCE through a standard OIDC client, and the site's API accepts only that site's
    /// tokens. Driven on one host: the root site, <c>/delato</c> sharing the root's tenant (one OpenIddict store, one user
    /// store - the hardest case for isolation) and <c>/didin</c> in a tenant of its own. A public client limited to
    /// <c>/delato</c> with a private-use-scheme redirect, as a phone app registers, and a second client used at the root.
    /// </summary>
    public abstract class FolderSiteAuthorityDrive
    {
        private const string Root = "localhost";
        private const string Delato = "localhost/delato";
        private const string Didin = "localhost/didin";
        private const string AppClient = "delato-app";
        private const string AppRedirect = "se.novaglobenab.delato:/oauth2redirect";
        private const string RootClient = "root-app";
        private const string RootRedirect = "http://127.0.0.1:7171/callback";

        protected readonly ITestOutputHelper _output;

        protected FolderSiteAuthorityDrive(ITestOutputHelper output) => _output = output;

        protected async Task DriveAsync(AuthorityScratch scratch)
        {
            using var env = new FolderSiteCookieTests.EnvironmentVariables(Settings(scratch.DidinConnectionString));
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = GateDrive.NewClient(app, Root);

                // Each site publishes its own issuer, and its endpoints under its own path.
                var rootIssuer = await IssuerAsync(client, "");
                var delatoIssuer = await IssuerAsync(client, "/delato");
                var didinIssuer = await IssuerAsync(client, "/didin");
                Assert.Equal("http://localhost/", rootIssuer);
                Assert.Equal("http://localhost/delato", delatoIssuer);
                Assert.Equal("http://localhost/didin", didinIssuer);

                // Not signed in: the folder site's authorize sends the browser to the folder site's own login page.
                var (verifier, challenge) = Pkce();
                var anonymous = await client.GetAsync(AuthorizeUrl("/delato", AppClient, AppRedirect, challenge));
                var login = anonymous.Headers.Location?.ToString() ?? "";
                _output.WriteLine($"anonymous authorize -> {(int)anonymous.StatusCode} {login}");
                Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
                Assert.StartsWith("/delato/login?returnurl=", login);
                Assert.Contains(WebUtility.UrlEncode("/delato/connect/authorize"), login);

                // Signed in at the folder site, the same request comes back to the app with a code.
                var delatoJar = await LoginAsync(app, client, "/delato", "-delato");
                var code = await CodeAsync(client, delatoJar, "/delato", AppClient, AppRedirect, challenge);
                var delatoTokens = await ExchangeAsync(client, "/delato", AppClient, AppRedirect, code, verifier);
                var delatoAccess = delatoTokens["access_token"]!.GetValue<string>();
                var claims = new JwtSecurityTokenHandler().ReadJwtToken(delatoAccess);
                Assert.Equal("http://localhost/delato", claims.Issuer);
                Assert.Equal(Delato, claims.Claims.First(claim => claim.Type == StommeOpenIddictExtensions.SiteClaim).Value);
                var idToken = new JwtSecurityTokenHandler().ReadJwtToken(delatoTokens["id_token"]!.GetValue<string>());
                Assert.Equal(Delato, idToken.Claims.First(claim => claim.Type == StommeOpenIddictExtensions.SiteClaim).Value);

                // The folder site's API accepts it; the root's and the other folder site's do not.
                Assert.True(await AuthenticatedAsync(client, "/delato", delatoAccess), "the folder site's own token was refused at its API");
                Assert.False(await AuthenticatedAsync(client, "", delatoAccess), "the folder site's token signed the caller in at the root");
                Assert.False(await AuthenticatedAsync(client, "/didin", delatoAccess), "the folder site's token signed the caller in at another folder site");

                // A root token, for the same user in the same tenant, is refused at the folder site.
                var rootJar = await GateDrive.LoginAsync(app, client, Root, UserNames.Host, FolderSiteCookieTests.Password, _output);
                var (rootVerifier, rootChallenge) = Pkce();
                var rootCode = await CodeAsync(client, rootJar, "", RootClient, RootRedirect, rootChallenge);
                var rootAccess = (await ExchangeAsync(client, "", RootClient, RootRedirect, rootCode, rootVerifier))["access_token"]!.GetValue<string>();
                Assert.True(await AuthenticatedAsync(client, "", rootAccess), "the root's own token was refused at its API");
                Assert.False(await AuthenticatedAsync(client, "/delato", rootAccess), "a root token signed the caller in at the folder site");

                // A desktop app listens on a loopback port of its own choosing (RFC 8252 7.3): any port matches the registered one.
                var (loopbackVerifier, loopbackChallenge) = Pkce();
                const string AnyPort = "http://127.0.0.1:53917/callback";
                var loopbackCode = await CodeAsync(client, rootJar, "", RootClient, AnyPort, loopbackChallenge);
                Assert.NotNull((await ExchangeAsync(client, "", RootClient, AnyPort, loopbackCode, loopbackVerifier))["access_token"]);

                // The folder client is limited to its site: the root (same tenant, same store) refuses it.
                var elsewhere = await client.SendAsync(AuthorizeWithCookies(rootJar, "", AppClient, AppRedirect, Pkce().Challenge));
                var elsewhereBody = await elsewhere.Content.ReadAsStringAsync();
                _output.WriteLine($"folder client at the root -> {(int)elsewhere.StatusCode} {elsewhere.Headers.Location} {elsewhereBody}");
                Assert.Contains("unauthorized_client", (elsewhere.Headers.Location?.ToString() ?? "") + elsewhereBody);

                // A refresh at the folder site renews the token; revoking it there ends the refresh.
                var refreshToken = delatoTokens["refresh_token"]!.GetValue<string>();
                var renewed = await RefreshAsync(client, "/delato", refreshToken);
                Assert.True(renewed.IsSuccessStatusCode, $"refresh: {(int)renewed.StatusCode} {await renewed.Content.ReadAsStringAsync()}");
                var rotated = JsonNode.Parse(await renewed.Content.ReadAsStringAsync())!["refresh_token"]!.GetValue<string>();
                Assert.True(await AuthenticatedAsync(client, "/delato", JsonNode.Parse(await renewed.Content.ReadAsStringAsync()) is { } node ? node["access_token"]!.GetValue<string>() : ""), "the refreshed token was refused");

                var revoke = await client.PostAsync("http://localhost/delato/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = rotated,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = AppClient
                }));
                _output.WriteLine($"revoke -> {(int)revoke.StatusCode} {await revoke.Content.ReadAsStringAsync()}");
                Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
                var afterRevoke = await RefreshAsync(client, "/delato", rotated);
                _output.WriteLine($"refresh after revoke -> {(int)afterRevoke.StatusCode} {await afterRevoke.Content.ReadAsStringAsync()}");
                Assert.Equal(HttpStatusCode.BadRequest, afterRevoke.StatusCode);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Dictionary<string, string> Settings(string didinConnectionString) => new()
        {
            ["OpenIddict__Enabled"] = "true",
            ["Installation__Sites__0__Name"] = "Delato",
            ["Installation__Sites__0__Aliases"] = Delato,
            ["Installation__Sites__1__Name"] = "Didin",
            ["Installation__Sites__1__Aliases"] = Didin,
            ["Installation__Sites__1__Tenant"] = "Didin",
            ["Installation__Sites__1__ConnectionString"] = didinConnectionString,

            // The phone app: public, code + PKCE, a private-use-scheme redirect, refresh tokens, limited to its site.
            ["OpenIddict__Applications__0__ClientId"] = AppClient,
            ["OpenIddict__Applications__0__Type"] = "public",
            ["OpenIddict__Applications__0__GrantTypes__0"] = "authorization_code",
            ["OpenIddict__Applications__0__GrantTypes__1"] = "refresh_token",
            ["OpenIddict__Applications__0__Scopes__0"] = "openid",
            ["OpenIddict__Applications__0__Scopes__1"] = "profile",
            ["OpenIddict__Applications__0__Scopes__2"] = "offline_access",
            ["OpenIddict__Applications__0__Scopes__3"] = "api",
            ["OpenIddict__Applications__0__RedirectUris__0"] = AppRedirect,
            ["OpenIddict__Applications__0__Sites__0"] = Delato,

            // A client for the root, limited to nothing.
            ["OpenIddict__Applications__1__ClientId"] = RootClient,
            ["OpenIddict__Applications__1__Type"] = "public",
            ["OpenIddict__Applications__1__GrantTypes__0"] = "authorization_code",
            ["OpenIddict__Applications__1__Scopes__0"] = "openid",
            ["OpenIddict__Applications__1__Scopes__1"] = "api",
            ["OpenIddict__Applications__1__RedirectUris__0"] = RootRedirect
        };

        private async Task<string> IssuerAsync(HttpClient client, string site)
        {
            var response = await client.GetAsync($"http://localhost{site}/.well-known/openid-configuration");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"discovery at '{site}': {(int)response.StatusCode} {body}");
            var document = JsonNode.Parse(body)!;
            _output.WriteLine($"discovery '{site}': issuer {document["issuer"]}, token {document["token_endpoint"]}, revocation {document["revocation_endpoint"]}");
            Assert.Equal($"http://localhost{site}/connect/token", document["token_endpoint"]!.GetValue<string>());
            Assert.Equal($"http://localhost{site}/connect/revoke", document["revocation_endpoint"]!.GetValue<string>());
            return document["issuer"]!.GetValue<string>();
        }

        private static string AuthorizeUrl(string site, string clientId, string redirect, string challenge) =>
            QueryHelpers.AddQueryString($"http://localhost{site}/connect/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["scope"] = clientId == AppClient ? "openid profile offline_access api" : "openid api",
                ["redirect_uri"] = redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "drive"
            });

        private static HttpRequestMessage AuthorizeWithCookies(CookieContainer jar, string site, string clientId, string redirect, string challenge)
        {
            var url = AuthorizeUrl(site, clientId, redirect, challenge);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var header = jar.GetCookieHeader(new Uri(url));
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            return request;
        }

        private async Task<string> CodeAsync(HttpClient client, CookieContainer jar, string site, string clientId, string redirect, string challenge)
        {
            var response = await client.SendAsync(AuthorizeWithCookies(jar, site, clientId, redirect, challenge));
            var location = response.Headers.Location?.ToString() ?? "";
            _output.WriteLine($"authorize '{site}' {clientId} -> {(int)response.StatusCode} {location}");
            Assert.StartsWith(redirect, location);
            var code = QueryHelpers.ParseQuery(new Uri(location).Query)["code"].ToString();
            Assert.False(string.IsNullOrEmpty(code), "no authorization code");
            return code;
        }

        private async Task<JsonObject> ExchangeAsync(HttpClient client, string site, string clientId, string redirect, string code, string verifier)
        {
            var response = await client.PostAsync($"http://localhost{site}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            }));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"token at '{site}': {(int)response.StatusCode} {body}");
            return JsonNode.Parse(body)!.AsObject();
        }

        private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string site, string refreshToken) =>
            client.PostAsync($"http://localhost{site}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = AppClient
            }));

        private async Task<bool> AuthenticatedAsync(HttpClient client, string site, string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost{site}/api/User/authenticate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"GET {site}/api/User/authenticate (bearer) -> {(int)response.StatusCode} {body}");
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(body)?["isAuthenticated"]?.GetValue<bool>() == true;
        }

        // The folder site's own sign-in form post: its antiforgery cookie travels under the folder site's name.
        private async Task<CookieContainer> LoginAsync(WebApplication app, HttpClient client, string site, string suffix)
        {
            var jar = new CookieContainer();
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app);
            var cookie = antiforgeryCookie.Replace(Constants.AntiForgeryTokenCookieName + "=", Constants.AntiForgeryTokenCookieName + suffix + "=", StringComparison.Ordinal);
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost{site}/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = UserNames.Host,
                    ["password"] = FolderSiteCookieTests.Password,
                    ["remember"] = "false",
                    ["returnurl"] = site + "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await client.SendAsync(request);
            GateDrive.CaptureCookies(jar, new Uri($"http://localhost{site}/"), response);
            _output.WriteLine($"POST {site}/pages/login -> {(int)response.StatusCode}");
            return jar;
        }

        private static (string Verifier, string Challenge) Pkce()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The folder-site authority on SQLite, every gate run (D116).</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class FolderSiteAuthorityTests : FolderSiteAuthorityDrive
    {
        public FolderSiteAuthorityTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void The_folder_sites_authority_requests_are_recognised_and_nothing_else()
        {
            Assert.True(TenantMiddleware.IsAuthorityRequest("/delato/connect/token", "delato", out var path));
            Assert.Equal("/connect/token", path);
            Assert.True(TenantMiddleware.IsAuthorityRequest("/Delato/.well-known/openid-configuration", "delato", out path));
            Assert.Equal("/.well-known/openid-configuration", path);
            Assert.True(TenantMiddleware.IsAuthorityRequest("/shop/se/connect/authorize", "shop/se/", out path));
            Assert.Equal("/connect/authorize", path);

            Assert.False(TenantMiddleware.IsAuthorityRequest("/delato/api/connect/x", "delato", out _));
            Assert.False(TenantMiddleware.IsAuthorityRequest("/delatox/connect/token", "delato", out _));
            Assert.False(TenantMiddleware.IsAuthorityRequest("/connect/token", "delato", out _));
            Assert.False(TenantMiddleware.IsAuthorityRequest("/delato/connections", "delato", out _));

            Assert.Contains("connect", Constants.ReservedRoutes);
            Assert.Contains(".well-known", Constants.ReservedRoutes);
        }

        [Theory]
        [InlineData("http://127.0.0.1:53917/callback", "http://127.0.0.1:7171/callback", true)]
        [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:7171/callback", true)]
        [InlineData("http://[::1]:61000/cb?x=1", "http://[::1]/cb?x=1", true)]
        [InlineData("http://127.0.0.1:53917/other", "http://127.0.0.1:7171/callback", false)]
        [InlineData("http://127.0.0.1:53917/callback?x=1", "http://127.0.0.1:7171/callback", false)]
        [InlineData("http://[::1]:53917/callback", "http://127.0.0.1:7171/callback", false)]
        [InlineData("http://localhost:53917/callback", "http://localhost:7171/callback", false)]
        [InlineData("https://127.0.0.1:53917/callback", "https://127.0.0.1:7171/callback", false)]
        [InlineData("http://192.168.1.5:53917/callback", "http://192.168.1.5:7171/callback", false)]
        public void A_loopback_redirect_matches_on_any_port_and_nothing_else_does(string requested, string registered, bool matches)
        {
            var result = LoopbackRedirectApplicationManager<object>.IsLoopback(requested, out var a)
                && LoopbackRedirectApplicationManager<object>.IsLoopback(registered, out var b)
                && LoopbackRedirectApplicationManager<object>.SameExceptPort(a, b);
            Assert.Equal(matches, result);
        }

        [Fact]
        public async Task A_folder_sites_user_gets_its_own_token_through_code_and_pkce_and_no_other_sites_token_is_accepted_there()
        {
            using var scratch = AuthorityScratch.Sqlite();
            await DriveAsync(scratch);
        }
    }

    /// <summary>
    /// The folder-site authority on RavenDB: the per-tenant OpenIddict stores are RavenDB's own, and the parity principle
    /// holds - an unreachable RavenDB fails the drive, it never skips.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class FolderSiteAuthorityRavenTests : FolderSiteAuthorityDrive
    {
        public FolderSiteAuthorityRavenTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task A_folder_sites_user_gets_its_own_token_on_RavenDB_and_no_other_sites_token_is_accepted_there()
        {
            using var scratch = AuthorityScratch.RavenDb(Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000");
            await DriveAsync(scratch);
        }
    }

    /// <summary>A content root for the drive: the installation's database and the Didin tenant's, on one provider.</summary>
    public sealed class AuthorityScratch : IDisposable
    {
        private readonly string? _previousOverridePath;
        private readonly string? _ravenUrl;
        private readonly List<string> _ravenDatabases = new();

        private AuthorityScratch(string databaseType, Func<string, string> connection, string? ravenUrl)
        {
            _ravenUrl = ravenUrl;
            ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-folder-authority-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
            Directory.CreateDirectory(Path.Combine(ContentRoot, "Data"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(ContentRoot, "appsettings.json"));
            var passwordFile = Path.Combine(ContentRoot, "host-password");
            File.WriteAllText(passwordFile, FolderSiteCookieTests.Password + "\n");
            DidinConnectionString = connection("didin");
            var overrides = new JsonObject
            {
                ["Database"] = new JsonObject { ["DefaultDBType"] = databaseType },
                ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connection("root") },
                ["Installation"] = new JsonObject
                {
                    ["DefaultAlias"] = "localhost",
                    ["HostEmail"] = GateDrive.HostEmail,
                    ["HostPasswordFile"] = passwordFile
                }
            };
            File.WriteAllText(Path.Combine(ContentRoot, "appsettings.override.json"), overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
        }

        public string ContentRoot { get; }

        public string DidinConnectionString { get; }

        public static AuthorityScratch Sqlite() =>
            new("Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server", name => $"Data Source=|DataDirectory|/{name}.db;", null);

        public static AuthorityScratch RavenDb(string url)
        {
            var token = Guid.NewGuid().ToString("N").Substring(0, 12);
            AuthorityScratch? scratch = null;
            scratch = new AuthorityScratch("Stomme.Database.RavenDb.RavenDatabase, Stomme.Server", name =>
            {
                var database = $"stomme_fsa_{name}_{token}";
                scratch?._ravenDatabases.Add(database);
                return $"Url={url};Database={database}";
            }, url);
            scratch._ravenDatabases.Add($"stomme_fsa_root_{token}");
            scratch._ravenDatabases.Add($"stomme_fsa_didin_{token}");
            return scratch;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
            ScratchSqlite.ReleaseInstall(ContentRoot);
            if (_ravenUrl != null)
            {
                // The installation's and the tenant's databases and their OpenIddict siblings; best effort, the test hosts'
                // sweep removes what is left on a loopback server.
                try
                {
                    using var store = new DocumentStore { Urls = new[] { _ravenUrl } };
                    store.Initialize();
                    var names = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1024))
                        .Where(name => _ravenDatabases.Distinct().Any(database => name.StartsWith(database, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                    if (names.Length > 0)
                    {
                        store.Maintenance.Server.Send(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters { DatabaseNames = names, HardDelete = true }));
                    }
                }
                catch
                {
                    // best effort
                }
            }

            try
            {
                Directory.Delete(ContentRoot, true);
            }
            catch
            {
                // best effort - SQLite may still hold a file
            }
        }
    }
}
