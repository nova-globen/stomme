#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 5 (D110) - the Connection store's observable contract once, driven on EF (SQLite and PostgreSQL),
    /// RavenDB and File through the module's own startup: a connection round-trips with its endpoint, its settings JSON
    /// (VALUES asserted) and its credential field names (the JSON-list convention, values asserted), a UTC test instant;
    /// found by site and normalised name whatever the case; listed in name order; an update and a delete land. Fail-not-skip
    /// on the external stores (D46).
    /// </summary>
    public abstract class ConnectionStoreConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 27, 2, 3, 4, 567, DateTimeKind.Utc);
        private const int Site = 7;
        private const int OtherSite = 8;

        private ServiceProvider _services = default!;

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            services.AddScoped<Stomme.Infrastructure.Security.ISiteSecretStore, NoSecretStore>();
            new Stomme.Modules.Connectivity.Startup.ServerStartup().ConfigureServices(services);
            _services = services.BuildServiceProvider();
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        private async Task<T> InScopeAsync<T>(Func<IConnectionRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IConnectionRepository>());
        }

        private static ConnectionAggregate Connection(int siteId, string name, string type = "postgresql") =>
            ConnectionAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, name, type, ConnectorEndpointKind.ConnectionString, "Host=db;Database=" + name.Replace(' ', '_'),
                new Dictionary<string, string> { ["username"] = "app", ["ssl"] = "true" }, new[] { "password" }, 30, Now);

        [Fact]
        public async Task A_connection_round_trips_with_its_endpoint_settings_credential_fields_and_utc_instants()
        {
            var connection = Connection(Site, "Prod DB");
            connection.RecordTestResult(true, "ok", Now.AddMinutes(1));
            await InScopeAsync(async repository => { await repository.AddAsync(connection); return 0; });

            var read = await InScopeAsync(repository => repository.GetByIdAsync(connection.Id));

            Assert.NotNull(read);
            Assert.Equal(("Prod DB", "PROD DB", Site, "postgresql", 30), (read.Name, read.NameNormalized, read.SiteId, read.ConnectorType, read.TimeoutSeconds));
            Assert.Equal("Host=db;Database=Prod_DB", read.Endpoint.Value);
            Assert.Equal(new Dictionary<string, string> { ["ssl"] = "true", ["username"] = "app" }, read.Settings.GetSettings());
            Assert.Equal(new[] { "password" }, read.CredentialFields);
            Assert.Equal((ConnectionStatus.Healthy, Now.AddMinutes(1)), (read.Status, read.LastTestedUtc));
            Assert.Equal(DateTimeKind.Utc, read.LastTestedUtc!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, read.CreatedUtc.Kind);
            Assert.Equal(TenantId.Default, read.TenantId);
        }

        [Fact]
        public async Task A_site_finds_its_connection_by_name_whatever_the_case_and_not_another_sites()
        {
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(Connection(Site, "Warehouse"));
                await repository.AddAsync(Connection(OtherSite, "Warehouse"));
                return 0;
            });

            var mine = await InScopeAsync(repository => repository.FindByNameAsync(Site, " wareHOUSE "));
            Assert.NotNull(mine);
            Assert.Equal(Site, mine.SiteId);
            Assert.Null(await InScopeAsync(repository => repository.FindByNameAsync(Site, "Lake")));
        }

        [Fact]
        public async Task A_sites_connections_list_in_name_order_and_an_update_and_a_delete_land()
        {
            var zulu = Connection(Site, "Zulu");
            var alpha = Connection(Site, "alpha", "rest");
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(zulu);
                await repository.AddAsync(alpha);
                await repository.AddAsync(Connection(OtherSite, "Other"));
                return 0;
            });

            Assert.Equal(new[] { "alpha", "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(connection => connection.Name));

            await InScopeAsync(async repository =>
            {
                var read = (await repository.GetByIdAsync(zulu.Id))!;
                read.Reconfigure("Zulu", ConnectorEndpointKind.ConnectionString, "Host=db2;Database=zulu", new Dictionary<string, string> { ["username"] = "app2" }, new[] { "password", "username" }, 60, Now.AddMinutes(5));
                await repository.UpdateAsync(read);
                return 0;
            });
            var updated = (await InScopeAsync(repository => repository.GetByIdAsync(zulu.Id)))!;
            Assert.Equal(("Host=db2;Database=zulu", "app2", 60, ConnectionStatus.Untested), (updated.Endpoint.Value, updated.Settings.GetSettings()["username"], updated.TimeoutSeconds, updated.Status));
            Assert.Equal(new[] { "password", "username" }, updated.CredentialFields);

            await InScopeAsync(async repository => { await repository.DeleteAsync((await repository.GetByIdAsync(alpha.Id))!); return 0; });
            Assert.Null(await InScopeAsync(repository => repository.GetByIdAsync(alpha.Id)));
            Assert.Equal(new[] { "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(connection => connection.Name));
        }

        /// <summary>The startup registers the secrets over the site secret store; the store contract needs none.</summary>
        private sealed class NoSecretStore : Stomme.Infrastructure.Security.ISiteSecretStore
        {
            public Task SetAsync(int siteId, string referenceKey, string secret, CancellationToken ct = default) => throw new NotSupportedException();

            public Task<Stomme.Infrastructure.Security.SiteSecretResult> GetAsync(int siteId, string referenceKey, CancellationToken ct = default) => throw new NotSupportedException();

            public Task DeleteAsync(int siteId, string referenceKey, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }

    public sealed class SqliteConnectionStoreConformanceTests : ConnectionStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-connections-conformance-{Guid.NewGuid():N}.db")}";

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = _connectionString,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(new SqliteConnectionStringBuilder(_connectionString).DataSource);
            }
            catch (IOException)
            {
            }

            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresConnectionStoreConformanceTests : ConnectionStoreConformanceTests
    {
        private readonly string _database = "stomme_connections_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The Connection store conformance suite FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = PostgresTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString(_database)));
            PostgresTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenConnectionStoreConformanceTests : ConnectionStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private readonly string _database = "stomme-connections-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-connections-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server" })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
            }
        }
    }

    public sealed class FileConnectionStoreConformanceTests : ConnectionStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-connections-file-conformance-{Guid.NewGuid():N}");

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }

            return Task.CompletedTask;
        }
    }
}
