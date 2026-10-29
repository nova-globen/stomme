#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Domain;
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

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The Dataset store's observable contract once, driven on EF (SQLite and PostgreSQL),
    /// RavenDB and File through the module's own startup: a dataset round-trips with its columns and its summary as
    /// VALUES, its sample, its enums and UTC instants; found by site and normalised name whatever the case; found by
    /// origin with a case-variant reference resolving to the SAME row on every provider (both sides normalised);
    /// listed in name order; a refresh and a delete land. Fail-not-skip on the external stores.
    /// </summary>
    public abstract class DatasetStoreConformanceTests : IAsyncLifetime
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
            new Stomme.Modules.Datasets.Startup.ServerStartup().ConfigureServices(services);
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

        protected async Task<T> InScopeAsync<T>(Func<IDatasetRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IDatasetRepository>());
        }

        protected static DatasetAggregate Dataset(int siteId, string name, OriginKind kind = OriginKind.PipelineOutput, string? reference = null, DataClassification classification = DataClassification.Internal, string sample = "[ { \"amount\": \"10\", \"email\": \"a@b.co\" }, { \"amount\": \"20\", \"email\": \"c@d.co\" } ]")
        {
            var inference = SchemaInference.Infer(sample);
            return DatasetAggregate.Register(TenantId.Default, siteId, name, kind, reference ?? "pipeline-" + name.Replace(' ', '-'), classification, inference.Schema, inference.Summary, inference.SampleRowsJson, Now);
        }

        [Fact]
        public async Task A_dataset_round_trips_with_its_columns_summary_sample_enums_and_utc_instants()
        {
            var dataset = Dataset(Site, "Sales", OriginKind.ExternalQuery, "conn-1|select 1", DataClassification.Restricted);
            await InScopeAsync(async repository => { await repository.AddAsync(dataset); return 0; });

            var read = await InScopeAsync(repository => repository.GetByIdAsync(dataset.Id));

            Assert.NotNull(read);
            Assert.Equal(("Sales", "SALES", Site, OriginKind.ExternalQuery, "conn-1|select 1", "CONN-1|SELECT 1", DataClassification.Restricted),
                (read.Name, read.NameNormalized, read.SiteId, read.OriginKind, read.OriginReference, read.OriginReferenceNormalized, read.Classification));
            Assert.Equal(new[] { ("amount", ColumnDataType.Number, "currency"), ("email", ColumnDataType.String, "email") }, read.Schema.Columns.Select(column => (column.Name, column.DataType, column.SemanticType)));
            var amount = read.Summary.Columns.First(column => column.Name == "amount");
            Assert.Equal(("10", "20", 2, 0, 2), (amount.Min, amount.Max, amount.UniqueCount, amount.NullCount, read.Summary.RowCountEstimate));
            Assert.Equal(new[] { "10", "20" }, amount.Samples);
            Assert.Contains("a@b.co", read.SampleRowsJson);
            Assert.All(read.PublishedSummary().Columns, column => Assert.Empty(column.Samples)); // Restricted: masked on the way out, stored in full
            Assert.Equal(DateTimeKind.Utc, read.LastRefreshedUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, read.CreatedUtc.Kind);
            Assert.Equal(Now, read.LastRefreshedUtc);
            Assert.Equal(TenantId.Default, read.TenantId);
        }

        [Fact]
        public async Task A_site_finds_its_dataset_by_name_whatever_the_case_and_not_another_sites()
        {
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(Dataset(Site, "Warehouse"));
                await repository.AddAsync(Dataset(OtherSite, "Warehouse"));
                return 0;
            });

            var mine = await InScopeAsync(repository => repository.FindByNameAsync(Site, " wareHOUSE "));
            Assert.NotNull(mine);
            Assert.Equal(Site, mine.SiteId);
            Assert.Null(await InScopeAsync(repository => repository.FindByNameAsync(Site, "Lake")));
        }

        [Fact]
        public async Task A_case_variant_reference_resolves_to_the_same_row_on_every_provider()
        {
            var dataset = Dataset(Site, "Crawl", OriginKind.PipelineOutput, "Pipeline-7");
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(dataset);
                await repository.AddAsync(Dataset(OtherSite, "Crawl", OriginKind.PipelineOutput, "Pipeline-7"));
                await repository.AddAsync(Dataset(Site, "Crawl upload", OriginKind.Upload, "Pipeline-7"));
                return 0;
            });

            foreach (var variant in new[] { "Pipeline-7", "PIPELINE-7", " pipeline-7 " })
            {
                var found = await InScopeAsync(repository => repository.FindByOriginAsync(Site, OriginKind.PipelineOutput, variant));
                Assert.NotNull(found);
                Assert.Equal(dataset.Id, found.Id);
            }

            Assert.Null(await InScopeAsync(repository => repository.FindByOriginAsync(Site, OriginKind.PipelineOutput, "pipeline-8")));
            Assert.NotEqual(dataset.Id, (await InScopeAsync(repository => repository.FindByOriginAsync(Site, OriginKind.Upload, "pipeline-7")))!.Id);
        }

        [Fact]
        public async Task A_sites_datasets_list_in_name_order_and_a_refresh_and_a_delete_land()
        {
            var zulu = Dataset(Site, "Zulu");
            var alpha = Dataset(Site, "alpha");
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(zulu);
                await repository.AddAsync(alpha);
                await repository.AddAsync(Dataset(OtherSite, "Other"));
                return 0;
            });

            Assert.Equal(new[] { "alpha", "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(dataset => dataset.Name));

            await InScopeAsync(async repository =>
            {
                var read = (await repository.GetByIdAsync(zulu.Id))!;
                var next = SchemaInference.Infer("[ { \"amount\": \"1\", \"email\": \"x@y.z\", \"extra\": \"true\" } ]");
                read.Refresh(next.Schema, next.Summary, next.SampleRowsJson, DataClassification.Restricted, Now.AddMinutes(5));
                await repository.UpdateAsync(read);
                return 0;
            });
            var updated = (await InScopeAsync(repository => repository.GetByIdAsync(zulu.Id)))!;
            Assert.Equal((3, DataClassification.Restricted, 1, Now.AddMinutes(5)), (updated.Schema.Columns.Count, updated.Classification, updated.Summary.RowCountEstimate, updated.LastRefreshedUtc));
            Assert.Equal(ColumnDataType.Boolean, updated.Schema.Columns.Single(column => column.Name == "extra").DataType);

            await InScopeAsync(async repository => { await repository.DeleteAsync((await repository.GetByIdAsync(alpha.Id))!); return 0; });
            Assert.Null(await InScopeAsync(repository => repository.GetByIdAsync(alpha.Id)));
            Assert.Equal(new[] { "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(dataset => dataset.Name));
        }
    }

    public sealed class SqliteDatasetStoreConformanceTests : DatasetStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-datasets-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresDatasetStoreConformanceTests : DatasetStoreConformanceTests
    {
        private readonly string _database = "stomme_datasets_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The Dataset store conformance suite FAILS rather than skips.");
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
    public sealed class RavenDatasetStoreConformanceTests : DatasetStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private readonly string _database = "stomme-datasets-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-datasets-conformance-");
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

    public sealed class FileDatasetStoreConformanceTests : DatasetStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-datasets-file-conformance-{Guid.NewGuid():N}");

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
