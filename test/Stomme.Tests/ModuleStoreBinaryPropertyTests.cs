#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-263 (infra): does the module store hold binary data? A <c>byte[]</c> property on a module aggregate,
    /// written and read back in separate scopes, byte for byte, on the relational store (SQLite), RavenDB and the File
    /// store. What it pins is the SMALL-value case the module-persistence guide allows - a thumbnail, a hash, a key - not
    /// files, which belong in the site's folders (stomme-store-files).
    /// </summary>
    public abstract class ModuleStoreBinaryPropertyTests : IAsyncLifetime
    {
        public sealed class BinaryProbe : AggregateRoot<Guid>
        {
            private BinaryProbe()
            {
            }

            public byte[] Payload { get; private set; } = [];

            public byte[]? Optional { get; private set; }

            public static BinaryProbe Create(byte[] payload) => new() { Id = Guid.NewGuid(), Payload = payload };
        }

        private ServiceProvider _services = null!;

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            services.AddModuleAggregatePersistence<BinaryProbe, Guid>();
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

        [Fact]
        public async Task A_byte_array_round_trips_byte_for_byte_and_a_null_one_stays_null()
        {
            // Every byte value, plus a run long enough that no store could pass it by truncating to a short column.
            var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 31 % 256)).ToArray();
            var probe = BinaryProbe.Create(payload);
            await using (var scope = _services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IRepository<BinaryProbe, Guid>>().AddAsync(probe);
            }

            await using (var scope = _services.CreateAsyncScope())
            {
                var read = await scope.ServiceProvider.GetRequiredService<IRepository<BinaryProbe, Guid>>().GetByIdAsync(probe.Id);
                Assert.NotNull(read);
                Assert.Equal(payload, read!.Payload);
                Assert.Null(read.Optional);
            }
        }
    }

    public sealed class SqliteModuleStoreBinaryPropertyTests : ModuleStoreBinaryPropertyTests
    {
        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-module-binary-{Guid.NewGuid():N}.db")};Pooling=True";

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
                // a leftover temp file is not worth a red run
            }

            return Task.CompletedTask;
        }
    }

    public sealed class FileModuleStoreBinaryPropertyTests : ModuleStoreBinaryPropertyTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-module-binary-file-{Guid.NewGuid():N}");

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
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // a leftover temp folder is not worth a red run
            }

            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresModuleStoreBinaryPropertyTests : ModuleStoreBinaryPropertyTests
    {
        private readonly string _database = "stomme_module_binary_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT).");
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
    public sealed class SqlServerModuleStoreBinaryPropertyTests : ModuleStoreBinaryPropertyTests
    {
        private readonly string _database = "stomme_module_binary_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = SqlServerTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearPool(new Microsoft.Data.SqlClient.SqlConnection(SqlServerTestEndpoint.ConnectionString(_database)));
            SqlServerTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenModuleStoreBinaryPropertyTests : ModuleStoreBinaryPropertyTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-module-binary-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-module-binary-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
                })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            // Delete through a FRESH store: the container already disposed the shared one.
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // a leftover test database is swept by the next run
            }
        }
    }
}
