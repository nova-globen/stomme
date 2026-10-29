using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    /// The IdP ADMIN UI, driven end to end: the Host-only <c>admin/openiddict</c> page is seeded, RENDERS, and
    /// its every action works over real HTTP against the exact <c>api/OpenIddict</c> channel the Blazor
    /// <c>IOpenIddictService</c> calls — on whichever store the install chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a DRIVE, not a boot check (the D47 bar): a client created through the admin channel must go on to
    /// mint a real <c>client_credentials</c> token, an edit that leaves the secret blank must leave the ORIGINAL
    /// secret working, and supplying a secret must actually rotate it. Each of those is a claim about
    /// OpenIddict's update semantics that the compiler cannot check and that a row-count assertion would miss.
    /// </para>
    /// <para>
    /// Providers per D46: SQLite always; PostgreSQL when the local service is reachable; RavenDB
    /// UNCONDITIONALLY (fail-not-skip). The admin channel calls <c>PopulateAsync</c> and <c>UpdateAsync</c> on
    /// the store, which the config seeder never exercised on the Raven twins — an EF-only pass would prove
    /// nothing about them.
    /// </para>
    /// <para>
    /// The gate on a STOCK install is the last test: with no <c>OpenIddict:Enabled</c>, the admin page is not
    /// seeded at all, so the opt-in authority stays invisible.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class OpenIddictAdminUiTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string AdminPagePath = "admin/openiddict";

        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Idp$2026";
        private const string HostEmail = "host@idp.test";

        private const string PlainUsername = "plainuser";
        private const string PlainPassword = "Pl41n!Idp$2026";
        private const string PlainEmail = "plainuser@idp.test";

        // The client the config seeder registers, so the UI has something to list before it creates anything.
        private const string SeededClientId = "seeded-web";
        private const string SeededSecret = "S3cr3t!Seeded$2026";

        // The client the ADMIN UI creates, edits, rotates and deletes.
        private const string UiClientId = "ui-created";
        private const string UiSecret = "S3cr3t!UiCreated$2026";
        private const string UiRotatedSecret = "R0t4t3d!UiCreated$2026";

        private readonly ITestOutputHelper _output;

        public OpenIddictAdminUiTests(ITestOutputHelper output) => _output = output;

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { "SQLite" };
            if (PostgresProvider.IsReachable())
            {
                yield return new object[] { "PostgreSQL" };
            }
            yield return new object[] { "RavenDB" };
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Admin_ui_manages_clients_and_scopes_over_http(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new ScratchInstall(provider, openIddictEnabled: true);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                await InstallAsync(app, provider);

                // ---- 1. The admin page + module definition were seeded, Host-only ------------------------
                var page = await AdminPageAsync(app);
                Assert.NotNull(page);
                _output.WriteLine($"[{providerName}] seeded page '{page!.Path}' (PageId {page.PageId})");
                AssertHostOnly(page.PermissionList, "page");

                var definition = await ModuleDefinitionAsync(app, "OpenIddict");
                Assert.NotNull(definition);
                Assert.Contains(definition!.PermissionList,
                    p => p.PermissionName == PermissionNames.Utilize && p.RoleName == RoleNames.Host && p.IsAuthorized);
                Assert.DoesNotContain(definition.PermissionList,
                    p => p.PermissionName == PermissionNames.Utilize && p.RoleName == RoleNames.Admin);

                await app.StartAsync();
                var server = app.GetTestServer();

                int hostUserId = await UserIdAsync(app, HostUsername);
                var hostCookies = new CookieContainer();

                using (var client = NewClient(server))
                {
                    await LoginAsync(app, client, hostCookies, HostUsername, HostPassword);
                    AssertIdentityCookie(hostCookies, "host");

                    // ---- 2. The page is REACHABLE by the authenticated host (200, not a login redirect) ----
                    // Admin pages are INTERACTIVE (a Server/WASM circuit), so a raw HTTP GET returns the app
                    // shell + the Stomme admin frame, not the module's client-loaded content — that arrives
                    // over the API drives below. So the render check here is: the host reaches the page (a
                    // non-host would be bounced), it is the Stomme host shell, and it is substantial. The
                    // module's actual content (the seeded client, the endpoints) is proven by steps 3-9 hitting
                    // the exact api/OpenIddict channel Index.razor binds to.
                    var response = await client.SendAsync(Authenticated(HttpMethod.Get, hostCookies, "/" + AdminPagePath));
                    var rendered = await response.Content.ReadAsStringAsync();
                    _output.WriteLine($"[{providerName}] GET /{AdminPagePath} -> {(int)response.StatusCode}, {rendered.Length} bytes");
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Contains("blazor", rendered, StringComparison.OrdinalIgnoreCase);
                    Assert.True(rendered.Length > 2000, $"admin page shell was unexpectedly small ({rendered.Length} bytes)");

                    // ---- 3. status: enabled, with THIS tenant's endpoints --------------------------------
                    var status = await GetJsonAsync<OpenIddictStatus>(client, hostCookies, "/api/OpenIddict/status");
                    Assert.NotNull(status);
                    Assert.True(status!.Enabled);
                    Assert.Equal($"http://{TenantHost}", status.Issuer);
                    Assert.Equal($"http://{TenantHost}/connect/token", status.TokenEndpoint);
                    Assert.Equal($"http://{TenantHost}/.well-known/openid-configuration", status.DiscoveryEndpoint);

                    // ---- 4. list: the config-seeded client is there, with DERIVED grants/scopes ----------
                    var applications = await GetJsonAsync<List<OpenIddictApplicationSummary>>(
                        client, hostCookies, "/api/OpenIddict/applications");
                    var seeded = applications!.Single(a => a.ClientId == SeededClientId);
                    Assert.Contains("client_credentials", seeded.GrantTypes);
                    Assert.Contains("openid", seeded.Scopes);
                    // The derived lists must be UNPREFIXED — a raw "gt:client_credentials" here would round-trip
                    // back into the upsert and register a bogus permission.
                    Assert.DoesNotContain(seeded.GrantTypes, g => g.Contains(':'));
                    Assert.DoesNotContain(seeded.Scopes, s => s.Contains(':'));

                    // ---- 5. CREATE through the admin channel, exactly as Add.razor posts -----------------
                    var created = await PostJsonAsync<OpenIddictApplicationSeed, OpenIddictApplicationSummary>(
                        app, client, hostCookies, hostUserId, "/api/OpenIddict/applications",
                        new OpenIddictApplicationSeed
                        {
                            ClientId = UiClientId,
                            DisplayName = "Created By The Admin UI",
                            Type = "confidential",
                            ClientSecret = UiSecret,
                            ConsentType = "implicit",
                            GrantTypes = new List<string> { "authorization_code", "client_credentials", "refresh_token" },
                            Scopes = new List<string> { "openid", "profile", "email", "offline_access", "api" },
                            RedirectUris = new List<string> { $"http://{TenantHost}/signin-oidc" },
                            PostLogoutRedirectUris = new List<string> { $"http://{TenantHost}/signout-callback-oidc" },
                            RequirePkce = true
                        });
                    Assert.NotNull(created);
                    Assert.Equal(UiClientId, created!.ClientId);
                    Assert.True(created.RequirePkce);
                    Assert.Contains("client_credentials", created.GrantTypes);
                    Assert.Contains("api", created.Scopes);
                    // A summary must NEVER carry a secret back.
                    Assert.DoesNotContain("ClientSecret", JsonSerializer.Serialize(created));

                    // It is a REAL client, not just a row: it mints a token.
                    var token = await ClientCredentialsTokenAsync(client, UiClientId, UiSecret);
                    _output.WriteLine($"[{providerName}] created client token -> {(int)token.Status}");
                    Assert.Equal(HttpStatusCode.OK, token.Status);
                    Assert.False(string.IsNullOrEmpty(token.AccessToken), token.Body);

                    // ---- 6. EDIT with a BLANK secret keeps the stored one (the Edit.razor affordance) ----
                    // The stored secret is hashed and unrecoverable, so the UI cannot re-supply it. If this
                    // silently wiped or re-hashed the secret, every relying party would break the moment an
                    // operator changed a redirect URI — asserting the row changed would not catch that, so the
                    // ORIGINAL secret is driven through the token endpoint again.
                    var edited = await PostJsonAsync<OpenIddictApplicationSeed, OpenIddictApplicationSummary>(
                        app, client, hostCookies, hostUserId, "/api/OpenIddict/applications",
                        new OpenIddictApplicationSeed
                        {
                            ClientId = UiClientId,
                            DisplayName = "Renamed By The Admin UI",
                            Type = "confidential",
                            ClientSecret = null, // blank == keep
                            ConsentType = "implicit",
                            GrantTypes = new List<string> { "authorization_code", "client_credentials", "refresh_token" },
                            Scopes = new List<string> { "openid", "profile", "email", "offline_access", "api" },
                            RedirectUris = new List<string> { $"http://{TenantHost}/signin-oidc", $"http://{TenantHost}/other-oidc" },
                            PostLogoutRedirectUris = new List<string> { $"http://{TenantHost}/signout-callback-oidc" },
                            RequirePkce = true
                        });
                    Assert.NotNull(edited);
                    Assert.Equal("Renamed By The Admin UI", edited!.DisplayName);
                    Assert.Equal(2, edited.RedirectUris.Count);

                    var afterEdit = await ClientCredentialsTokenAsync(client, UiClientId, UiSecret);
                    _output.WriteLine($"[{providerName}] original secret after blank-secret edit -> {(int)afterEdit.Status}");
                    Assert.Equal(HttpStatusCode.OK, afterEdit.Status);

                    // ---- 7. Supplying a secret ROTATES it: new works, old is rejected --------------------
                    await PostJsonAsync<OpenIddictApplicationSeed, OpenIddictApplicationSummary>(
                        app, client, hostCookies, hostUserId, "/api/OpenIddict/applications",
                        new OpenIddictApplicationSeed
                        {
                            ClientId = UiClientId,
                            DisplayName = "Renamed By The Admin UI",
                            Type = "confidential",
                            ClientSecret = UiRotatedSecret,
                            ConsentType = "implicit",
                            GrantTypes = new List<string> { "client_credentials" },
                            Scopes = new List<string> { "api" },
                            RequirePkce = false
                        });

                    var withNew = await ClientCredentialsTokenAsync(client, UiClientId, UiRotatedSecret);
                    var withOld = await ClientCredentialsTokenAsync(client, UiClientId, UiSecret);
                    _output.WriteLine($"[{providerName}] rotation: new={(int)withNew.Status} old={(int)withOld.Status}");
                    Assert.Equal(HttpStatusCode.OK, withNew.Status);
                    Assert.NotEqual(HttpStatusCode.OK, withOld.Status);

                    // ---- 8. SCOPES: create, list, delete -------------------------------------------------
                    var scope = await PostJsonAsync<OpenIddictScopeSeed, OpenIddictScopeSeed>(
                        app, client, hostCookies, hostUserId, "/api/OpenIddict/scopes",
                        new OpenIddictScopeSeed
                        {
                            Name = "invoices.read",
                            DisplayName = "Read Invoices",
                            Description = "Read-only access to invoices",
                            Resources = new List<string> { "billing-api" }
                        });
                    Assert.NotNull(scope);

                    var scopes = await GetJsonAsync<List<OpenIddictScopeSeed>>(client, hostCookies, "/api/OpenIddict/scopes");
                    var stored = scopes!.Single(s => s.Name == "invoices.read");
                    Assert.Equal("Read Invoices", stored.DisplayName);
                    Assert.Contains("billing-api", stored.Resources);

                    var scopeDeleted = await DeleteAsync(app, client, hostCookies, hostUserId, "/api/OpenIddict/scopes/invoices.read");
                    Assert.Equal(HttpStatusCode.NoContent, scopeDeleted);
                    scopes = await GetJsonAsync<List<OpenIddictScopeSeed>>(client, hostCookies, "/api/OpenIddict/scopes");
                    Assert.DoesNotContain(scopes!, s => s.Name == "invoices.read");

                    // ---- 9. DELETE the client: gone from the list AND unable to obtain a token -----------
                    var deleted = await DeleteAsync(app, client, hostCookies, hostUserId, $"/api/OpenIddict/applications/{UiClientId}");
                    Assert.Equal(HttpStatusCode.NoContent, deleted);

                    applications = await GetJsonAsync<List<OpenIddictApplicationSummary>>(
                        client, hostCookies, "/api/OpenIddict/applications");
                    Assert.DoesNotContain(applications!, a => a.ClientId == UiClientId);

                    var afterDelete = await ClientCredentialsTokenAsync(client, UiClientId, UiRotatedSecret);
                    _output.WriteLine($"[{providerName}] deleted client token -> {(int)afterDelete.Status}");
                    Assert.NotEqual(HttpStatusCode.OK, afterDelete.Status);
                }

                // ---- 10. A non-host is denied every action (the gate is Host, not Admin) -----------------
                await CreateRegisteredUserAsync(app, PlainUsername, PlainPassword, PlainEmail);
                int plainUserId = await UserIdAsync(app, PlainUsername);
                var plainCookies = new CookieContainer();

                using (var client = NewClient(server))
                {
                    await LoginAsync(app, client, plainCookies, PlainUsername, PlainPassword);
                    AssertIdentityCookie(plainCookies, "non-host");

                    Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(client, plainCookies, "/api/OpenIddict/status"));
                    Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(client, plainCookies, "/api/OpenIddict/applications"));
                    Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(client, plainCookies, "/api/OpenIddict/scopes"));

                    var forbiddenPost = await PostStatusAsync(app, client, plainCookies, plainUserId,
                        "/api/OpenIddict/applications",
                        new OpenIddictApplicationSeed { ClientId = "should-not-exist", ClientSecret = "x", Type = "confidential" });
                    Assert.Equal(HttpStatusCode.Forbidden, forbiddenPost);
                }

                // and the denied write left nothing behind
                using (var client = NewClient(server))
                {
                    var applications = await GetJsonAsync<List<OpenIddictApplicationSummary>>(
                        client, hostCookies, "/api/OpenIddict/applications");
                    Assert.DoesNotContain(applications!, a => a.ClientId == "should-not-exist");
                }
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// A STOCK install (no <c>OpenIddict:Enabled</c>) must be unchanged: no admin page is seeded, so the
        /// opt-in authority adds nothing to the admin menu. The API still answers <c>status</c> — with
        /// <c>Enabled=false</c>, which is what lets the UI say "disabled" instead of guessing from a 404.
        /// </summary>
        [Fact]
        public async Task Stock_install_seeds_no_identity_provider_page()
        {
            using var provider = new SqliteProvider();
            using var scratch = new ScratchInstall(provider, openIddictEnabled: false);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                await InstallAsync(app, provider);

                var page = await AdminPageAsync(app);
                Assert.Null(page);

                await app.StartAsync();
                var server = app.GetTestServer();

                var cookies = new CookieContainer();
                using var client = NewClient(server);
                await LoginAsync(app, client, cookies, HostUsername, HostPassword);

                var status = await GetJsonAsync<OpenIddictStatus>(client, cookies, "/api/OpenIddict/status");
                Assert.NotNull(status);
                Assert.False(status!.Enabled);

                // Every other action stays 404 while disabled.
                Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(client, cookies, "/api/OpenIddict/applications"));
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        // ---- install + fixture helpers -------------------------------------------------------------------

        private async Task InstallAsync(WebApplication app, ISeedProvider provider)
        {
            var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
            var installTask = Task.Run(() => databaseManager.InstallAsync(new InstallConfig
            {
                DatabaseType = provider.DatabaseType,
                ConnectionString = provider.TenantConnectionString,
                Aliases = TenantHost,
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
            }));
            Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "Install timed out.");
            var install = await installTask;
            Assert.True(install.Success, $"Install failed: {install.Message}");
        }

        private static async Task<Page> AdminPageAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
            return pages.FirstOrDefault(p => p.Path == AdminPagePath);
        }

        private static async Task<ModuleDefinition> ModuleDefinitionAsync(WebApplication app, string shortName)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            // The module definition is keyed on the module NAMESPACE (a module = one folder of Index/Add/Edit
            // controls), not the individual component: "Stomme.Modules.Admin.OpenIddict, Stomme.Client".
            var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>().GetModuleDefinitionsAsync(alias.SiteId);
            return definitions.FirstOrDefault(d =>
                d.ModuleDefinitionName != null &&
                d.ModuleDefinitionName.StartsWith($"Stomme.Modules.Admin.{shortName},", StringComparison.Ordinal));
        }

        private static void AssertHostOnly(List<Permission> permissions, string what)
        {
            Assert.NotNull(permissions);
            Assert.Contains(permissions, p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Host && p.IsAuthorized);
            Assert.DoesNotContain(permissions, p => p.RoleName == RoleNames.Admin && p.IsAuthorized);
            Assert.DoesNotContain(permissions, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);
        }

        private static async Task<int> UserIdAsync(WebApplication app, string username)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            var user = await sp.GetRequiredService<IUserRepository>().GetUserAsync(username);
            Assert.NotNull(user);
            return user!.UserId;
        }

        private static async Task CreateRegisteredUserAsync(WebApplication app, string username, string password, string email)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            var created = await sp.GetRequiredService<IUserManager>().AddUser(new User
            {
                Username = username,
                Password = password,
                Email = email,
                DisplayName = username,
                SiteId = alias.SiteId,
                EmailConfirmed = true
            });
            Assert.NotNull(created);
        }

        // ---- HTTP helpers (the exact channel the Blazor IOpenIddictService uses) --------------------------

        private static HttpClient NewClient(TestServer server) =>
            new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + TenantHost) };

        private async Task LoginAsync(WebApplication app, HttpClient client, CookieContainer cookies, string username, string password)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app);

            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(request);
            _output.WriteLine($"[login {username}] POST /pages/login -> {(int)response.StatusCode}");
            CaptureCookies(cookies, new Uri("http://" + TenantHost), response);
        }

        private static HttpRequestMessage Authenticated(HttpMethod method, CookieContainer cookies, string path)
        {
            var uri = new Uri(new Uri("http://" + TenantHost), path);
            var request = new HttpRequestMessage(method, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }
            return request;
        }

        private static async Task<T> GetJsonAsync<T>(HttpClient client, CookieContainer cookies, string path)
        {
            var response = await client.SendAsync(Authenticated(HttpMethod.Get, cookies, path));
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }
            return await response.Content.ReadFromJsonAsync<T>();
        }

        private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, CookieContainer cookies, string path)
        {
            var response = await client.SendAsync(Authenticated(HttpMethod.Get, cookies, path));
            return response.StatusCode;
        }

        private static HttpRequestMessage UnsafeRequest(
            WebApplication app, CookieContainer cookies, int userId, HttpMethod method, string path, object body)
        {
            // The host globally validates antiforgery on unsafe methods, so an admin-UI write carries a matched
            // pair minted for THIS user — exactly what the interactive circuit's cookie-authenticated call does.
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + TenantHost), path);
            var request = new HttpRequestMessage(method, uri);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType());
            }

            var cookieHeader = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                cookieHeader += "; ";
            }
            cookieHeader += antiforgeryCookie;
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);
            return request;
        }

        private async Task<TResult> PostJsonAsync<TValue, TResult>(
            WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path, TValue body)
        {
            var response = await client.SendAsync(UnsafeRequest(app, cookies, userId, HttpMethod.Post, path, body));
            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"POST {path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                return default;
            }
            return await response.Content.ReadFromJsonAsync<TResult>();
        }

        private static async Task<HttpStatusCode> PostStatusAsync(
            WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path, object body)
        {
            var response = await client.SendAsync(UnsafeRequest(app, cookies, userId, HttpMethod.Post, path, body));
            return response.StatusCode;
        }

        private static async Task<HttpStatusCode> DeleteAsync(
            WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path)
        {
            var response = await client.SendAsync(UnsafeRequest(app, cookies, userId, HttpMethod.Delete, path, null));
            return response.StatusCode;
        }

        private static async Task<(HttpStatusCode Status, string Body, string AccessToken)> ClientCredentialsTokenAsync(
            HttpClient client, string clientId, string secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://{TenantHost}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = secret,
                    ["scope"] = "api"
                })
            };
            request.Headers.Host = TenantHost;
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            string accessToken = null;
            try { accessToken = JsonSerializer.Deserialize<JsonObject>(body)?["access_token"]?.GetValue<string>(); }
            catch (JsonException) { /* failure cases assert on status/body */ }
            return (response.StatusCode, body, accessToken);
        }

        private static (string requestToken, string cookie) GenerateAntiforgery(WebApplication app, int? userId = null)
        {
            using var scope = app.Services.CreateScope();
            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (userId.HasValue)
            {
                var identity = new System.Security.Claims.ClaimsIdentity(Constants.AuthenticationScheme);
                identity.AddClaim(new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, userId.Value.ToString()));
                httpContext.User = new System.Security.Claims.ClaimsPrincipal(identity);
            }
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            var setCookie = httpContext.Response.Headers.SetCookie.ToString();
            return (tokens.RequestToken!, setCookie.Split(';')[0]);
        }

        private void AssertIdentityCookie(CookieContainer cookies, string who)
        {
            var names = cookies.GetCookies(new Uri("http://" + TenantHost)).Cast<Cookie>().Select(c => c.Name).ToList();
            _output.WriteLine($"[{who}] cookies after login: {string.Join(", ", names)}");
            Assert.True(names.Any(n => n.Contains("Identity.Application")),
                $"[{who}] Identity cookie was not issued by /pages/login — authentication failed.");
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
                try { cookies.Add(new Cookie(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim(), "/", uri.Host)); }
                catch { /* ignore malformed/deletion cookies */ }
            }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        // ---- providers ------------------------------------------------------------------------------------

        private static ISeedProvider ResolveProvider(string name) => name switch
        {
            "SQLite" => new SqliteProvider(),
            "PostgreSQL" => new PostgresProvider(),
            "RavenDB" => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        private interface ISeedProvider : IDisposable
        {
            string DatabaseType { get; }
            string TenantConnectionString { get; }
            string DatabaseTypePreseed { get; }
            void BindToScratch(string scratchDirectory);
        }

        private sealed class SqliteProvider : ISeedProvider
        {
            private string _tenant;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantConnectionString => _tenant;
            public string DatabaseTypePreseed => null;
            public void BindToScratch(string scratch) => _tenant = $"Data Source={Path.Combine(scratch, "tenant.db")};";
            // This provider's own database, never the process-wide ClearAllPools.
            public void Dispose() => ScratchSqlite.Release(_tenant);
        }

        private sealed class PostgresProvider : ISeedProvider
        {
            private readonly string _db = "stomme_idpui_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string TenantConnectionString => PostgresTestEndpoint.ConnectionString(_db);
            public string DatabaseTypePreseed => null;
            public void BindToScratch(string scratch) { }
            public static bool IsReachable() => PostgresTestEndpoint.IsReachable();
            public void Dispose()
            {
                foreach (var db in new[] { _db, _db + "_OpenIddict" })
                {
                    PostgresTestEndpoint.DropDatabase(db);
                }
            }
        }

        private sealed class RavenProvider : ISeedProvider
        {
            private static string BaseUrl => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
            private readonly string _db = "stomme_idpui_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantConnectionString => $"Url={BaseUrl};Database={_db}";
            public string DatabaseTypePreseed => DatabaseType;
            public void BindToScratch(string scratch) { }
            public void Dispose()
            {
                try
                {
                    using var store = new DocumentStore { Urls = new[] { BaseUrl } }; store.Initialize();
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(new DeleteDatabasesOperation.Parameters
                    { DatabaseNames = new[] { _db, _db + "_OpenIddict" }, HardDelete = true }));
                }
                catch { }
            }
        }

        /// <summary>A throwaway content root whose override settings switch the authority on (or leave it off).</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string _previousOverridePath;

            public ScratchInstall(ISeedProvider provider, bool openIddictEnabled)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-idpui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject();
                if (openIddictEnabled)
                {
                    overrides["OpenIddict"] = new JsonObject
                    {
                        ["Enabled"] = true,
                        ["Applications"] = new JsonArray(new JsonObject
                        {
                            ["ClientId"] = SeededClientId,
                            ["ClientSecret"] = SeededSecret,
                            ["DisplayName"] = "Seeded From Config",
                            ["Type"] = "confidential",
                            ["GrantTypes"] = new JsonArray("authorization_code", "client_credentials"),
                            ["Scopes"] = new JsonArray("openid", "profile", "email", "api"),
                            ["RedirectUris"] = new JsonArray($"http://{TenantHost}/signin-oidc")
                        })
                    };
                }
                if (provider.DatabaseTypePreseed != null)
                {
                    overrides["Database"] = new JsonObject { ["DefaultDBType"] = provider.DatabaseTypePreseed };
                }

                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { }
            }
        }
    }
}
