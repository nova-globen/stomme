using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Identity.OpenIddict;
using Stomme.Identity.OpenIddict.Raven;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stomme.Tests
{
    /// <summary>
    /// THE PER-TENANT OPENIDDICT AUTHORITY SPIKE, DRIVEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stands up the REAL Stomme host (<see cref="Program.BuildAppAsync"/> + the real middleware pipeline,
    /// only the transport swapped for TestServer) with OpenIddict co-hosted, installs TWO isolated tenants
    /// (two databases on two aliases/hosts), registers a DIFFERENT client-credentials client in each tenant's
    /// DEDICATED OpenIddict store, then drives the token endpoint over real HTTP for both and asserts the four
    /// claims:
    /// </para>
    /// <list type="number">
    /// <item><b>Co-hosting</b> — <c>/connect/token</c> is served from inside Stomme's Blazor SSR pipeline
    /// (no sibling process); a token is issued.</item>
    /// <item><b>Per-tenant issuer</b> — no <c>SetIssuer</c> is called, so the <c>iss</c> claim is the request
    /// host; the two tenants' distinct hosts yield distinct issuers for free.</item>
    /// <item><b>Per-tenant stores</b> — tenant A's client is INVISIBLE at tenant B: the same client_id is
    /// rejected (<c>invalid_client</c>) at B's host, because B's OpenIddict store is a physically different
    /// database routed by <see cref="TenantOpenIddictDbContext"/> per alias.</item>
    /// <item><b>Isolation, driven</b> — a token minted at A is REJECTED at B's protected resource and accepted
    /// at A's. The rejection is by OpenIddict's NATIVE issuer check (validation infers the expected issuer from
    /// the request host); the test output records the exact error.</item>
    /// </list>
    /// <para>
    /// PROVIDER DIMENSION. SQLite is the always-on case (two files = true per-tenant DB isolation, no external
    /// service). PostgreSQL runs additionally when reachable (shared test servers by default; see PostgresTestEndpoint). Unlike the
    /// render guard there is NO "must have a real async provider" assertion here, and that is deliberate:
    /// the isolation mechanism under test — per-alias store routing, host-inferred issuer, the validation
    /// issuer check — lives above the database and is provider-independent, so SQLite is genuine cover, not a
    /// stand-in. PostgreSQL is added as extra confidence on a real relational server, not because SQLite is
    /// insufficient.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class OpenIddictPerTenantAuthorityTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantAHost = "localhost";
        private const string TenantBHost = "tenant-b.localhost";
        private const string ClientA = "client-tenant-a";
        private const string ClientB = "client-tenant-b";
        private const string Secret = "S3cr3t!Client$2026";

        private readonly ITestOutputHelper _output;

        public OpenIddictPerTenantAuthorityTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { SqliteOidcProvider.ProviderName };

            if (PostgresOidcProvider.IsReachable())
            {
                yield return new object[] { PostgresOidcProvider.ProviderName };
            }

            // RavenDB is ALWAYS yielded — never conditional on reachability (the parity principle): an EF-only run
            // proves nothing about EF↔Raven parity, so an unreachable RavenDB must FAIL the test, not skip it. If
            // Raven is down, the tenant-A install (which bootstraps the Raven database) fails and the install
            // assertion below trips — a hard red, not a silent shrink.
            yield return new object[] { RavenOidcProvider.ProviderName };
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Two_tenants_get_isolated_authorities_over_real_http(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new ScratchOpenIddictInstallation(provider);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();

                // --- Install tenant A (the master install) on alias "localhost" -------------------------
                await InstallAsync(databaseManager, new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantAConnectionString,
                    Aliases = TenantAHost,
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = "hosta",
                    HostPassword = "R3nder!Guard$2026",
                    HostEmail = "a@example.com",
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }, "tenant A");

                // --- Install tenant B — a SECOND isolated tenant (new database) on a distinct host --------
                await InstallAsync(databaseManager, new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantBConnectionString,
                    Aliases = TenantBHost,
                    TenantName = "TenantB",
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = "hostb",
                    HostPassword = "R3nder!Guard$2026",
                    HostEmail = "b@example.com",
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }, "tenant B");

                await app.StartAsync();

                // --- Register a DIFFERENT client in each tenant's dedicated OpenIddict store -------------
                await SeedClientAsync(app, TenantAHost, ClientA);
                await SeedClientAsync(app, TenantBHost, ClientB);

                using var client = app.GetTestClient();

                // ==== Q1 + Q2: token issues at each host; issuer is the request host =====================
                var tokenA = await RequestTokenAsync(client, TenantAHost, ClientA, Secret);
                Assert.Equal(HttpStatusCode.OK, tokenA.Status);
                Assert.False(string.IsNullOrEmpty(tokenA.AccessToken),
                    $"[{provider.Name}] Tenant A did not issue a token. Body: {tokenA.Body}");

                var tokenB = await RequestTokenAsync(client, TenantBHost, ClientB, Secret);
                Assert.Equal(HttpStatusCode.OK, tokenB.Status);
                Assert.False(string.IsNullOrEmpty(tokenB.AccessToken),
                    $"[{provider.Name}] Tenant B did not issue a token. Body: {tokenB.Body}");

                var issA = ReadJwtClaim(tokenA.AccessToken, "iss");
                var issB = ReadJwtClaim(tokenB.AccessToken, "iss");
                _output.WriteLine($"[{provider.Name}][Q2] tenant A iss = {issA}");
                _output.WriteLine($"[{provider.Name}][Q2] tenant B iss = {issB}");

                Assert.Contains(TenantAHost, issA);
                Assert.Contains(TenantBHost, issB);
                Assert.NotEqual(issA, issB); // per-tenant issuer, inferred from host, for free

                // ==== Q3: tenant A's client is INVISIBLE to tenant B (store isolation) ===================
                var crossClient = await RequestTokenAsync(client, TenantBHost, ClientA, Secret);
                _output.WriteLine($"[{provider.Name}][Q3] clientA @ tenant B -> {(int)crossClient.Status} {crossClient.Body}");
                Assert.NotEqual(HttpStatusCode.OK, crossClient.Status);
                Assert.DoesNotContain("access_token", crossClient.Body);

                // sanity: the SAME client_id DOES work on its own tenant, so the rejection above is isolation,
                // not a broken client.
                var reissueA = await RequestTokenAsync(client, TenantAHost, ClientA, Secret);
                Assert.Equal(HttpStatusCode.OK, reissueA.Status);

                // ==== Q4: token minted at A is rejected at B's resource, accepted at A's =================
                var acceptedAtA = await CallWhoAmIAsync(client, TenantAHost, tokenA.AccessToken);
                _output.WriteLine($"[{provider.Name}][Q4] tokenA @ tenant A /whoami -> {(int)acceptedAtA}");
                Assert.Equal(HttpStatusCode.OK, acceptedAtA);

                var rejectedAtB = await CallWhoAmIAsync(client, TenantBHost, tokenA.AccessToken);
                _output.WriteLine($"[{provider.Name}][Q4] tokenA @ tenant B /whoami -> {(int)rejectedAtB} " +
                    "(native OpenIddict issuer check: the token's iss is not this host's inferred issuer)");
                Assert.NotEqual(HttpStatusCode.OK, rejectedAtB);

                // ==== A client-credentials token is valid at the authority but carries no PERSON, so it
                // signs no one in to api/* (until a service identity exists) - accepted above, anonymous here.
                var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"http://{TenantAHost}/api/User/authenticate");
                apiRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenA.AccessToken);
                var apiResponse = await client.SendAsync(apiRequest);
                var apiBody = await apiResponse.Content.ReadAsStringAsync();
                _output.WriteLine($"[{provider.Name}] client token @ api/User/authenticate -> {(int)apiResponse.StatusCode} {apiBody}");
                Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
                Assert.False(System.Text.Json.Nodes.JsonNode.Parse(apiBody)?["isAuthenticated"]?.GetValue<bool>());
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// Client_id lookup is CASE-SENSITIVE on EVERY provider, DRIVEN. Both an END-TO-END token
        /// endpoint assertion (all providers) AND a fix-sensitive STORE-LEVEL assertion (RavenDB).
        /// </summary>
        /// <remarks>
        /// <para>
        /// OAuth2/OIDC defines <c>client_id</c> as case-SENSITIVE (RFC 6749). <b>Checked and falsified:</b> there is NO observable EF-vs-Raven divergence at the token endpoint, because OpenIddict's
        /// <c>OpenIddictApplicationManager.FindByClientIdAsync</c> applies its OWN ordinal second pass ABOVE the
        /// store ("a second pass using string.Equals(StringComparison.Ordinal) is manually made here … enforced
        /// independently of the … collation used by the store"). So a mixed-case client_id is correctly rejected
        /// on EF <i>and</i> RavenDB even with the naive store — verified: this test's end-to-end half passes on
        /// all three providers with OR without the store fix.
        /// </para>
        /// <para>
        /// The store fix (an in-memory ordinal post-filter in <c>OpenIddictRavenApplicationStore</c>) is
        /// therefore DEFENSE-IN-DEPTH: it makes the Raven store's own contract case-sensitive rather than relying
        /// solely on the manager's guard (which OpenIddict lets callers disable via <c>DisableAdditionalFiltering</c>).
        /// To keep the fix non-vacuous, the RavenDB leg ALSO drives the store DIRECTLY (below the manager): with
        /// the fix, the wrong case MISSES and the exact case HITS at the store; without the fix the wrong case
        /// HITS at the store (RavenDB <c>==</c> is case-insensitive) — so this assertion goes red without the fix.
        /// </para>
        /// <para>Runs RavenDB unconditionally (fail-not-skip), like the sibling isolation test.</para>
        /// </remarks>
        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Client_id_lookup_is_case_sensitive_on_every_provider(string providerName)
        {
            const string mixedCaseClientId = "Casing-Test-Client";
            const string wrongCaseClientId = "casing-test-client";

            using var provider = ResolveProvider(providerName);
            using var scratch = new ScratchOpenIddictInstallation(provider);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();

                await InstallAsync(databaseManager, new InstallConfig
                {
                    DatabaseType = provider.DatabaseType,
                    ConnectionString = provider.TenantAConnectionString,
                    Aliases = TenantAHost,
                    TenantName = TenantNames.Master,
                    IsNewTenant = true,
                    SiteName = Constants.DefaultSite,
                    HostUsername = "hosta",
                    HostPassword = "R3nder!Guard$2026",
                    HostEmail = "a@example.com",
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }, "casing tenant");

                await app.StartAsync();

                await SeedClientAsync(app, TenantAHost, mixedCaseClientId);

                using var client = app.GetTestClient();

                // ---- End-to-end (all providers): exact case HITS, wrong case MISSES. This holds via
                //      OpenIddict's manager guard regardless of the store fix (the falsification above). --------
                var exact = await RequestTokenAsync(client, TenantAHost, mixedCaseClientId, Secret);
                _output.WriteLine($"[{provider.Name}] exact-case client_id -> {(int)exact.Status}");
                Assert.Equal(HttpStatusCode.OK, exact.Status);
                Assert.False(string.IsNullOrEmpty(exact.AccessToken),
                    $"[{provider.Name}] exact-case client_id did not issue a token. Body: {exact.Body}");

                var wrong = await RequestTokenAsync(client, TenantAHost, wrongCaseClientId, Secret);
                _output.WriteLine($"[{provider.Name}] wrong-case client_id -> {(int)wrong.Status} {wrong.Body}");
                Assert.NotEqual(HttpStatusCode.OK, wrong.Status);
                Assert.DoesNotContain("access_token", wrong.Body);

                // ---- Store level (RavenDB): the fix-sensitive gate. Resolve the RavenDB OpenIddict application
                //      store directly (below the manager) in a warmed tenant scope and assert IT is case-sensitive.
                //      Without the fix this MISSES/HITS pair flips (wrong case returns the doc). --------------
                if (providerName == RavenOidcProvider.ProviderName)
                {
                    using var scope = app.Services.CreateScope();
                    var sp = scope.ServiceProvider;
                    var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantAHost);
                    await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

                    var store = sp.GetRequiredService<IOpenIddictApplicationStore<OpenIddictRavenApplication>>();
                    var wrongCaseAtStore = await store.FindByClientIdAsync(wrongCaseClientId, CancellationToken.None);
                    var exactCaseAtStore = await store.FindByClientIdAsync(mixedCaseClientId, CancellationToken.None);
                    _output.WriteLine($"[{provider.Name}][store] wrong-case -> {(wrongCaseAtStore is null ? "null" : "FOUND")}; " +
                        $"exact-case -> {(exactCaseAtStore is null ? "null" : "FOUND")}");
                    Assert.Null(wrongCaseAtStore);   // fix-sensitive: without the fix the case-insensitive Raven query returns the doc
                    Assert.NotNull(exactCaseAtStore);
                }
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
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

        /// <summary>
        /// Registers a client-credentials application in <paramref name="aliasHost"/>'s tenant OpenIddict store,
        /// resolving the tenant off the alias exactly as a request would, then creating the OpenIddict schema
        /// (EnsureCreated) and the client through the real <see cref="IOpenIddictApplicationManager"/>.
        /// </summary>
        private static async Task SeedClientAsync(WebApplication app, string aliasHost, string clientId)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var aliasRepository = sp.GetRequiredService<IAliasRepository>();
            Alias alias = await aliasRepository.GetAliasAsync(aliasHost);
            if (alias == null)
            {
                throw new InvalidOperationException($"No alias resolved for host '{aliasHost}' — the tenant was not installed.");
            }

            // Establish (and WARM) the tenant for this scope, so TenantOpenIddictDbContext.OnConfiguring routes
            // to this tenant's dedicated OpenIddict database — the same warm the request path performs.
            var tenantManager = sp.GetRequiredService<ITenantManager>();
            await tenantManager.SetAliasAsync(alias);

            // Relational: create the per-tenant OpenIddict schema (EnsureCreated). RavenDB: the context is not
            // registered on a document install — the per-tenant OpenIddict database is created lazily by the
            // session provider on the first store write below, so nothing to do here.
            var context = sp.GetService<TenantOpenIddictDbContext>();
            if (context != null)
            {
                await context.Database.EnsureCreatedAsync();
            }

            var applications = sp.GetRequiredService<IOpenIddictApplicationManager>();
            await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientSecret = Secret,
                ClientType = ClientTypes.Confidential,
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + "api"
                }
            });
        }

        private static async Task<TokenResult> RequestTokenAsync(HttpClient client, string host, string clientId, string secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://{host}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = secret,
                    ["scope"] = "api"
                })
            };

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            string? accessToken = null;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var json = JsonNode.Parse(body);
                accessToken = json?["access_token"]?.GetValue<string>();
            }

            return new TokenResult(response.StatusCode, body, accessToken!);
        }

        private static async Task<HttpStatusCode> CallWhoAmIAsync(HttpClient client, string host, string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/connect/whoami");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await client.SendAsync(request);
            return response.StatusCode;
        }

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
            var node = JsonNode.Parse(json);
            return node?[claim]?.ToString();
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        private static IOidcProvider ResolveProvider(string name) => name switch
        {
            SqliteOidcProvider.ProviderName => new SqliteOidcProvider(),
            PostgresOidcProvider.ProviderName => new PostgresOidcProvider(),
            RavenOidcProvider.ProviderName => new RavenOidcProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        private readonly record struct TokenResult(HttpStatusCode Status, string Body, string AccessToken);

        /// <summary>
        /// Supplies the four databases a two-tenant OpenIddict drive needs: tenant A's and tenant B's own
        /// databases, plus each tenant's DEDICATED OpenIddict database (keyed {tenant.DBConnectionString}
        /// _OpenIddict). Connection strings are set once per instance so the scratch override and the installs
        /// agree.
        /// </summary>
        public interface IOidcProvider : IDisposable
        {
            string Name { get; }
            string DatabaseType { get; }
            string TenantAConnectionString { get; }
            string TenantBConnectionString { get; }
            string OpenIddictAConnectionString { get; }
            string OpenIddictBConnectionString { get; }

            /// <summary>
            /// The provider type to pre-seed into <c>Database:DefaultDBType</c> BEFORE the host is built, or null
            /// to leave it at the fresh-boot default. OpenIddict picks its store provider at COMPOSITION
            /// time (see <c>StommeOpenIddictExtensions</c>), so a RavenDB run must declare the Raven provider
            /// before <c>BuildAppAsync</c> or the EF store branch would be baked in; the relational providers rely
            /// on the default and return null.
            /// </summary>
            string? DatabaseTypePreseed { get; }

            /// <summary>Called once the scratch directory exists, for providers that key files off it.</summary>
            void BindToScratch(string scratchDirectory);
        }

        private sealed class SqliteOidcProvider : IOidcProvider
        {
            public const string ProviderName = "SQLite";

            private string _tenantA = null!, _tenantB = null!, _oidcA = null!, _oidcB = null!;

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantAConnectionString => _tenantA;
            public string TenantBConnectionString => _tenantB;
            public string OpenIddictAConnectionString => _oidcA;
            public string OpenIddictBConnectionString => _oidcB;
            public string? DatabaseTypePreseed => null; // relational: the EF OpenIddict branch is the composition default

            public void BindToScratch(string scratch)
            {
                _tenantA = $"Data Source={Path.Combine(scratch, "tenant-a.db")};";
                _tenantB = $"Data Source={Path.Combine(scratch, "tenant-b.db")};";
                _oidcA = $"Data Source={Path.Combine(scratch, "oidc-a.db")};";
                _oidcB = $"Data Source={Path.Combine(scratch, "oidc-b.db")};";
            }

            public void Dispose()
            {
                // Microsoft.Data.Sqlite pools by connection string; release the four files so the scratch tree
                // can be deleted — BY THOSE FOUR STRINGS. The process-wide ClearAllPools that used to stand here
                // also released every other suite's pool, mid-request.
                ScratchSqlite.Release(_tenantA);
                ScratchSqlite.Release(_tenantB);
                ScratchSqlite.Release(_oidcA);
                ScratchSqlite.Release(_oidcB);
            }
        }

        private sealed class PostgresOidcProvider : IOidcProvider
        {
            public const string ProviderName = "PostgreSQL";

            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string TenantAConnectionString => Build($"stomme_oidc_ta_{_suffix}");
            public string TenantBConnectionString => Build($"stomme_oidc_tb_{_suffix}");
            public string OpenIddictAConnectionString => Build($"stomme_oidc_oa_{_suffix}");
            public string OpenIddictBConnectionString => Build($"stomme_oidc_ob_{_suffix}");
            public string? DatabaseTypePreseed => null; // relational: the EF OpenIddict branch is the composition default

            public void BindToScratch(string scratch) { /* PostgreSQL keys off a guid, not the scratch dir */ }

            private string Build(string database)
            {
                if (!_databases.Contains(database))
                {
                    _databases.Add(database);
                }
                return PostgresTestEndpoint.ConnectionString(database);
            }

            public static bool IsReachable() => PostgresTestEndpoint.IsReachable();

            public void Dispose()
            {
                // Force-drop the four throwaway databases this run created (the installs create the tenant DBs;
                // EnsureCreated creates the OpenIddict DBs). Best-effort: a leftover DB is harmless.
                foreach (var database in _databases)
                {
                    PostgresTestEndpoint.DropDatabase(database);
                }
            }
        }

        /// <summary>
        /// The RavenDB provider. Tenant A/B each get their own RavenDB database, and each tenant's
        /// dedicated OpenIddict database is a SEPARATE RavenDB database again — the document-store analogue of
        /// the per-tenant relational databases, so tenant A's clients/tokens are physically invisible at tenant
        /// B. Connection strings are RavenDB descriptors (<c>Url=…;Database=…</c>) against the local server on
        /// <c>RAVENDB_TEST_URL</c> (default <c>http://127.0.0.1:7000</c>). Unlike PostgreSQL this provider is
        /// yielded UNCONDITIONALLY: an unreachable RavenDB fails the install (and thus the test), never skips.
        /// </summary>
        private sealed class RavenOidcProvider : IOidcProvider
        {
            public const string ProviderName = "RavenDB";

            private static string BaseUrl =>
                Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

            private readonly string _suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            private readonly List<string> _databases = new();

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantAConnectionString => Build($"stomme_oidc_ta_{_suffix}");
            public string TenantBConnectionString => Build($"stomme_oidc_tb_{_suffix}");
            public string OpenIddictAConnectionString => Build($"stomme_oidc_oa_{_suffix}");
            public string OpenIddictBConnectionString => Build($"stomme_oidc_ob_{_suffix}");
            // Raven: OpenIddict chooses its store provider at composition time, so the provider must be declared
            // before the host is built.
            public string DatabaseTypePreseed => DatabaseType;

            public void BindToScratch(string scratch) { /* RavenDB keys off a guid + server URL, not the scratch dir */ }

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
                // Force-drop the four throwaway RavenDB databases this run created (two tenant DBs; the install
                // bootstraps those, and the OpenIddict session provider bootstraps the two OpenIddict DBs).
                // Best-effort: a leftover DB is harmless and the stomme-db-up skill also cleans them.
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
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        /// <summary>
        /// A throwaway content root. The per-tenant OpenIddict connection strings (keyed
        /// {tenant.DBConnectionString}_OpenIddict) and the OpenIddict:Enabled gate are pre-seeded into the
        /// override file; the installer's own writes (DefaultConnection, the tenant connection strings) also land
        /// there via STOMME_APPSETTINGS_PATH and never touch the repository's appsettings.json.
        /// </summary>
        private sealed class ScratchOpenIddictInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchOpenIddictInstallation(IOidcProvider provider)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-oidc-" + Guid.NewGuid().ToString("N"));
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
                        // Tenant A's DBConnectionString key is "DefaultConnection" (master); tenant B's is its
                        // name "TenantB". The OpenIddict store resolves {key}_OpenIddict.
                        ["DefaultConnection_OpenIddict"] = provider.OpenIddictAConnectionString,
                        ["TenantB_OpenIddict"] = provider.OpenIddictBConnectionString
                    }
                };

                // OpenIddict picks EF vs Raven stores at COMPOSITION time from Database:DefaultDBType, so a
                // RavenDB run must declare the provider before the host is built. Relational runs leave it unset
                // (null) and inherit the EF branch as the default.
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
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
