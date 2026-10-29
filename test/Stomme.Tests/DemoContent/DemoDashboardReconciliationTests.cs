#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.Dashboards.Infrastructure;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Help;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DemoContent
{
    /// <summary>
    /// The demo dashboard is OFF by default and ADDITIVE when on: with
    /// <c>DemoContent:Enabled</c> the install path writes one published board per site whose four widgets bind the
    /// demo datasets by origin (the chart and the pivot on sales, the grid on inventory, the KPI on the Restricted
    /// customers set); a second boot changes nothing; an operator's delete survives every later boot because the site
    /// is stamped; a second tenant on the same site id receives its own board under its own tenant.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DemoDashboardReconciliationTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static JsonObject On() => new() { ["DemoContent"] = new JsonObject { ["Enabled"] = true } };

        [Fact]
        public async Task With_the_switch_on_the_board_is_written_once_over_the_demo_datasets_and_a_delete_survives_later_boots()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var site = await HelpDrive.SiteAsync(app, Host);
                var boards = await BoardsAsync(app, site);
                var datasets = await DatasetsAsync(app, site);
                output.WriteLine($"[on] boards: {string.Join(", ", boards.Select(board => $"{board.Name} ({board.Status}, {board.Widgets.Count} widgets)"))}");
                var board = Assert.Single(boards, candidate => candidate.Name == DashboardsDemoContentReconciler.BoardName); // beside the cloud cost board
                Assert.Equal((DashboardsDemoContentReconciler.BoardName, DashboardStatus.Published, site.TenantId), (board.Name, board.Status, board.TenantId));

                var byOrigin = datasets.ToDictionary(dataset => dataset.OriginReference!, dataset => dataset.Id);
                Assert.Equal(new[] { "Amount by region", "Customers", "Inventory", "Sales by region" }, board.Widgets.Select(widget => widget.Title).OrderBy(title => title, StringComparer.Ordinal));
                Assert.Equal(byOrigin["demo:sales"], board.Widgets.Single(widget => widget.Kind == WidgetKind.Chart).Binding.DatasetId);
                Assert.Equal(byOrigin["demo:sales"], board.Widgets.Single(widget => widget.Kind == WidgetKind.Pivot).Binding.DatasetId);
                Assert.Equal(byOrigin["demo:inventory"], board.Widgets.Single(widget => widget.Kind == WidgetKind.Grid).Binding.DatasetId);
                Assert.Equal(byOrigin["demo:customers"], board.Widgets.Single(widget => widget.Kind == WidgetKind.Kpi).Binding.DatasetId);
                Assert.Equal(DataClassification.Restricted, datasets.Single(dataset => dataset.OriginReference == "demo:customers").Classification);

                // A second pass: nothing changes.
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                Assert.Equal((2, 6), ((await BoardsAsync(app, site)).Count, (await DatasetsAsync(app, site)).Count)); // with the cloud cost board and its three datasets

                // The operator deletes the board; the next boot leaves it deleted.
                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var repository = sp.GetRequiredService<IDashboardRepository>();
                    await repository.DeleteAsync((await repository.FindByNameAsync(site.Alias.SiteId, DashboardsDemoContentReconciler.BoardName))!);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return 0;
                });
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                Assert.DoesNotContain(await BoardsAsync(app, site), candidate => candidate.Name == DashboardsDemoContentReconciler.BoardName);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_second_tenant_on_the_same_site_id_receives_its_own_board_under_its_own_tenant()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var (first, second) = await HelpDrive.AddSecondTenantAsync(app);
                Assert.Empty(await BoardsAsync(app, second));

                var report = await HelpDrive.RunReconcilersAsync(app);
                output.WriteLine($"[two tenants] visited {report.SitesVisited}, failures: {string.Join(" | ", report.Failures)}");
                Assert.Empty(report.Failures);

                var firstBoard = Assert.Single(await BoardsAsync(app, first), candidate => candidate.Name == DashboardsDemoContentReconciler.BoardName);
                var secondBoard = Assert.Single(await BoardsAsync(app, second), candidate => candidate.Name == DashboardsDemoContentReconciler.BoardName);
                Assert.NotEqual(firstBoard.Id, secondBoard.Id);
                Assert.Equal((first.TenantId, second.TenantId), (firstBoard.TenantId, secondBoard.TenantId));
                // Each board binds ITS tenant's demo datasets.
                var secondDatasets = (await DatasetsAsync(app, second)).Select(dataset => dataset.Id).ToHashSet();
                Assert.All(secondBoard.Widgets, widget => Assert.Contains(widget.Binding.DatasetId, secondDatasets));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<IReadOnlyList<DashboardAggregate>> BoardsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IDashboardRepository>().ListForSiteAsync(site.Alias.SiteId));

        private static Task<IReadOnlyList<Stomme.Modules.Datasets.Domain.DatasetAggregate>> DatasetsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IDatasetRepository>().ListForSiteAsync(site.Alias.SiteId));
    }
}
