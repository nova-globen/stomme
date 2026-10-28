#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Modules.CostOptimization.Domain;
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

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 6 - the recommendation on every store the install can choose (D79/D80), composed through the module's
    /// OWN startup: the predecessor platform's <c>OptimizationRecommendationRepositoryTests</c> by name (every field of an open recommendation
    /// round-trips, its evidence list and its decimal saving exact; the dismissed lifecycle persists with its cooldown), and
    /// the port's own: a site lists its own recommendations newest first and finds another site's id as none.
    /// </summary>
    public abstract class CostOptimizationStoreConformanceTests : IAsyncLifetime
    {
        private const int Site = 7;
        private const int OtherSite = 8;

        private static readonly DateTime RaisedAt = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

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
            new Stomme.Modules.CostOptimization.Startup.ServerStartup().ConfigureServices(services);
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

        private Task AddAsync(OptimizationRecommendationAggregate recommendation) =>
            InScopeAsync(async sp => { await sp.GetRequiredService<IOptimizationRecommendationRepository>().AddAsync(recommendation); return 0; });

        private static OptimizationRecommendationAggregate Raise(int siteId, string key, DateTime raisedAt, RecommendationCategory category = RecommendationCategory.RightSizing) =>
            OptimizationRecommendationAggregate.Raise(Guid.NewGuid(), TenantId.Default, siteId, category, key, $"Summary for {key}.", new[] { $"Evidence for {key}." }, EstimatedSaving.Of(1m, "USD"), raisedAt);

        [Fact]
        public async Task Repository_RoundTripsOptimizationRecommendation()
        {
            var recommendation = OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(),
                TenantId.Default,
                Site,
                RecommendationCategory.Cleanup,
                "cleanup:export-artifacts:expired",
                "Purge expired export artifacts.",
                new[] { "37 GB of export artifacts past their retention window", "the oldest is 412 days old" },
                EstimatedSaving.Of(18.75m, "usd"),
                RaisedAt);
            await AddAsync(recommendation);

            var reloaded = await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().GetByIdAsync(recommendation.Id));

            Assert.NotNull(reloaded);
            Assert.Equal((recommendation.Id, TenantId.Default, Site), (reloaded.Id, reloaded.TenantId, reloaded.SiteId));
            Assert.Equal(RecommendationCategory.Cleanup, reloaded.Category);
            Assert.Equal("cleanup:export-artifacts:expired", reloaded.RecommendationKey);
            Assert.Equal("Purge expired export artifacts.", reloaded.Summary);
            Assert.Equal(new[] { "37 GB of export artifacts past their retention window", "the oldest is 412 days old" }, reloaded.Evidence);
            Assert.Equal((18.75m, "USD"), (reloaded.EstimatedSavingAmount, reloaded.EstimatedSavingCurrency));
            Assert.Equal(RecommendationStatus.Open, reloaded.Status);
            Assert.Equal((RaisedAt, DateTimeKind.Utc), (reloaded.RaisedUtc, reloaded.RaisedUtc.Kind));
            Assert.Null(reloaded.AppliedAction);
            Assert.Null(reloaded.DismissedUntilUtc);

            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(Site)));
        }

        [Fact]
        public async Task Repository_PersistsTheDismissedLifecycle()
        {
            var recommendation = OptimizationRecommendationAggregate.Raise(
                Guid.NewGuid(),
                TenantId.Default,
                Site,
                RecommendationCategory.RightSizing,
                "right-sizing:nightly-sync:idle",
                "Reduce the nightly sync schedule to weekly.",
                new[] { "the nightly sync moved 0 rows on 27 of the last 30 runs" },
                EstimatedSaving.Of(42m, "USD"),
                RaisedAt);
            await AddAsync(recommendation);

            var dismissedAt = RaisedAt.AddDays(3);
            await InScopeAsync(async sp =>
            {
                var repository = sp.GetRequiredService<IOptimizationRecommendationRepository>();
                var loaded = (await repository.FindForSiteAsync(Site, recommendation.Id))!;
                loaded.Dismiss("Sync frequency is a customer commitment.", dismissedAt, TimeSpan.FromDays(90));
                await repository.UpdateAsync(loaded);
                return 0;
            });

            var reloaded = await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().GetByIdAsync(recommendation.Id));

            Assert.NotNull(reloaded);
            Assert.Equal(RecommendationStatus.Dismissed, reloaded.Status);
            Assert.Equal("Sync frequency is a customer commitment.", reloaded.DismissReason);
            Assert.Equal(dismissedAt, reloaded.DismissedUtc);
            Assert.Equal(dismissedAt.AddDays(90), reloaded.DismissedUntilUtc);
            Assert.True(reloaded.IsInDismissCooldown(dismissedAt.AddDays(89)));
        }

        [Fact]
        public async Task A_site_lists_its_own_recommendations_newest_first_and_finds_another_sites_id_as_none()
        {
            var older = Raise(Site, "right-sizing:b", RaisedAt);
            var sameInstant = Raise(Site, "right-sizing:a", RaisedAt);
            var newer = Raise(Site, "caching:c", RaisedAt.AddHours(5), RecommendationCategory.Caching);
            var theirs = Raise(OtherSite, "right-sizing:a", RaisedAt.AddDays(1));
            foreach (var recommendation in new[] { older, sameInstant, newer, theirs })
            {
                await AddAsync(recommendation);
            }

            var mine = await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().ListForSiteAsync(Site));

            Assert.Equal(new[] { "caching:c", "right-sizing:a", "right-sizing:b" }, mine.Select(r => r.RecommendationKey)); // newest, then key
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().FindForSiteAsync(Site, theirs.Id)));
            Assert.Equal(theirs.Id, (await InScopeAsync(sp => sp.GetRequiredService<IOptimizationRecommendationRepository>().FindForSiteAsync(OtherSite, theirs.Id)))?.Id);
        }
    }

    public sealed class SqliteCostOptimizationStoreConformanceTests : CostOptimizationStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-costopt-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresCostOptimizationStoreConformanceTests : CostOptimizationStoreConformanceTests
    {
        private readonly string _database = "stomme_costopt_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The CostOptimization store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenCostOptimizationStoreConformanceTests : CostOptimizationStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-costopt-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-costopt-conformance-");
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

    public sealed class FileCostOptimizationStoreConformanceTests : CostOptimizationStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-costopt-file-conformance-{Guid.NewGuid():N}");

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
