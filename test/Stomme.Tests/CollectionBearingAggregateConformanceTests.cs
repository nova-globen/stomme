using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Fixtures.CollectionAggregate;
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
    /// A COLLECTION-BEARING storage-blind aggregate (one collection of each element
    /// shape the notifications aggregates carry: enum, string, keyless record, record with a string value object,
    /// Entity&lt;Guid&gt; child with a UTC DateTime) round-trips member for member on EVERY module-aggregate
    /// provider: EF (SQLite), EF (PostgreSQL), RavenDB and File — the GuestbookStoreConformanceTests shape, so the
    /// JSON collection convention of <c>ModuleAggregatesDbContext</c> is held to the document stores' answer.
    /// PostgreSQL and RavenDB FAIL rather than skip when unreachable: a SQLite-only run proves nothing about
    /// the provider the relational convention most often meets in production.
    /// </summary>
    public abstract class CollectionBearingAggregateConformanceTests : IAsyncLifetime
    {
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
            services.AddModuleAggregatePersistence<CollectionBearingAggregate, Guid>();
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

        private async Task<T> InScopeAsync<T>(Func<IRepository<CollectionBearingAggregate, Guid>, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IRepository<CollectionBearingAggregate, Guid>>());
        }

        private Task AddAsync(CollectionBearingAggregate aggregate) =>
            InScopeAsync(async repository =>
            {
                await repository.AddAsync(aggregate);
                return 0;
            });

        [Fact]
        public async Task Every_collection_round_trips_member_for_member_into_a_fresh_scope()
        {
            var written = CollectionBearingAggregate.CreateFull(11, "full");
            await AddAsync(written);

            var reread = await InScopeAsync(repository => repository.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            AssertSameCollections(written, reread!);
        }

        [Fact]
        public async Task FindAsync_on_a_non_string_scalar_returns_the_aggregate_with_its_collections_intact()
        {
            var target = CollectionBearingAggregate.CreateFull(21, "target");
            await AddAsync(target);
            await AddAsync(CollectionBearingAggregate.CreateFull(22, "other"));

            var found = await InScopeAsync(repository => repository.FindAsync(aggregate => aggregate.Bucket == 21));

            var single = Assert.Single(found);
            Assert.Equal(target.Id, single.Id);
            AssertSameCollections(target, single);
        }

        [Fact]
        public async Task Empty_collections_read_back_empty_not_null()
        {
            var written = CollectionBearingAggregate.Create(31, "empty");
            await AddAsync(written);

            var reread = await InScopeAsync(repository => repository.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            Assert.Empty(reread!.Kinds);
            Assert.Empty(reread.Tags);
            Assert.Empty(reread.Parameters);
            Assert.Empty(reread.Contents);
            Assert.Empty(reread.Attempts);
        }

        [Fact]
        public async Task A_collection_grown_after_load_persists_on_update()
        {
            var written = CollectionBearingAggregate.CreateFull(41, "grow");
            await AddAsync(written);

            await InScopeAsync(async repository =>
            {
                var loaded = await repository.GetByIdAsync(written.Id);
                loaded!.AddTag("after-load");
                loaded.RecordAttempt(CollectionProbeKind.InApp, new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc), null);
                await repository.UpdateAsync(loaded);
                return 0;
            });

            var reread = await InScopeAsync(repository => repository.GetByIdAsync(written.Id));

            Assert.NotNull(reread);
            Assert.Equal(written.Tags.Append("after-load"), reread!.Tags);
            Assert.Equal(3, reread.Attempts.Count);
            Assert.Equal(CollectionProbeKind.InApp, reread.Attempts[2].Channel);
        }

        private static void AssertSameCollections(CollectionBearingAggregate expected, CollectionBearingAggregate actual)
        {
            Assert.Equal(expected.Bucket, actual.Bucket);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Kinds, actual.Kinds);
            Assert.Equal(expected.Tags, actual.Tags);
            Assert.Equal(expected.Parameters, actual.Parameters);
            Assert.Equal(expected.Contents, actual.Contents);
            Assert.Equal(
                expected.Attempts.Select(a => (a.Id, a.Channel, a.AttemptedOnUtc, a.AttemptedOnUtc.Kind, a.Error)),
                actual.Attempts.Select(a => (a.Id, a.Channel, a.AttemptedOnUtc, a.AttemptedOnUtc.Kind, a.Error)));
        }
    }

    /// <summary>EF Core on a throwaway SQLite file — the storage-blind overload, exactly as a module composes it.</summary>
    public sealed class SqliteCollectionBearingAggregateConformanceTests : CollectionBearingAggregateConformanceTests
    {
        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-collection-conformance-{Guid.NewGuid():N}.db")};Pooling=True";

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
            var connection = new SqliteConnectionStringBuilder(_connectionString);
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(connection.DataSource);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// EF Core on a LIVE PostgreSQL scratch database (the install's own provider applied to
    /// <c>ConnectionStrings:ModuleAggregates</c>, co-located tables). Fails, never skips, when PostgreSQL is down.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresCollectionBearingAggregateConformanceTests : CollectionBearingAggregateConformanceTests
    {
        private readonly string _database = "stomme_collection_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The collection conformance suite FAILS rather than skips (D46).");
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

    /// <summary>The generic RavenRepository on a LIVE RavenDB, its own database per run. Fails, never skips.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenCollectionBearingAggregateConformanceTests : CollectionBearingAggregateConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-collection-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-collection-conformance-");
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
            // Delete through a FRESH store: the container already disposed the shared one (see
            // RavenGuestbookStoreConformanceTests.DisposeStoreAsync).
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: the next run's janitor sweep removes a leftover
            }
        }
    }

    /// <summary>The generic FileRepository in a throwaway directory — the third provider.</summary>
    public sealed class FileCollectionBearingAggregateConformanceTests : CollectionBearingAggregateConformanceTests
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-collection-file-conformance-{Guid.NewGuid():N}");

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
                // A leftover temp directory is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }
}