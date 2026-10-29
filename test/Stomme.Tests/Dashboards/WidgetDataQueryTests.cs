#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Modules.Dashboards.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>The WidgetDataQueryTests cases (4), ported by name over the port: a widget's data comes from the Datasets catalogue after its bound columns are checked, the classification masks and refuses, and the boards answer the Datasets delete guard.</summary>
    public sealed class WidgetDataQueryTests
    {
        [Fact]
        public async Task GetWidgetData_KnownColumns_ResolvesViewAndRows()
        {
            var datasetId = Guid.NewGuid();
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Internal, "amount"));
            catalog.Rows = new DatasetPreviewRows(true, "[ { \"amount\": \"10\" } ]", "OK");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Sales grid", WidgetKind.Grid, datasetId, new[] { "amount" }, 0, 0, 4, 3));

            var data = await harness.SendAsync(new GetWidgetDataQuery(boardId, widgetId));

            Assert.Equal((widgetId, datasetId, "Sales", DataClassification.Internal, true), (data.WidgetId, data.DatasetId, data.DatasetName, data.Classification, data.RowsAllowed));
            Assert.Equal(new[] { "amount" }, data.Columns);
            Assert.Contains("amount", data.RowsJson);
        }

        [Fact]
        public async Task GetWidgetData_UnknownColumn_IsRejected()
        {
            var datasetId = Guid.NewGuid();
            await using var harness = await DashboardsApplicationHarness.StartAsync(new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Internal, "amount")));
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Bad", WidgetKind.Grid, datasetId, new[] { "nope" }, 0, 0, 4, 3));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new GetWidgetDataQuery(boardId, widgetId)));

            Assert.Contains("nope", ex.Message);
        }

        [Fact]
        public async Task GetWidgetData_ConfidentialDataset_MaskedAndRowsRefused()
        {
            var datasetId = Guid.NewGuid();
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Confidential, "ssn"));
            catalog.Rows = new DatasetPreviewRows(false, "[]", "Raw rows are not available for Confidential data.");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Sensitive", WidgetKind.Grid, datasetId, new[] { "ssn" }, 0, 0, 4, 3));

            var data = await harness.SendAsync(new GetWidgetDataQuery(boardId, widgetId));

            Assert.Equal((DataClassification.Confidential, false), (data.Classification, data.RowsAllowed));
            Assert.All(data.Summary.Columns, column => Assert.Empty(column.Samples));
            Assert.Contains("Confidential", data.RowsReason);
        }

        [Fact]
        public async Task UsageProvider_NamesReferencingWidget()
        {
            var datasetId = Guid.NewGuid();
            await using var harness = await DashboardsApplicationHarness.StartAsync(new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Internal, "amount")));
            var boardId = await harness.CreateBoardAsync("Board");
            await harness.SendAsync(new AddWidgetCommand(boardId, "Sales grid", WidgetKind.Grid, datasetId, new[] { "amount" }, 0, 0, 4, 3));

            var references = await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetUsageProvider>().FindReferencesAsync(datasetId));

            Assert.Equal("widget 'Sales grid' on dashboard 'Board'", Assert.Single(references));
            Assert.Empty(await harness.InScopeAsync(sp => sp.GetRequiredService<IDatasetUsageProvider>().FindReferencesAsync(Guid.NewGuid())));
        }

        [Fact]
        public async Task GetWidgetData_MissingDataset_IsRejected()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Orphan", WidgetKind.Kpi, Guid.NewGuid(), Array.Empty<string>(), 0, 0, 4, 3));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new GetWidgetDataQuery(boardId, widgetId)));

            Assert.Contains("does not exist", ex.Message);
        }
    }
}
