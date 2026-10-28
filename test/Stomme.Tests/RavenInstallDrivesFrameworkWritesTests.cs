using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D54a — THE FIRST FRAMEWORK CLUSTER EVER DRIVEN ON RAVENDB THROUGH THE INSTALL PIPELINE.
    /// <para>
    /// Drives the real installer's stages against a live RavenDB (default 127.0.0.1:7000) up to — but NOT including — the
    /// host-account step, and asserts the master <c>Tenant</c> and <c>Alias</c> documents actually exist in the
    /// live database. That is the proof that the resolve-time factory selected RavenDB, the LAZY
    /// <c>IDocumentStore</c> was built, the bootstrap created the database, and the de-EF'd write path (D53)
    /// persisted to Raven rather than throwing on an unconfigured EF context.
    /// </para>
    /// <para>
    /// SCOPE. It stops after <c>CreateTenantAsync</c>. The next stage, <c>MigrateTenantsAsync</c>, still
    /// enumerates tenants through the EF <c>InstallationContext</c> (<c>db.Tenant.ToListAsync()</c>) and the
    /// host-account step needs the Raven <c>IUserStore</c> — both are D54b. So this does NOT complete a Raven
    /// install; it proves the framework WRITE lands, which is D54a's bar.
    /// </para>
    /// <para>
    /// GATED, and it FAILS rather than skips when RavenDB is unreachable — an EF-only or absent-server run would
    /// prove nothing, the same doctrine the conformance suite already enforces. Point it at a reachable server
    /// with <c>RAVENDB_TEST_URL</c> (default the local install at http://127.0.0.1:7000).
    /// </para>
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class RavenInstallDrivesFrameworkWritesTests
    {
        private const string RavenType = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server";
        // RavenDB endpoint, shared with the conformance suite via RAVENDB_TEST_URL. Default is the repo's
        // local RavenDB install (http://127.0.0.1:7000); the Docker container is gone (both stores are now
        // local Windows services: RavenDB on 7000, PostgreSQL postgresql-x64-18 on 127.0.0.1:5432). Override
        // the env var for any other server.
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private static Uri Endpoint => new(Url);
        private static string Host => Endpoint.Host;
        private static int Port => Endpoint.Port;

        private readonly ITestOutputHelper _output;

        public RavenInstallDrivesFrameworkWritesTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task Install_pipeline_writes_master_tenant_and_alias_documents_to_live_raven()
        {
            Assert.True(IsReachable(),
                $"RavenDB is not reachable at {Url}, so nothing here proves a framework write lands on Raven. " +
                "Start the local RavenDB instance (default http://127.0.0.1:7000) or point RAVENDB_TEST_URL at a reachable server.");

            var database = "stomme_d54_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var connectionString = $"Url={Url};Database={database}";

            using var scratch = new ScratchInstallation(RavenType, connectionString);

            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" });

            try
            {
                var manager = (DatabaseManager)app.Services.GetRequiredService<IDatabaseManager>();

                var install = new InstallConfig
                {
                    ConnectionString = connectionString,
                    TenantName = TenantNames.Master,
                    DatabaseType = RavenType,
                    IsNewTenant = true,
                    Aliases = "localhost",
                };

                // The three stages before the host-account step. Each is driven and asserted in turn, so a
                // failure names the stage that forced EF instead of hiding inside a single InstallAsync call.
                var created = await InvokeStageAsync(manager, "CreateDatabaseAsync", install);
                Assert.True(created.Success, $"CreateDatabaseAsync failed: {created.Message}");

                var migrated = await InvokeStageAsync(manager, "MigrateMasterAsync", install);
                Assert.True(migrated.Success, $"MigrateMasterAsync failed: {migrated.Message}");

                var tenantCreated = await InvokeStageAsync(manager, "CreateTenantAsync", install);
                Assert.True(tenantCreated.Success, $"CreateTenantAsync failed: {tenantCreated.Message}");

                // ---- THE PROOF: the documents physically exist in the live RavenDB. --------------------
                // Queried through an INDEPENDENT document store (not the host's, not the seam's cached
                // repositories) via the collection statistics, so a seam-level cache cannot fake a hit.
                using var verifier = (DocumentStore)new DocumentStore { Urls = [Url], Database = database }.Initialize();
                var collections = await verifier.Maintenance.SendAsync(new GetCollectionStatisticsOperation());

                _output.WriteLine($"[Raven {database}] collections: " +
                    string.Join(", ", collections.Collections.Select(kv => $"{kv.Key}={kv.Value}")));

                var tenantDocs = CollectionCount(collections, "Tenant");
                var aliasDocs = CollectionCount(collections, "Alias");

                Assert.True(tenantDocs >= 1,
                    "The master Tenant document was not written to RavenDB — the de-EF'd CreateTenantAsync write " +
                    "path did not persist through the Raven provider.");
                Assert.True(aliasDocs >= 1,
                    "The master Alias document was not written to RavenDB.");

                // And it is retrievable through the seam on the RavenDB provider — the read path the resolver
                // uses on every request.
                using var scope = app.Services.CreateScope();
                var tenants = await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync();
                var aliases = await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync();

                Assert.Contains(tenants, t => t.Name == TenantNames.Master);
                Assert.Contains(aliases, a => a.Name.Equals("localhost", StringComparison.OrdinalIgnoreCase));

                // Best-effort database cleanup.
                try
                {
                    await verifier.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
                }
                catch
                {
                    // disposable container; a leftover database is harmless
                }
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        private static async Task<Installation> InvokeStageAsync(DatabaseManager manager, string stage, InstallConfig install)
        {
            var method = typeof(DatabaseManager).GetMethod(stage, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (Task<Installation>)method!.Invoke(manager, new object[] { install })!;
            return await task;
        }

        private static long CollectionCount(Raven.Client.Documents.Operations.CollectionStatistics statistics, string contains) =>
            statistics.Collections
                .Where(kv => kv.Key.Contains(contains, StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);

        private static bool IsReachable()
        {
            try
            {
                using var tcp = new TcpClient();
                return tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch
            {
                return false;
            }
        }

        // A throwaway content root plus the override the installer's self-writes (InstallationId, connection
        // string, DefaultDBType) land in — so the repository's appsettings.json is never touched, exactly as the
        // render guard isolates itself. The override is also read LAST by BuildAppAsync, which is how the Raven
        // provider + connection string get into config in the first place.
        private sealed class ScratchInstallation : IDisposable
        {
            private readonly string? _previousOverridePath;

            public ScratchInstallation(string databaseType, string connectionString)
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-raven-d54-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));

                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = databaseType },
                    ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = connectionString },
                };

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
                try
                {
                    Directory.Delete(ContentRoot, true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
