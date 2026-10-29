using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Models;
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
    /// THE OPENIDDICT STORE MUST PROVISION ITSELF, STORAGE-BLIND.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-tenant OpenIddict store used to be provisioned ONLY by the test suite: the sibling authority
    /// suites pre-seed the <c>{tenant}_OpenIddict</c> connection strings into their scratch config and call
    /// <c>EnsureCreated</c> themselves, while NOTHING in <c>src/</c> or <c>setup/</c> does either — so the
    /// suites were green while a deployed install threw
    /// <c>"no per-tenant OpenIddict connection string was found under ConnectionStrings:…_OpenIddict"</c> on
    /// the first store touch (driven on a real SQLite install; a class of green-gate
    /// broken-product). This suite pins the fix by reproducing PRODUCTION conditions: <b>zero</b>
    /// <c>_OpenIddict</c> keys, <b>zero</b> test-side <c>EnsureCreated</c>.
    /// </para>
    /// <para>
    /// Provider dimension: SQLite always; PostgreSQL when the local service is reachable; RavenDB
    /// UNCONDITIONALLY — an unreachable RavenDB fails the run, never skips it, because an EF-only pass proves
    /// nothing about the Raven authority path.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class OpenIddictStoreProvisioningTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string ClientId = "bl55-zero-config-client";
        private const string Secret = "S3cr3t!Bl55$2026";

        private readonly ITestOutputHelper _output;

        public OpenIddictStoreProvisioningTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---------------------------------------------------------------------------------------------
        // Derivation unit tests — the connection-string shapes the three relational providers + RavenDB
        // actually produce, plus the fail-closed edges.
        // ---------------------------------------------------------------------------------------------

        [Theory]
        // SQLite: the suffix lands on the file stem, path preserved.
        [InlineData(@"Data Source=C:\data\stomme.db;", @"Data Source=C:\data\stomme_OpenIddict.db;")]
        // PostgreSQL: the suffix lands on Database=, every other part preserved verbatim.
        [InlineData("Server=127.0.0.1;Port=5432;Database=stomme;User Id=postgres;Password=x;",
                    "Server=127.0.0.1;Port=5432;Database=stomme_OpenIddict;User Id=postgres;Password=x;")]
        // SQL Server / LocalDB: Initial Catalog wins over Data Source (which names the SERVER here).
        [InlineData(@"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog=Stomme;Integrated Security=SSPI;",
                    @"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog=Stomme_OpenIddict;Integrated Security=SSPI;")]
        // RavenDB descriptor: same Database= branch, URL untouched.
        [InlineData("Url=http://127.0.0.1:7000;Database=stomme_x",
                    "Url=http://127.0.0.1:7000;Database=stomme_x_OpenIddict;")]
        public void Derives_a_dedicated_sibling_store(string tenant, string expected)
        {
            Assert.Equal(expected, OpenIddictConnectionStrings.DeriveDedicatedStore(tenant));
        }

        [Theory]
        // In-memory SQLite: nothing durable to site an authority store in.
        [InlineData("Data Source=:memory:")]
        // A server with no database name: deriving would have to invent one.
        [InlineData("Data Source=tcp:server.example,1433")]
        public void Underivable_shapes_fail_closed_with_the_explicit_key_escape_hatch(string tenant)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => OpenIddictConnectionStrings.DeriveDedicatedStore(tenant));
            Assert.Contains("_OpenIddict", exception.Message);
        }

        // ---------------------------------------------------------------------------------------------
        // The integration pin: a REAL install with ZERO manual provisioning issues a token.
        // ---------------------------------------------------------------------------------------------

        public static IEnumerable<object[]> Providers()
        {
            yield return new object[] { SqliteProvider.ProviderName };

            if (PostgresProvider.IsReachable())
            {
                yield return new object[] { PostgresProvider.ProviderName };
            }

            // RavenDB is ALWAYS yielded: unreachable RavenDB = a hard red, never a silent shrink.
            yield return new object[] { RavenProvider.ProviderName };
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public async Task Token_issued_with_zero_manual_provisioning(string providerName)
        {
            using var provider = ResolveProvider(providerName);
            using var scratch = new ZeroConfigInstallation(provider);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
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
                    HostPassword = "P@ssw0rd!Bl55$2026",
                    HostEmail = "host@bl55.test",
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    RenderMode = "",
                    Runtime = ""
                }));
                var completed = await Task.WhenAny(installTask, Task.Delay(InstallTimeout));
                Assert.True(completed == installTask, "Install timed out.");
                var install = await installTask;
                _output.WriteLine($"[install] Success={install.Success} Message={install.Message}");
                Assert.True(install.Success, $"Install failed: {install.Message}");

                // Register a client through the PRODUCTION path a request-scoped registration takes:
                // establish the tenant, run the PRODUCTION provisioner (what the middleware calls), then the
                // real application manager. NO test-side EnsureCreated, NO _OpenIddict connection string.
                using (var scope = app.Services.CreateScope())
                {
                    var sp = scope.ServiceProvider;
                    var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                        ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
                    await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

                    await sp.GetRequiredService<IOpenIddictStoreProvisioner>().EnsureProvisionedAsync();

                    await sp.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
                        new OpenIddictApplicationDescriptor
                        {
                            ClientId = ClientId,
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

                await app.StartAsync();
                using var client = app.GetTestServer().CreateClient();

                // The pin: a client-credentials token over real HTTP, backed by a store nobody configured.
                var token = await PostTokenAsync(client, ClientId, Secret);
                _output.WriteLine($"[token] {token.Status} {Truncate(token.Body)}");
                Assert.Equal(HttpStatusCode.OK, token.Status);
                Assert.False(string.IsNullOrEmpty(token.AccessToken), $"No access_token in: {token.Body}");

                // Negative control — an UNKNOWN client must get OpenIddict's own invalid_client error, not
                // the swallowed empty 200 the missing store used to produce (an earlier observed symptom).
                var unknown = await PostTokenAsync(client, "no-such-client", "wrong");
                _output.WriteLine($"[unknown-client] {unknown.Status} {Truncate(unknown.Body)}");
                Assert.NotEqual(HttpStatusCode.OK, unknown.Status);
                Assert.Contains(Errors.InvalidClient, unknown.Body);

                provider.AssertDerivedStore(_output, scratch.ContentRoot);
            }
            finally
            {
                using var shutdown = new System.Threading.CancellationTokenSource(ShutdownTimeout);
                try { await app.StopAsync(shutdown.Token); } catch { /* best effort */ }
                await app.DisposeAsync();
            }
        }

        private static IZeroConfigProvider ResolveProvider(string name) => name switch
        {
            SqliteProvider.ProviderName => new SqliteProvider(),
            PostgresProvider.ProviderName => new PostgresProvider(),
            RavenProvider.ProviderName => new RavenProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown provider.")
        };

        private static async Task<(HttpStatusCode Status, string Body, string? AccessToken)> PostTokenAsync(
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
            string? accessToken = null;
            try
            {
                accessToken = JsonSerializer.Deserialize<JsonObject>(body)?["access_token"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                // non-JSON body (the failure cases assert on Status/Body, not the token)
            }

            return (response.StatusCode, body, accessToken);
        }

        private static string Truncate(string value) =>
            string.IsNullOrEmpty(value) ? "<empty>" : value.Length <= 300 ? value : value.Substring(0, 300) + "…";

        // ---------------------------------------------------------------------------------------------
        // Providers — ONE tenant each (isolation is the sibling suites' claim; this one pins provisioning),
        // and deliberately NO OpenIddict connection strings anywhere.
        // ---------------------------------------------------------------------------------------------

        private interface IZeroConfigProvider : IDisposable
        {
            string Name { get; }
            string DatabaseType { get; }
            string TenantConnectionString { get; }

            /// <summary>Database:DefaultDBType preseed (composition-time store selection), or null.</summary>
            string? DatabaseTypePreseed { get; }

            void BindToScratch(string scratchDirectory);

            /// <summary>Asserts the DERIVED dedicated store actually exists where derivation says it should.</summary>
            void AssertDerivedStore(ITestOutputHelper output, string scratchDirectory);
        }

        private sealed class SqliteProvider : IZeroConfigProvider
        {
            public const string ProviderName = "SQLite";

            private string _tenant = null!;

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server";
            public string TenantConnectionString => _tenant;
            public string? DatabaseTypePreseed => null;

            public void BindToScratch(string scratch)
            {
                _tenant = $"Data Source={Path.Combine(scratch, "tenant.db")};";
            }

            public void AssertDerivedStore(ITestOutputHelper output, string scratch)
            {
                var derived = Path.Combine(scratch, "tenant_OpenIddict.db");
                output.WriteLine($"[derived store] {derived} exists={System.IO.File.Exists(derived)}");
                Assert.True(System.IO.File.Exists(derived), $"The derived SQLite store {derived} was not created.");
            }

            public void Dispose()
            {
                // The tenant store AND the store the app DERIVED from it — both by their exact connection
                // strings, since the pool is keyed by the string and a near-miss releases nothing.
                // The derivation is the product's own, so the two can never drift apart here.
                ScratchSqlite.Release(_tenant);
                if (!string.IsNullOrEmpty(_tenant))
                {
                    ScratchSqlite.Release(OpenIddictConnectionStrings.DeriveDedicatedStore(_tenant));
                }
            }
        }

        private sealed class PostgresProvider : IZeroConfigProvider
        {
            public const string ProviderName = "PostgreSQL";

            private readonly string _database = "stomme_bl55_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            public string TenantConnectionString => PostgresTestEndpoint.ConnectionString(_database);
            public string? DatabaseTypePreseed => null;

            public void BindToScratch(string scratch) { /* keys off a guid, not the scratch dir */ }

            public void AssertDerivedStore(ITestOutputHelper output, string scratch)
            {
                // The token round-trip already proved the derived database serves reads+writes; record its name.
                output.WriteLine($"[derived store] {_database}_OpenIddict (proven by the token round-trip)");
            }

            public static bool IsReachable() => PostgresTestEndpoint.IsReachable();

            public void Dispose()
            {
                // Drop the tenant database AND the derived _OpenIddict sibling. Best effort.
                foreach (var database in new[] { _database, _database + "_OpenIddict" })
                {
                    PostgresTestEndpoint.DropDatabase(database);
                }
            }
        }

        private sealed class RavenProvider : IZeroConfigProvider
        {
            public const string ProviderName = "RavenDB";

            private static string BaseUrl =>
                Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

            private readonly string _database = "stomme_bl55_" + Guid.NewGuid().ToString("N").Substring(0, 12);

            public string Name => ProviderName;
            public string DatabaseType => "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
            public string TenantConnectionString => $"Url={BaseUrl};Database={_database}";
            public string? DatabaseTypePreseed => DatabaseType;

            public void BindToScratch(string scratch) { /* keys off a guid + server URL */ }

            public void AssertDerivedStore(ITestOutputHelper output, string scratch)
            {
                output.WriteLine($"[derived store] {_database}_OpenIddict (proven by the token round-trip)");
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
                            DatabaseNames = new[] { _database, _database + "_OpenIddict" },
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
        /// A throwaway content root whose override file carries ONLY <c>OpenIddict:Enabled</c> (and, for
        /// RavenDB, the composition-time provider preseed) — deliberately NO <c>_OpenIddict</c>
        /// connection strings, because production never has them. The installer's own writes land here via
        /// the override env var and never touch the repository's appsettings.json.
        /// </summary>
        private sealed class ZeroConfigInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ZeroConfigInstallation(IZeroConfigProvider provider)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-bl55-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                provider.BindToScratch(ContentRoot);

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["OpenIddict"] = new JsonObject { ["Enabled"] = true }
                };
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
