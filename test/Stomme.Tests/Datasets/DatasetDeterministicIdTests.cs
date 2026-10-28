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
    /// BL-94 increment 9 (D110) - a dataset's id is its ORIGIN's, so two registrations of one origin - concurrent, from
    /// two scopes, with different names and a case-variant reference - land on ONE row on EF and on RavenDB (and File):
    /// the store either refuses the second insert (EF's key) or writes the same document (RavenDB's id), and the site
    /// lists one dataset for the origin either way. A different origin is a second row.
    /// </summary>
    public abstract class DatasetDeterministicIdTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 27, 2, 3, 4, 567, DateTimeKind.Utc);
        private const int Site = 7;

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

        private static DatasetAggregate Dataset(string name, string reference, string sample)
        {
            var inference = SchemaInference.Infer(sample);
            return DatasetAggregate.Register(TenantId.Default, Site, name, OriginKind.PipelineOutput, reference, DataClassification.Internal, inference.Schema, inference.Summary, inference.SampleRowsJson, Now);
        }

        private async Task<Exception?> TryAddAsync(DatasetAggregate dataset)
        {
            await using var scope = _services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddAsync(dataset);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        [Fact]
        public async Task Two_concurrent_registrations_of_one_origin_are_one_row()
        {
            var first = Dataset("Crawl", "Pipeline-7", "[ { \"a\": \"1\" } ]");
            var second = Dataset("Crawl again", " pipeline-7 ", "[ { \"a\": \"2\" } ]");
            Assert.Equal(first.Id, second.Id);

            var outcomes = await Task.WhenAll(TryAddAsync(first), TryAddAsync(second));

            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDatasetRepository>();
            var rows = await repository.ListForSiteAsync(Site);
            var row = Assert.Single(rows);
            Assert.Equal(first.Id, row.Id);
            Assert.Contains(row.Name, new[] { "Crawl", "Crawl again" });
            // At least one write landed; a store that refused the other did so on the KEY, not on anything else.
            Assert.Contains(outcomes, outcome => outcome is null);

            // A different origin is a second row; the same origin found by either spelling is the one row.
            Assert.Null(await TryAddAsync(Dataset("Other", "pipeline-8", "[ { \"a\": \"3\" } ]")));
            Assert.Equal(2, (await repository.ListForSiteAsync(Site)).Count);
            Assert.Equal(first.Id, (await repository.FindByOriginAsync(Site, OriginKind.PipelineOutput, "PIPELINE-7"))!.Id);
        }
    }

    public sealed class SqliteDatasetDeterministicIdTests : DatasetDeterministicIdTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-datasets-identity-{Guid.NewGuid():N}.db")}";

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
    public sealed class RavenDatasetDeterministicIdTests : DatasetDeterministicIdTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private readonly string _database = "stomme-datasets-identity-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-datasets-identity-");
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

    public sealed class FileDatasetDeterministicIdTests : DatasetDeterministicIdTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-datasets-file-identity-{Guid.NewGuid():N}");

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
