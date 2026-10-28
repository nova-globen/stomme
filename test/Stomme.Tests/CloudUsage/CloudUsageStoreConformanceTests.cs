#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.Modules.CloudUsage.Domain;
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

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// BL-97 increment 2 - the three CloudUsage aggregates on every store the install can choose (D79/D80), composed through
    /// the module's OWN startup: a cost row round-trips every Unified Cost Schema field with its decimal exact, a negative
    /// cost and its UTC-midnight date; a site lists its own rows in a window (from inclusive, to exclusive) and not another
    /// site's; a budget round-trips with and without a provider scope, the scope found by value; an alert marker is found by
    /// its deterministic id. the predecessor platform's <c>Repository_RoundTripsCloudCostRecord</c> is the first case, on four stores.
    /// </summary>
    public abstract class CloudUsageStoreConformanceTests : IAsyncLifetime
    {
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
            new Stomme.Modules.CloudUsage.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        private static CloudCostRecordAggregate Row(int siteId, DateOnly date, decimal cost, CloudProvider provider = CloudProvider.Aws, string service = "AmazonS3") =>
            CloudCostRecordAggregate.Record(Guid.NewGuid(), TenantId.Default, siteId, new CloudCostEntry(date, provider, CloudServiceCategory.Storage, service, "arn:aws:s3:::predecessor-exports", cost, "usd", "Finance", "predecessor", "prod"));

        [Fact]
        public async Task Repository_RoundTripsCloudCostRecord()
        {
            var record = Row(Site, new DateOnly(2026, 6, 30), -4.20m);
            await InScopeAsync(async sp => { await sp.GetRequiredService<ICloudCostRecordRepository>().AddAsync(record); return 0; });

            var read = await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().GetByIdAsync(record.Id));

            Assert.NotNull(read);
            Assert.Equal((record.Id, TenantId.Default, Site), (read.Id, read.TenantId, read.SiteId));
            Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), read.DateUtc);
            Assert.Equal(DateTimeKind.Utc, read.DateUtc.Kind);
            Assert.Equal((CloudProvider.Aws, CloudServiceCategory.Storage, "AmazonS3", "arn:aws:s3:::predecessor-exports"), (read.Provider, read.ServiceCategory, read.Service, read.Resource));
            Assert.Equal(-4.20m, read.Cost);
            Assert.Equal(("USD", "Finance", "predecessor", "prod"), (read.Currency, read.Department, read.Project, read.Environment));
            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(Site)));
        }

        [Fact]
        public async Task A_site_lists_its_own_rows_in_a_window_from_inclusive_to_exclusive()
        {
            await InScopeAsync(async sp =>
            {
                var rows = sp.GetRequiredService<ICloudCostRecordRepository>();
                await rows.AddAsync(Row(Site, new DateOnly(2026, 8, 31), 1m));
                await rows.AddAsync(Row(Site, new DateOnly(2026, 9, 1), 2m));
                await rows.AddAsync(Row(Site, new DateOnly(2026, 9, 30), 3.333m, CloudProvider.Azure, "Virtual Machines"));
                await rows.AddAsync(Row(Site, new DateOnly(2026, 10, 1), 4m));
                await rows.AddAsync(Row(OtherSite, new DateOnly(2026, 9, 15), 99m));
                return 0;
            });
            var september = CloudCostRecordAggregate.ToStoredDate(new DateOnly(2026, 9, 1));
            var october = CloudCostRecordAggregate.ToStoredDate(new DateOnly(2026, 10, 1));

            var inSeptember = await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(Site, september, october));
            var fromSeptember = await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(Site, september));
            var beforeSeptember = await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(Site, null, september));
            var theirs = await InScopeAsync(sp => sp.GetRequiredService<ICloudCostRecordRepository>().ListForSiteAsync(OtherSite));

            Assert.Equal(new[] { 2m, 3.333m }, inSeptember.Select(row => row.Cost));
            Assert.Equal(new[] { 2m, 3.333m, 4m }, fromSeptember.Select(row => row.Cost));
            Assert.Equal(new[] { 1m }, beforeSeptember.Select(row => row.Cost));
            Assert.Equal(99m, Assert.Single(theirs).Cost);
        }

        [Fact]
        public async Task A_budget_round_trips_with_and_without_a_provider_scope_and_the_scope_is_found_by_value()
        {
            var overall = CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 200.50m, "usd");
            var azure = CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, Site, 80m, "EUR", CloudProvider.Azure);
            var theirs = CloudBudgetAggregate.Set(Guid.NewGuid(), TenantId.Default, OtherSite, 10m, "USD");
            await InScopeAsync(async sp =>
            {
                var budgets = sp.GetRequiredService<ICloudBudgetRepository>();
                await budgets.AddAsync(overall);
                await budgets.AddAsync(azure);
                await budgets.AddAsync(theirs);
                return 0;
            });

            var mine = await InScopeAsync(sp => sp.GetRequiredService<ICloudBudgetRepository>().ListForSiteAsync(Site));
            Assert.Equal(new[] { overall.Id, azure.Id }, mine.Select(budget => budget.Id)); // the all-providers scope first
            Assert.Null(mine[0].Provider);
            Assert.Equal((200.50m, "USD"), (mine[0].MonthlyAmount, mine[0].Currency));
            Assert.Equal((CloudProvider?)CloudProvider.Azure, mine[1].Provider);

            Assert.Equal(overall.Id, (await InScopeAsync(sp => sp.GetRequiredService<ICloudBudgetRepository>().FindForScopeAsync(Site, null)))?.Id);
            Assert.Equal(azure.Id, (await InScopeAsync(sp => sp.GetRequiredService<ICloudBudgetRepository>().FindForScopeAsync(Site, CloudProvider.Azure)))?.Id);
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<ICloudBudgetRepository>().FindForScopeAsync(Site, CloudProvider.Gcp)));

            await InScopeAsync(async sp =>
            {
                var budgets = sp.GetRequiredService<ICloudBudgetRepository>();
                var loaded = (await budgets.GetByIdAsync(azure.Id))!;
                loaded.Amend(95.25m, "eur");
                await budgets.UpdateAsync(loaded);
                return 0;
            });
            var amended = (await InScopeAsync(sp => sp.GetRequiredService<ICloudBudgetRepository>().GetByIdAsync(azure.Id)))!;
            Assert.Equal((95.25m, "EUR", (CloudProvider?)CloudProvider.Azure), (amended.MonthlyAmount, amended.Currency, amended.Provider));
        }

        [Fact]
        public async Task An_alert_marker_is_found_by_its_deterministic_id_and_is_the_sites_own()
        {
            var raised = new DateTime(2026, 9, 27, 14, 5, 0, DateTimeKind.Utc);
            await InScopeAsync(async sp =>
            {
                await sp.GetRequiredService<ICloudCostAlertMarkerRepository>().AddAsync(CloudCostAlertMarkerAggregate.Raise(TenantId.Default, Site, "budget:b1:80:2026-09", raised));
                return 0;
            });

            Assert.True(await InScopeAsync(sp => sp.GetRequiredService<ICloudCostAlertMarkerRepository>().WasRaisedAsync(TenantId.Default, Site, "budget:b1:80:2026-09")));
            Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ICloudCostAlertMarkerRepository>().WasRaisedAsync(TenantId.Default, OtherSite, "budget:b1:80:2026-09")));
            Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ICloudCostAlertMarkerRepository>().WasRaisedAsync(TenantId.Default, Site, "budget:b1:100:2026-09")));
            var read = (await InScopeAsync(sp => sp.GetRequiredService<ICloudCostAlertMarkerRepository>().GetByIdAsync(CloudCostAlertMarkerAggregate.BuildId(TenantId.Default, Site, "budget:b1:80:2026-09"))))!;
            Assert.Equal(("budget:b1:80:2026-09", Site, raised, DateTimeKind.Utc), (read.Key, read.SiteId, read.RaisedUtc, read.RaisedUtc.Kind));
        }
    }

    public sealed class SqliteCloudUsageStoreConformanceTests : CloudUsageStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-cloudusage-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresCloudUsageStoreConformanceTests : CloudUsageStoreConformanceTests
    {
        private readonly string _database = "stomme_cloudusage_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The CloudUsage store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenCloudUsageStoreConformanceTests : CloudUsageStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-cloudusage-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-cloudusage-conformance-");
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

    public sealed class FileCloudUsageStoreConformanceTests : CloudUsageStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-cloudusage-file-conformance-{Guid.NewGuid():N}");

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
