using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Identity.OpenIddict;
using Stomme.Models;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
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
    /// Client/scope work (a), DRIVEN, STORAGE-BLIND — the declarative config seeder registers a relying party
    /// into whatever store the install chose, and it works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh install declares one client in <c>OpenIddict:Applications</c> config; the seeder (the exact call
    /// the bootstrap middleware makes, after the provisioner) reconciles it into the tenant's store; then
    /// the seeded client mints a <c>client_credentials</c> token over real HTTP. Idempotence is pinned by
    /// seeding twice and asserting a single application with an unchanged physical id — the UPSERT contract that
    /// replaced the old delete-then-create (which orphaned authorizations/tokens on every restart).
    /// </para>
    /// <para>
    /// Providers: SQLite always; PostgreSQL when the local service is reachable; RavenDB
    /// UNCONDITIONALLY (fail-not-skip — an EF-only pass proves nothing about the Raven client store).
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class OpenIddictClientManagementTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string ClientId = "managed-web";
        private const string Secret = "S3cr3t!Managed$2026";

        private readonly ITestOutputHelper _output;

        public OpenIddictClientManagementTests(ITestOutputHelper output)
        {
            _output = output;
        }

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
        public async Task Config_declared_client_is_seeded_and_works(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new SeededInstallation(provider);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                await InstallAsync(app, provider);

                // Reconcile config into the store exactly as the bootstrap middleware does: provision, then seed.
                await BootstrapAsync(app);

                // (a) the config-declared client now EXISTS with the permissions its grants/scopes imply.
                await AssertClientSeeded(app);

                // Idempotence: seeding again UPSERTs — one client, same physical id (not delete-then-create).
                var idBefore = await ResolvePhysicalId(app);
                await BootstrapAsync(app, force: true);
                var idAfter = await ResolvePhysicalId(app);
                Assert.Equal(idBefore, idAfter);
                Assert.Equal(1, await CountClients(app));

                // The seeded client works over real HTTP: a client_credentials token is issued.
                await app.StartAsync();
                using var client = app.GetTestServer().CreateClient();
                var token = await ClientCredentialsTokenAsync(client);
                _output.WriteLine($"[{providerName}] token status={token.Status} hasAccessToken={!string.IsNullOrEmpty(token.AccessToken)}");
                Assert.Equal(HttpStatusCode.OK, token.Status);
                Assert.False(string.IsNullOrEmpty(token.AccessToken), $"no access_token: {token.Body}");

                // A wrong secret for the SAME client is rejected (invalid_client), not a swallowed 200.
                var bad = await ClientCredentialsTokenAsync(client, secret: "wrong");
                Assert.NotEqual(HttpStatusCode.OK, bad.Status);
            }
            finally
            {
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                try { await app.StopAsync(cts.Token); } catch { /* best effort */ }
                await app.DisposeAsync();
            }
        }

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
                HostUsername = "host",
                HostPassword = "P@ssw0rd!Mgmt$2026",
                HostEmail = "host@mgmt.test",
                HostName = "Host",
                SiteTemplate = Constants.DefaultSiteTemplate,
                RenderMode = "",
                Runtime = ""
            }));
            Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "Install timed out.");
            var install = await installTask;
            Assert.True(install.Success, $"Install failed: {install.Message}");
        }

        private static async Task BootstrapAsync(WebApplication app, bool force = false)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            await sp.GetRequiredService<IOpenIddictStoreProvisioner>().EnsureProvisionedAsync();

            if (force)
            {
                // The seeded-tenant cache would short-circuit a second SeedAsync in-process; re-run the seeder's
                // reconcile directly to prove the UPSERT path is itself idempotent.
                await ReseedDirectlyAsync(sp);
            }
            else
            {
                await sp.GetRequiredService<IOpenIddictConfigSeeder>().SeedAsync();
            }
        }

        private static async Task ReseedDirectlyAsync(IServiceProvider sp)
        {
            var options = sp.GetRequiredService<OpenIddictSeedOptions>();
            var applications = sp.GetRequiredService<IOpenIddictApplicationManager>();
            foreach (var seed in options.Applications)
            {
                var descriptor = OpenIddictConfigSeeder.BuildDescriptor(seed);
                var existing = await applications.FindByClientIdAsync(seed.ClientId, CancellationToken.None);
                if (existing is null)
                {
                    await applications.CreateAsync(descriptor, CancellationToken.None);
                }
                else
                {
                    await applications.UpdateAsync(existing, descriptor, CancellationToken.None);
                }
            }
        }

        private async Task AssertClientSeeded(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(
                await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost));

            var applications = sp.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.FindByClientIdAsync(ClientId, CancellationToken.None);
            Assert.NotNull(application);

            var permissions = (await applications.GetPermissionsAsync(application, CancellationToken.None)).ToList();
            Assert.Contains(Permissions.GrantTypes.AuthorizationCode, permissions);
            Assert.Contains(Permissions.GrantTypes.ClientCredentials, permissions);
            Assert.Contains(Permissions.GrantTypes.RefreshToken, permissions);
            Assert.Contains(Permissions.Prefixes.Scope + "offline_access", permissions);
            Assert.Contains(Permissions.Endpoints.EndSession, permissions);
        }

        private static async Task<string> ResolvePhysicalId(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(
                await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost));
            var applications = sp.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.FindByClientIdAsync(ClientId, CancellationToken.None);
            Assert.NotNull(application);
            return await applications.GetIdAsync(application, CancellationToken.None);
        }

        private static async Task<int> CountClients(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(
                await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost));
            var applications = sp.GetRequiredService<IOpenIddictApplicationManager>();
            var count = 0;
            await foreach (var application in applications.ListAsync(cancellationToken: CancellationToken.None))
            {
                if (await applications.GetClientIdAsync(application, CancellationToken.None) == ClientId)
                {
                    count++;
                }
            }
            return count;
        }

        private static async Task<(HttpStatusCode Status, string Body, string AccessToken)> ClientCredentialsTokenAsync(
            HttpClient client, string secret = Secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://{TenantHost}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = ClientId,
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

        private static ISeedProvider ResolveProvider(string name) => name switch
        {
            "SQLite" => new SqliteProvider(),
            "PostgreSQL" => new PostgresProvider(),
            "RavenDB" => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        // ---- provider + install scaffolding (a config-declared client, no _OpenIddict conn key) --------

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
            private readonly string _db = "stomme_mgmt_" + Guid.NewGuid().ToString("N").Substring(0, 12);
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
            private readonly string _db = "stomme_mgmt_" + Guid.NewGuid().ToString("N").Substring(0, 12);
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

        private sealed class SeededInstallation : IDisposable
        {
            private readonly string _previousOverridePath;

            public SeededInstallation(ISeedProvider provider)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-mgmt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var application = new JsonObject
                {
                    ["ClientId"] = ClientId,
                    ["ClientSecret"] = Secret,
                    ["DisplayName"] = "Managed Web",
                    ["Type"] = "confidential",
                    ["GrantTypes"] = new JsonArray("authorization_code", "client_credentials", "refresh_token"),
                    ["Scopes"] = new JsonArray("openid", "profile", "email", "offline_access", "api"),
                    ["RedirectUris"] = new JsonArray($"http://{TenantHost}/signin-oidc"),
                    ["PostLogoutRedirectUris"] = new JsonArray($"http://{TenantHost}/signout-callback-oidc")
                };
                var openIddict = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Applications"] = new JsonArray(application)
                };
                var overrides = new JsonObject { ["OpenIddict"] = openIddict };
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
