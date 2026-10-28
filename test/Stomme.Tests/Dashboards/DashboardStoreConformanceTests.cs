#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Modules.Dashboards.Domain;
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

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// BL-96 increment 8 - the Dashboard aggregate on every store the install can choose (D79/D80): the widgets ride the
    /// JSON collection column, every value object its scalar; a board round-trips with its widgets in order, its layout,
    /// its enums and its UTC instants; a site lists its own boards and not another's; a change to a widget is read back;
    /// a delete is a delete. Composed through the module's OWN startup, the way the host does it. the predecessor platform's
    /// <c>Dashboard_RoundTrips_ThroughRepository</c> is the first case, on four stores instead of an in-memory one.
    /// </summary>
    public abstract class DashboardStoreConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 30, 0, DateTimeKind.Utc);
        private const string BarChartJson = """{ "series": [ { "type": "bar", "encode": { "y": "$amount" } } ] }""";
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
            new Stomme.Modules.Dashboards.Startup.ServerStartup().ConfigureServices(services);
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

        protected async Task<T> InScopeAsync<T>(Func<IDashboardRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IDashboardRepository>());
        }

        private static DashboardAggregate Board(int siteId, string name, LayoutGrid? layout = null) => DashboardAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, name, layout ?? LayoutGrid.Default, Now);

        private static Widget Chart(string title, GridPosition position, Guid datasetId) =>
            Widget.Create(Guid.NewGuid(), title, WidgetKind.Chart, new DataBinding(datasetId, new[] { "amount" }), position, RefreshMode.Interval(60), new Interaction(InteractionKind.Navigate, "/sales"), new ChartSpec(BarChartJson));

        [Fact]
        public async Task Dashboard_RoundTrips_ThroughRepository()
        {
            var datasetId = Guid.NewGuid();
            var dashboard = Board(Site, "Sales", new LayoutGrid(8, 50));
            dashboard.AddWidget(Chart("Revenue", new GridPosition(0, 0, 4, 3), datasetId), Now);
            var grid = Widget.Create(Guid.NewGuid(), "Rows", WidgetKind.Grid, new DataBinding(datasetId, new[] { "amount", "email" }), new GridPosition(4, 0, 4, 3), RefreshMode.EventDriven);
            dashboard.AddWidget(grid, Now);
            dashboard.SaveWidgetGridState(grid.Id, new GridState(new[] { new ColumnWidth("amount", 120) }, "amount", true, "amount > 0"), Now);
            await InScopeAsync(async repository => { await repository.AddAsync(dashboard); return 0; });

            var read = await InScopeAsync(repository => repository.GetByIdAsync(dashboard.Id));

            Assert.NotNull(read);
            Assert.Equal(("Sales", "SALES", Site, DashboardStatus.Draft, new LayoutGrid(8, 50), TenantId.Default), (read.Name, read.NameNormalized, read.SiteId, read.Status, read.Layout, read.TenantId));
            Assert.Equal((Now, DateTimeKind.Utc, DateTimeKind.Utc), (read.CreatedUtc, read.CreatedUtc.Kind, read.UpdatedUtc.Kind));
            Assert.Equal(new[] { "Revenue", "Rows" }, read.Widgets.Select(w => w.Title)); // in order
            var chart = read.Widgets[0];
            Assert.Equal((WidgetKind.Chart, datasetId, new GridPosition(0, 0, 4, 3), RefreshMode.Interval(60), new Interaction(InteractionKind.Navigate, "/sales"), new ChartSpec(BarChartJson)),
                (chart.Kind, chart.Binding.DatasetId, chart.Position, chart.Refresh, chart.Interaction, chart.ChartSpec));
            Assert.Equal("amount", Assert.Single(chart.Binding.Columns));
            var rows = read.Widgets[1];
            Assert.Equal((WidgetKind.Grid, RefreshMode.EventDriven, Interaction.None), (rows.Kind, rows.Refresh, rows.Interaction));
            Assert.Null(rows.ChartSpec);
            Assert.Equal(new[] { "amount", "email" }, rows.Binding.Columns);
            Assert.Equal(new GridState(new[] { new ColumnWidth("amount", 120) }, "amount", true, "amount > 0"), rows.GridState);
            Assert.Equal(grid.Id, rows.Id);
        }

        [Fact]
        public async Task A_site_lists_its_own_boards_in_name_order_and_finds_one_by_name_whatever_the_case()
        {
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(Board(Site, "Zulu"));
                await repository.AddAsync(Board(Site, "alpha"));
                await repository.AddAsync(Board(OtherSite, "Alpha"));
                return 0;
            });

            var mine = await InScopeAsync(repository => repository.ListForSiteAsync(Site));
            Assert.Equal(new[] { "alpha", "Zulu" }, mine.Select(dashboard => dashboard.Name));
            Assert.All(mine, dashboard => Assert.Equal(Site, dashboard.SiteId));

            var found = await InScopeAsync(repository => repository.FindByNameAsync(Site, " ZULU "));
            Assert.Equal("Zulu", found?.Name);
            Assert.Null(await InScopeAsync(repository => repository.FindByNameAsync(Site, "Bravo")));
        }

        [Fact]
        public async Task A_change_to_a_widget_and_to_the_board_is_read_back_and_a_delete_is_a_delete()
        {
            var datasetId = Guid.NewGuid();
            var dashboard = Board(Site, "Ops");
            var widget = Chart("Revenue", new GridPosition(0, 0, 4, 3), datasetId);
            dashboard.AddWidget(widget, Now);
            await InScopeAsync(async repository => { await repository.AddAsync(dashboard); return 0; });

            var later = Now.AddMinutes(5);
            await InScopeAsync(async repository =>
            {
                var loaded = (await repository.GetByIdAsync(dashboard.Id))!;
                loaded.MoveWidget(widget.Id, new GridPosition(4, 2, 4, 3), later);
                loaded.ReconfigureWidget(widget.Id, "Income", RefreshMode.Manual, Interaction.None, new ChartSpec(BarChartJson), later);
                loaded.Rename("Operations", later);
                loaded.Publish(later);
                await repository.UpdateAsync(loaded);
                return 0;
            });

            var read = (await InScopeAsync(repository => repository.GetByIdAsync(dashboard.Id)))!;
            Assert.Equal(("Operations", "OPERATIONS", DashboardStatus.Published, later), (read.Name, read.NameNormalized, read.Status, read.UpdatedUtc));
            var moved = Assert.Single(read.Widgets);
            Assert.Equal(("Income", new GridPosition(4, 2, 4, 3), RefreshMode.Manual, Interaction.None), (moved.Title, moved.Position, moved.Refresh, moved.Interaction));

            await InScopeAsync(async repository =>
            {
                var loaded = (await repository.GetByIdAsync(dashboard.Id))!;
                loaded.RemoveWidget(widget.Id, later);
                await repository.UpdateAsync(loaded);
                return 0;
            });
            Assert.Empty((await InScopeAsync(repository => repository.GetByIdAsync(dashboard.Id)))!.Widgets);

            await InScopeAsync(async repository => { await repository.DeleteAsync((await repository.GetByIdAsync(dashboard.Id))!); return 0; });
            Assert.Null(await InScopeAsync(repository => repository.GetByIdAsync(dashboard.Id)));
        }
    }

    public sealed class SqliteDashboardStoreConformanceTests : DashboardStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-dashboards-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresDashboardStoreConformanceTests : DashboardStoreConformanceTests
    {
        private readonly string _database = "stomme_dashboards_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The Dashboard store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenDashboardStoreConformanceTests : DashboardStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private readonly string _database = "stomme-dashboards-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-dashboards-conformance-");
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

    public sealed class FileDashboardStoreConformanceTests : DashboardStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-dashboards-file-conformance-{Guid.NewGuid():N}");

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
