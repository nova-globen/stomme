#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.Datasets.Infrastructure;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Dashboards.Infrastructure
{
    /// <summary>
    /// The Dashboards module's demo content: one published board over the demo datasets
    /// an evaluator can open on <c>admin/dashboards</c> - a chart, a pivot, a grid and a KPI - written per site on the
    /// boot/upgrade path through the framework's <see cref="SiteContentReconciliationRunner"/>, ONLY while
    /// <c>DemoContent:Enabled</c> is true (default off; while it is off this type is not registered), and ADDITIVELY: a
    /// site stamped at <see cref="Version"/> is left alone whatever an operator did to the board, an unstamped site
    /// receives the board unless one of that name exists.
    /// </summary>
    /// <remarks>
    /// The board binds the Datasets module's demo sets by ORIGIN, whichever reconciler the runner happens to visit first:
    /// a demo dataset the site lacks is registered here through the same registrar with the same declaration, so the
    /// Datasets reconciler then finds it by origin and adds nothing. The customers set comes out Restricted from the
    /// classifier, so the KPI over it shows the row estimate and the rows are refused - the classification gate,
    /// demonstrated on the board.
    /// </remarks>
    public sealed class DashboardsDemoContentReconciler(
        IDashboardRepository dashboards,
        IDatasetRepository datasets,
        IDatasetRegistrar registrar,
        IUnitOfWork unitOfWork,
        DemoContentStamp stamp,
        TimeProvider clock,
        ILogger<DashboardsDemoContentReconciler> logger) : ISiteContentReconciler
    {
        public const string Module = "Dashboards";

        /// <summary>Bump when the demo board changes; a site stamped at an older version receives the board again only if it lacks one of the name.</summary>
        public const string Version = "1";

        public const string BoardName = "Demo operations";

        // The columns go in a DATASET and `encode` names its dimensions - ECharts' own idiom, as the cloud cost board's specs use.
        // The first version put the "$column" tokens inside `encode`, which takes dimension NAMES: the series then held no data and
        // drew nothing but an axis, on the Dashboards page and on the landing page alike.
        // ChartSpecTokenPlacementTests keeps a token out of `encode` in every in-tree spec.
        public const string SalesChartSpec = """{ "dataset": { "source": { "region": "$region", "amount": "$amount" } }, "xAxis": { "type": "category" }, "yAxis": { "type": "value" }, "series": [ { "type": "bar", "encode": { "x": "region", "y": "amount" } } ] }""";

        public string Name => "Demo dashboard";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            if (await stamp.IsAppliedAsync(context.TenantId, siteId, Module, Version, ct))
            {
                return;
            }

            if (await dashboards.FindByNameAsync(siteId, BoardName, ct) is not null)
            {
                await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
                logger.LogInformation("Demo dashboard on site {SiteId}: present.", siteId);
                return;
            }

            var sales = await DemoDatasetIdAsync(siteId, "demo:sales", ct);
            var inventory = await DemoDatasetIdAsync(siteId, "demo:inventory", ct);
            var customers = await DemoDatasetIdAsync(siteId, "demo:customers", ct);

            var now = clock.GetUtcNow().UtcDateTime;
            var board = DashboardAggregate.Create(Guid.NewGuid(), context.TenantId, siteId, BoardName, LayoutGrid.Default, now);
            board.AddWidget(Widget.Create(Guid.NewGuid(), "Sales by region", WidgetKind.Chart, new DataBinding(sales, new[] { "region", "amount" }), new GridPosition(0, 0, 6, 6), RefreshMode.EventDriven, chartSpec: new ChartSpec(SalesChartSpec)), now);
            board.AddWidget(Widget.Create(Guid.NewGuid(), "Amount by region", WidgetKind.Pivot, new DataBinding(sales, new[] { "region", "amount" }), new GridPosition(6, 0, 6, 6), RefreshMode.EventDriven), now);
            board.AddWidget(Widget.Create(Guid.NewGuid(), "Inventory", WidgetKind.Grid, new DataBinding(inventory, new[] { "sku", "warehouse", "on_hand", "reorder_at" }), new GridPosition(0, 6, 8, 6), RefreshMode.Interval(60)), now);
            board.AddWidget(Widget.Create(Guid.NewGuid(), "Customers", WidgetKind.Kpi, new DataBinding(customers, Array.Empty<string>()), new GridPosition(8, 6, 4, 6), RefreshMode.Manual), now);
            board.Publish(now);
            await dashboards.AddAsync(board, ct);
            await unitOfWork.SaveChangesAsync(ct);

            await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
            logger.LogInformation("Demo dashboard on site {SiteId}: created '{Board}' with {Widgets} widgets.", siteId, BoardName, board.Widgets.Count);
        }

        /// <summary>The demo dataset by origin - registered here, through the Datasets module's own declaration, when the site lacks it.</summary>
        private async Task<Guid> DemoDatasetIdAsync(int siteId, string origin, CancellationToken ct)
        {
            if (await datasets.FindByOriginAsync(siteId, OriginKind.DemoSeed, origin, ct) is { } existing)
            {
                return existing.Id;
            }

            var demo = DatasetsDemoContentReconciler.Demo.Single(candidate => candidate.Origin == origin);
            return await registrar.RegisterOrRefreshAsync(new DatasetRegistration(demo.Name, OriginKind.DemoSeed, demo.Origin, demo.SampleJson, demo.Classification), ct);
        }
    }
}
