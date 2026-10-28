#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Dashboards;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// BL-96 increment 8 - the predecessor platform's DashboardTests (14) by name over the port's domain: the grid rules, the theme-free
    /// chart spec, the widget kind and spec pairing, the refresh floor, publishing, the layout that would orphan a widget.
    /// The round-trip case runs on every store in <see cref="DashboardStoreConformanceTests"/>. Plus the port's own:
    /// every value object's scalar reconstructs it exactly, since that scalar is what every store persists.
    /// </summary>
    public sealed class DashboardTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        private const string BarChartJson = """{ "series": [ { "type": "bar", "encode": { "y": "$amount" } } ] }""";

        private static DashboardAggregate Board(string name = "Ops", LayoutGrid? layout = null) => DashboardAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, name, layout ?? LayoutGrid.Default, Now);

        private static Widget Chart(GridPosition position) =>
            Widget.Create(Guid.NewGuid(), "Revenue", WidgetKind.Chart, new DataBinding(Guid.NewGuid(), new[] { "amount" }), position, RefreshMode.Manual, chartSpec: new ChartSpec(BarChartJson));

        [Fact]
        public void Create_RaisesCreatedEvent()
        {
            var dashboard = Board();

            Assert.Contains(dashboard.DomainEvents, e => e is DashboardCreatedEvent);
            Assert.Equal((DashboardStatus.Draft, "OPS", 1, Now), (dashboard.Status, dashboard.NameNormalized, dashboard.SiteId, dashboard.CreatedUtc));
        }

        [Fact]
        public void AddWidget_Overlapping_IsRejected()
        {
            var dashboard = Board();
            dashboard.AddWidget(Chart(new GridPosition(0, 0, 4, 3)), Now);

            var ex = Assert.Throws<InvalidOperationException>(() => dashboard.AddWidget(Chart(new GridPosition(2, 1, 4, 3)), Now));
            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AddWidget_AdjacentNonOverlapping_IsAccepted()
        {
            var dashboard = Board();
            dashboard.AddWidget(Chart(new GridPosition(0, 0, 4, 3)), Now);
            dashboard.AddWidget(Chart(new GridPosition(4, 0, 4, 3)), Now);

            Assert.Equal(2, dashboard.Widgets.Count);
        }

        [Fact]
        public void AddWidget_OutsideGrid_IsRejected()
        {
            var dashboard = Board(layout: new LayoutGrid(6, 40));

            Assert.Throws<InvalidOperationException>(() => dashboard.AddWidget(Chart(new GridPosition(4, 0, 4, 3)), Now));
        }

        [Fact]
        public void ChartSpec_WithThemeValue_IsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new ChartSpec("""{ "series": [ { "type": "bar", "color": "#ff0000" } ] }"""));
            Assert.Contains("theme-free", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ChartSpec_ThemeFree_IsAccepted() => Assert.Equal(BarChartJson, new ChartSpec(BarChartJson).EchartsJson);

        [Fact]
        public void ChartSpec_InvalidJson_IsRejected() => Assert.Throws<ArgumentException>(() => new ChartSpec("{ not json"));

        [Fact]
        public void ChartWidget_WithoutSpec_IsRejected() =>
            Assert.Throws<InvalidOperationException>(() => Widget.Create(Guid.NewGuid(), "x", WidgetKind.Chart, new DataBinding(Guid.NewGuid(), new[] { "a" }), new GridPosition(0, 0, 1, 1), RefreshMode.Manual));

        [Fact]
        public void NonChartWidget_WithSpec_IsRejected() =>
            Assert.Throws<InvalidOperationException>(() => Widget.Create(Guid.NewGuid(), "x", WidgetKind.Grid, new DataBinding(Guid.NewGuid(), new[] { "a" }), new GridPosition(0, 0, 1, 1), RefreshMode.Manual, chartSpec: new ChartSpec(BarChartJson)));

        [Fact]
        public void RefreshMode_IntervalBelowMinimum_IsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => RefreshMode.Interval(10));

        [Fact]
        public void Publish_TransitionsDraftToPublished_AndRaisesEvent()
        {
            var dashboard = Board();
            dashboard.ClearDomainEvents();

            dashboard.Publish(Now);

            Assert.Equal(DashboardStatus.Published, dashboard.Status);
            Assert.Contains(dashboard.DomainEvents, e => e is DashboardPublishedEvent);
        }

        [Fact]
        public void Publish_WhenAlreadyPublished_IsNoOp()
        {
            var dashboard = Board();
            dashboard.Publish(Now);
            dashboard.ClearDomainEvents();

            dashboard.Publish(Now);

            Assert.DoesNotContain(dashboard.DomainEvents, e => e is DashboardPublishedEvent);
        }

        [Fact]
        public void RearrangeLayout_ThatOrphansAWidget_IsRejected()
        {
            var dashboard = Board();
            dashboard.AddWidget(Chart(new GridPosition(8, 0, 4, 3)), Now);

            Assert.Throws<InvalidOperationException>(() => dashboard.RearrangeLayout(new LayoutGrid(6, 40), Now));
        }

        [Fact]
        public void CloneAs_copies_every_widget_under_a_new_id_as_a_draft()
        {
            var source = Board("Source");
            source.AddWidget(Chart(new GridPosition(0, 0, 4, 3)), Now);
            source.Publish(Now);

            var clone = source.CloneAs(Guid.NewGuid(), "Copy", Now);

            Assert.Equal((DashboardStatus.Draft, "Copy", source.SiteId, source.Layout), (clone.Status, clone.Name, clone.SiteId, clone.Layout));
            var widget = Assert.Single(clone.Widgets);
            Assert.NotEqual(source.Widgets[0].Id, widget.Id);
            Assert.Equal((source.Widgets[0].Binding, source.Widgets[0].Position, source.Widgets[0].ChartSpec), (widget.Binding, widget.Position, widget.ChartSpec));
        }

        [Fact]
        public void Every_value_objects_scalar_reconstructs_it_exactly()
        {
            // The scalar is what every store persists (a plain value object is refused at model build): Create(Value) must be the identity.
            Assert.Equal(new LayoutGrid(8, 50), LayoutGrid.Create(new LayoutGrid(8, 50).Value));
            Assert.Equal(new GridPosition(4, 2, 4, 3), GridPosition.Create(new GridPosition(4, 2, 4, 3).Value));
            Assert.Equal(new ChartSpec(BarChartJson), ChartSpec.Create(new ChartSpec(BarChartJson).Value));
            var datasetId = Guid.NewGuid();
            Assert.Equal(new DataBinding(datasetId, new[] { "amount", "email" }), DataBinding.Create(new DataBinding(datasetId, new[] { "amount", "email" }).Value));
            Assert.Equal(new Interaction(InteractionKind.Navigate, "/reports/1"), Interaction.Create(new Interaction(InteractionKind.Navigate, "/reports/1").Value));
            Assert.Equal(Interaction.None, Interaction.Create(Interaction.None.Value));
            var state = new GridState(new[] { new ColumnWidth("amount", 120) }, "amount", true, "amount > 0");
            Assert.Equal(state, GridState.Create(state.Value));
            Assert.Equal(GridState.Empty, GridState.Create(GridState.Empty.Value));
            Assert.Equal(RefreshMode.Interval(60), RefreshMode.Create(RefreshMode.Interval(60).Value));
            Assert.Equal(RefreshMode.EventDriven, RefreshMode.Create(RefreshMode.EventDriven.Value));
            Assert.Equal(RefreshMode.Manual, RefreshMode.Create("Manual"));

            Assert.Throws<ArgumentException>(() => LayoutGrid.Create("twelve"));
            Assert.Throws<ArgumentException>(() => GridPosition.Create("1,2"));
            Assert.Throws<ArgumentException>(() => RefreshMode.Create("Interval"));
            Assert.Throws<ArgumentException>(() => Interaction.Create("Teleport|x"));
        }

        [Fact]
        public void ReorderWidget_swaps_neighbours_and_stays_put_at_an_edge()
        {
            var dashboard = Board();
            var first = Chart(new GridPosition(0, 0, 4, 3));
            var second = Chart(new GridPosition(4, 0, 4, 3));
            dashboard.AddWidget(first, Now);
            dashboard.AddWidget(second, Now);

            dashboard.ReorderWidget(first.Id, forward: true, Now);
            Assert.Equal(new[] { second.Id, first.Id }, dashboard.Widgets.Select(w => w.Id));

            dashboard.ReorderWidget(first.Id, forward: true, Now); // already last
            Assert.Equal(new[] { second.Id, first.Id }, dashboard.Widgets.Select(w => w.Id));
            Assert.Throws<InvalidOperationException>(() => dashboard.ReorderWidget(Guid.NewGuid(), forward: true, Now));
        }
    }
}
