#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Dashboards.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.CloudUsage.Infrastructure
{
    /// <summary>
    /// The "Cloud Cost Analytics" board (the predecessor platform's <c>CloudUsageDashboardCatalogue</c> and its seeder):
    /// six widgets - total spend, month-over-month and cost-vs-budget KPIs, the cost trend by provider and the top services
    /// as charts, the underutilized resources as a grid - over three datasets, published, per site.
    /// </summary>
    /// <remarks>
    /// <para><b>CHANGED from the predecessor platform.</b> the predecessor platform seeded it on every start of every host, into the default tenant, with
    /// well-known ids. Here it is DEMO CONTENT: written per site on the boot path through the framework's site-content
    /// runner ONLY while <c>DemoContent:Enabled</c> is true (default off; the reconciler is not even registered otherwise),
    /// additively under the per-site stamp, with fresh ids - the module store is one per installation, so a well-known id
    /// would collide across sites and tenants.</para>
    /// <para><b>The data is the site's when it exists.</b> A dataset of the canonical name the site already holds - a sync
    /// materialising its ledger as "Multi-cloud cost", say - is bound as it is; only an absent one is registered as an
    /// obviously-sample seed through the Datasets registrar (<c>DemoSeed</c>, one origin reference each, so the three seeds
    /// are three datasets). The underutilized-resources list stays a labelled sample: advisor-API integration is a booked
    /// follow-up, never faked.</para>
    /// </remarks>
    public sealed class CloudUsageDemoContentReconciler(
        IDashboardRepository dashboards,
        IDatasetRepository datasets,
        IDatasetRegistrar registrar,
        IUnitOfWork unitOfWork,
        DemoContentStamp stamp,
        TimeProvider clock,
        ILogger<CloudUsageDemoContentReconciler> logger) : ISiteContentReconciler
    {
        public const string Module = "CloudUsage";

        public const string Version = "1";

        public const string BoardName = "Cloud Cost Analytics";

        public const string CostDatasetName = "Multi-cloud cost";

        public const string BudgetDatasetName = "Cloud budget status";

        public const string UnderutilizedDatasetName = "Underutilized resources";

        public const string CostDatasetSampleJson = """
            [
              { "date": "2026-06-01", "provider": "Azure", "serviceCategory": "Compute", "service": "Virtual Machines", "resource": "sample-vm-01 (sample)", "cost": 118.40, "currency": "USD", "department": "engineering", "project": "sample-project", "environment": "production" },
              { "date": "2026-06-01", "provider": "Aws", "serviceCategory": "Storage", "service": "Amazon S3", "resource": "sample-bucket (sample)", "cost": 42.10, "currency": "USD", "department": "engineering", "project": "sample-project", "environment": "production" },
              { "date": "2026-06-02", "provider": "Gcp", "serviceCategory": "Database", "service": "Cloud SQL", "resource": "sample-instance (sample)", "cost": 63.75, "currency": "USD", "department": "engineering", "project": "sample-project", "environment": "staging" }
            ]
            """;

        public const string BudgetDatasetSampleJson = """
            [
              { "month": "2026-06", "provider": "all providers", "currency": "USD", "budgetAmount": 5000.00, "actualSpend": 3712.50, "remainingAmount": 1287.50, "percentUsed": 74.3 }
            ]
            """;

        public const string UnderutilizedDatasetSampleJson = """
            [
              { "provider": "Azure", "resource": "sample-vm-01 (sample)", "service": "Virtual Machines", "utilizationPercent": 4.2, "monthlyCost": 118.40, "currency": "USD", "note": "Sample row - connect a cloud advisor source to populate this list (advisor-API integration is a follow-up)." }
            ]
            """;

        private const string TrendChartSpecJson = """
            {
              "tooltip": { "trigger": "axis" },
              "legend": {},
              "dataset": [
                { "source": { "date": "$date", "provider": "$provider", "cost": "$cost" } },
                { "transform": { "type": "filter", "config": { "dimension": "provider", "value": "Azure" } } },
                { "transform": { "type": "filter", "config": { "dimension": "provider", "value": "Aws" } } },
                { "transform": { "type": "filter", "config": { "dimension": "provider", "value": "Gcp" } } }
              ],
              "xAxis": { "type": "category" },
              "yAxis": { "type": "value" },
              "series": [
                { "name": "Azure", "type": "line", "stack": "provider", "areaStyle": {}, "datasetIndex": 1, "encode": { "x": "date", "y": "cost" } },
                { "name": "AWS", "type": "line", "stack": "provider", "areaStyle": {}, "datasetIndex": 2, "encode": { "x": "date", "y": "cost" } },
                { "name": "GCP", "type": "line", "stack": "provider", "areaStyle": {}, "datasetIndex": 3, "encode": { "x": "date", "y": "cost" } }
              ]
            }
            """;

        private const string TopServicesChartSpecJson = """
            {
              "tooltip": { "trigger": "axis" },
              "dataset": [
                { "source": { "service": "$service", "cost": "$cost" } },
                { "transform": { "type": "sort", "config": { "dimension": "cost", "order": "desc" } } }
              ],
              "xAxis": { "type": "value" },
              "yAxis": { "type": "category", "inverse": true },
              "series": [ { "type": "bar", "datasetIndex": 1, "encode": { "x": "cost", "y": "service" } } ]
            }
            """;

        public string Name => "Demo cloud cost board";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            if (await stamp.IsAppliedAsync(context.TenantId, siteId, Module, Version, ct))
            {
                return;
            }

            if (await dashboards.FindByNameAsync(siteId, BoardName, ct) is null)
            {
                var cost = await DatasetAsync(siteId, CostDatasetName, "demo:cloud-cost", CostDatasetSampleJson, ct);
                var budget = await DatasetAsync(siteId, BudgetDatasetName, "demo:cloud-budget-status", BudgetDatasetSampleJson, ct);
                var underutilized = await DatasetAsync(siteId, UnderutilizedDatasetName, "demo:cloud-underutilized", UnderutilizedDatasetSampleJson, ct);

                var now = clock.GetUtcNow().UtcDateTime;
                var board = DashboardAggregate.Create(Guid.NewGuid(), context.TenantId, siteId, BoardName, LayoutGrid.Default, now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Total monthly spend", WidgetKind.Kpi, new DataBinding(cost, new[] { "date", "cost", "currency" }), new GridPosition(0, 0, 4, 3), RefreshMode.EventDriven), now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Month-over-month change", WidgetKind.Kpi, new DataBinding(cost, new[] { "date", "cost", "currency" }), new GridPosition(4, 0, 4, 3), RefreshMode.EventDriven), now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Cost vs. budget", WidgetKind.Kpi, new DataBinding(budget, new[] { "month", "currency", "budgetAmount", "actualSpend", "percentUsed" }), new GridPosition(8, 0, 4, 3), RefreshMode.EventDriven), now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Cost trend by provider", WidgetKind.Chart, new DataBinding(cost, new[] { "date", "provider", "cost" }), new GridPosition(0, 3, 8, 6), RefreshMode.EventDriven, chartSpec: new ChartSpec(TrendChartSpecJson)), now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Top 10 services", WidgetKind.Chart, new DataBinding(cost, new[] { "service", "cost" }), new GridPosition(8, 3, 4, 6), RefreshMode.EventDriven, chartSpec: new ChartSpec(TopServicesChartSpecJson)), now);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Underutilized resources", WidgetKind.Grid,
                    new DataBinding(underutilized, new[] { "provider", "resource", "service", "utilizationPercent", "monthlyCost", "currency", "note" }), new GridPosition(0, 9, 12, 5), RefreshMode.Manual), now);
                board.Publish(now);
                await dashboards.AddAsync(board, ct);
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Demo cloud cost board on site {SiteId}: created '{Board}'.", siteId, BoardName);
            }

            await stamp.MarkAppliedAsync(context.TenantId, siteId, Module, Version, ct);
        }

        // The site's own dataset of that name when there is one; an obviously-sample seed otherwise.
        private async Task<Guid> DatasetAsync(int siteId, string name, string origin, string sampleJson, CancellationToken ct)
        {
            if (await datasets.FindByNameAsync(siteId, name, ct) is { } existing)
            {
                return existing.Id;
            }

            return await registrar.RegisterOrRefreshAsync(new DatasetRegistration(name, OriginKind.DemoSeed, origin, sampleJson, DataClassification.Internal), ct);
        }
    }
}
