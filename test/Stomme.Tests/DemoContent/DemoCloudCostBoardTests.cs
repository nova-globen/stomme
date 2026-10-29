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
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.CloudUsage.Infrastructure;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Help;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DemoContent
{
    /// <summary>
    /// The seeded cost board, checked on an installed SQLite host with <c>DemoContent:Enabled</c>: the install path writes the published "Cloud Cost
    /// Analytics" board - three KPIs, two charts, one grid - every widget bound to a dataset whose schema has its columns, a
    /// chart spec on the charts alone; a second pass changes nothing. The port's own: a site that already holds the canonical
    /// "Multi-cloud cost" dataset gets its board bound to THAT dataset, not a sample; an operator's delete survives.
    /// The navigation to the seeded board is the module's own <c>admin/cloud-usage</c> page, which
    /// <c>CloudUsageModuleDriveTests</c> reaches.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DemoCloudCostBoardTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static JsonObject On() => new() { ["DemoContent"] = new JsonObject { ["Enabled"] = true } };

        [Fact]
        public async Task Host_SeedsCloudCostAnalyticsDashboard_BoundToCostDatasets()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var site = await HelpDrive.SiteAsync(app, Host);
                var board = Assert.Single(await BoardsAsync(app, site), b => b.Name == CloudUsageDemoContentReconciler.BoardName);
                var datasets = await DatasetsAsync(app, site);
                output.WriteLine($"[board] {string.Join(", ", board.Widgets.Select(w => $"{w.Title} ({w.Kind})"))}");

                Assert.Equal((DashboardStatus.Published, site.TenantId), (board.Status, board.TenantId));
                Assert.Equal(new[] { WidgetKind.Kpi, WidgetKind.Kpi, WidgetKind.Kpi, WidgetKind.Chart, WidgetKind.Chart, WidgetKind.Grid }, board.Widgets.Select(w => w.Kind));
                Assert.All(board.Widgets, widget =>
                {
                    var dataset = Assert.Single(datasets, d => d.Id == widget.Binding.DatasetId);
                    var columns = dataset.ToDescriptor().Schema.Columns.Select(c => c.Name).ToList();
                    Assert.All(widget.Binding.Columns, column => Assert.Contains(column, columns));
                    Assert.Equal(widget.Kind == WidgetKind.Chart, widget.ChartSpec is not null);
                });
                var cost = Assert.Single(datasets, d => d.Name == CloudUsageDemoContentReconciler.CostDatasetName);
                Assert.Equal((OriginKind.DemoSeed, "demo:cloud-cost"), (cost.OriginKind, cost.OriginReference));
                Assert.Equal(4, board.Widgets.Count(w => w.Binding.DatasetId == cost.Id)); // the spend and month-over-month KPIs, the trend, the top services
                Assert.Equal(cost.Id, board.Widgets.Single(w => w.Title == "Top 10 services").Binding.DatasetId);

                // A second pass: nothing changes.
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                Assert.Single(await BoardsAsync(app, site), b => b.Name == CloudUsageDemoContentReconciler.BoardName);
                Assert.Equal(datasets.Count, (await DatasetsAsync(app, site)).Count);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_site_that_holds_the_canonical_cost_dataset_gets_the_board_bound_to_it_and_a_delete_survives()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: On());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var site = await HelpDrive.SiteAsync(app, Host);

                // Remove what the install wrote - the board, then the sample cost dataset it bound - and register the site's
                // OWN "Multi-cloud cost" (a sync's ledger, say); then clear the stamp, the operator's "reset to seed".
                var mine = await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    var boards = sp.GetRequiredService<IDashboardRepository>();
                    await boards.DeleteAsync((await boards.FindByNameAsync(site.Alias.SiteId, CloudUsageDemoContentReconciler.BoardName))!);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    var datasets = sp.GetRequiredService<IDatasetRepository>();
                    await datasets.DeleteAsync((await datasets.FindByNameAsync(site.Alias.SiteId, CloudUsageDemoContentReconciler.CostDatasetName))!);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return await sp.GetRequiredService<IDatasetRegistrar>().RegisterOrRefreshAsync(new DatasetRegistration(
                        CloudUsageDemoContentReconciler.CostDatasetName, OriginKind.ExternalQuery, "connection-ledger", CloudUsageDemoContentReconciler.CostDatasetSampleJson));
                });
                await ClearStampAsync(app, site);

                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                var board = Assert.Single(await BoardsAsync(app, site), b => b.Name == CloudUsageDemoContentReconciler.BoardName);
                Assert.Equal(mine, board.Widgets.Single(w => w.Title == "Total monthly spend").Binding.DatasetId);

                // The operator deletes the board; the next boot leaves it deleted.
                await HelpDrive.InSiteAsync(app, site, async sp =>
                {
                    await sp.GetRequiredService<IDashboardRepository>().DeleteAsync(board);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return 0;
                });
                Assert.Empty((await HelpDrive.RunReconcilersAsync(app)).Failures);
                Assert.DoesNotContain(await BoardsAsync(app, site), b => b.Name == CloudUsageDemoContentReconciler.BoardName);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task ClearStampAsync(WebApplication app, HelpSite site) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var settings = sp.GetRequiredService<ISettingRepository>();
                var stamp = await settings.GetSettingAsync(EntityNames.Site, alias.SiteId, DemoContentStamp.SettingName(CloudUsageDemoContentReconciler.Module, site.TenantId));
                Assert.NotNull(stamp);
                await settings.DeleteSettingAsync(EntityNames.Site, stamp.SettingId);
                return 0;
            });

        private static Task<IReadOnlyList<DashboardAggregate>> BoardsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IDashboardRepository>().ListForSiteAsync(site.Alias.SiteId));

        private static Task<IReadOnlyList<Stomme.Modules.Datasets.Domain.DatasetAggregate>> DatasetsAsync(WebApplication app, HelpSite site) =>
            HelpDrive.InSiteAsync(app, site, sp => sp.GetRequiredService<IDatasetRepository>().ListForSiteAsync(site.Alias.SiteId));
    }
}
