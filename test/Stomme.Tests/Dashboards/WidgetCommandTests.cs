#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Domain;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>The WidgetCommandTests cases (10), ported by name over the application layer.</summary>
    public sealed class WidgetCommandTests
    {
        private const string BarChartJson = """{ "series": [ { "type": "bar", "encode": { "y": "$amount" } } ] }""";

        private static Task<Guid> Grid(DashboardsApplicationHarness harness, Guid boardId, string title, int column, int row) =>
            harness.SendAsync(new AddWidgetCommand(boardId, title, WidgetKind.Grid, Guid.NewGuid(), new[] { "x" }, column, row, 4, 3));

        [Fact]
        public async Task AddWidget_GridWidget_AppearsInProjection()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();

            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Sales grid", WidgetKind.Grid, Guid.NewGuid(), new[] { "amount" }, 0, 0, 4, 3));

            var widget = Assert.Single((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets);
            Assert.Equal((widgetId, WidgetKind.Grid, "amount"), (widget.Id, widget.Kind, Assert.Single(widget.Columns)));
        }

        [Fact]
        public async Task AddWidget_ChartWithSpec_RoundTripsSpec()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();

            await harness.SendAsync(new AddWidgetCommand(boardId, "Revenue", WidgetKind.Chart, Guid.NewGuid(), new[] { "amount" }, 0, 0, 4, 3, ChartSpecJson: BarChartJson));

            Assert.Equal(BarChartJson, Assert.Single((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets).ChartSpecJson);
        }

        [Fact]
        public async Task AddWidget_Overlapping_IsRejected()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            await Grid(harness, boardId, "A", 0, 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new AddWidgetCommand(boardId, "B", WidgetKind.Grid, Guid.NewGuid(), new[] { "x" }, 2, 1, 4, 3)));
        }

        [Fact]
        public async Task UpdateWidget_ChangesTitleAndRefresh()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await Grid(harness, boardId, "Old", 0, 0);

            await harness.SendAsync(new UpdateWidgetCommand(boardId, widgetId, "New", RefreshKind.Interval, 60, InteractionKind.None, null, null));

            var widget = Assert.Single((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets);
            Assert.Equal(("New", RefreshKind.Interval, 60), (widget.Title, widget.RefreshKind, widget.IntervalSeconds));
        }

        [Fact]
        public async Task MoveWidget_RepositionsOnGrid()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await Grid(harness, boardId, "A", 0, 0);

            await harness.SendAsync(new MoveWidgetCommand(boardId, widgetId, 4, 2, 4, 3));

            var widget = Assert.Single((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets);
            Assert.Equal((4, 2), (widget.Column, widget.Row));
        }

        [Fact]
        public async Task RemoveWidget_DropsIt()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await Grid(harness, boardId, "A", 0, 0);

            await harness.SendAsync(new RemoveWidgetCommand(boardId, widgetId));

            Assert.Empty((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets);
        }

        [Fact]
        public async Task RearrangeLayout_ThatOrphansWidget_IsRejected()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            await Grid(harness, boardId, "A", 8, 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new RearrangeLayoutCommand(boardId, 6, 40)));
        }

        [Fact]
        public async Task SaveGridState_PersistsColumnsSortFilter()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await Grid(harness, boardId, "Grid", 0, 0);

            await harness.SendAsync(new SaveGridStateCommand(boardId, widgetId, new[] { new ColumnWidth("x", 120) }, "x", true, "x > 0"));

            var widget = Assert.Single((await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets);
            Assert.Equal((120, "x", true, "x > 0"), (Assert.Single(widget.GridState.ColumnWidths).WidthPx, widget.GridState.SortColumn, widget.GridState.SortDescending, widget.GridState.FilterExpression));
        }

        [Fact]
        public async Task SaveGridState_OnChartWidget_IsRejected()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Chart", WidgetKind.Chart, Guid.NewGuid(), new[] { "x" }, 0, 0, 4, 3, ChartSpecJson: BarChartJson));

            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SendAsync(new SaveGridStateCommand(boardId, widgetId, new[] { new ColumnWidth("x", 120) }, null, false, null)));
        }

        [Fact]
        public async Task ReorderWidget_SwapsRenderOrder()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();
            var first = await Grid(harness, boardId, "First", 0, 0);
            await Grid(harness, boardId, "Second", 4, 0);

            await harness.SendAsync(new ReorderWidgetCommand(boardId, first, Forward: true));

            Assert.Equal(new[] { "Second", "First" }, (await harness.SendAsync(new GetDashboardQuery(boardId)))!.Widgets.Select(widget => widget.Title));
        }
    }
}
