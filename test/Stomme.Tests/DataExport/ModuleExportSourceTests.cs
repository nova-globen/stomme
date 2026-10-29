#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Export;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.Dashboards.Infrastructure;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.DataExport.Sources;
using Stomme.Modules.Datasets.Infrastructure;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Stomme.Tests.Dashboards;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The <c>ExportSourceResolverTests</c> cases, ported by name on the
    /// contract: the dataset rows projected onto the schema or a projection, a sensitive dataset exported schema-only, a grid
    /// widget and a whole board read exactly as the widget's data query reads them. The permission check that the resolver
    /// once made itself (the caller's permission) is the engine's now: each source DECLARES the key and the engine asks it for the requester
    /// at request time and again when the job runs - the drive proves the refusal over HTTP.
    /// </summary>
    public sealed class ModuleExportSourceTests
    {
        private static readonly Guid DatasetId = Guid.NewGuid();

        // ---- the dataset source ----------------------------------------------------------------------------------

        [Fact]
        public async Task Dataset_resolves_rows_projected_to_the_schema_columns()
        {
            var source = new DatasetRowsExportSource(Catalog(DataClassification.Internal, "[ { \"Name\": \"Widget\", \"Amount\": \"12.5\" } ]", "Name", "Amount"));

            var table = Assert.Single(await ReadAsync(source, Args(DatasetId)));

            Assert.Equal("Sales", table.Name);
            Assert.Equal(new[] { "Name", "Amount" }, table.Columns);
            Assert.Equal(new object?[] { "Widget", "12.5" }, Assert.Single(table.Rows));
        }

        [Fact]
        public async Task Dataset_filter_snapshot_projects_and_reorders_columns()
        {
            var source = new DatasetRowsExportSource(Catalog(DataClassification.Internal, "[ { \"Name\": \"Widget\", \"Amount\": \"12.5\" } ]", "Name", "Amount"));

            var table = Assert.Single(await ReadAsync(source, Args(DatasetId, "Amount")));

            Assert.Equal(new[] { "Amount" }, table.Columns);
            Assert.Equal(new object?[] { "12.5" }, Assert.Single(table.Rows));
        }

        [Fact]
        public async Task Dataset_unknown_projection_column_is_rejected()
        {
            var source = new DatasetRowsExportSource(Catalog(DataClassification.Internal, "[]", "Name"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(source, Args(DatasetId, "nope")));

            Assert.Contains("nope", ex.Message);
        }

        [Fact]
        public void Dataset_without_preview_permission_is_denied()
        {
            // The engine refuses a requester without the declared key (the drive shows the 403); the source declares it.
            var source = new DatasetRowsExportSource(Catalog(DataClassification.Internal, "[]", "Name"));

            Assert.Equal(DatasetPermissions.Preview, source.RequiredPermission);
            Assert.Equal(TableCardinality.Single, source.TableCardinality);
        }

        [Fact]
        public async Task Dataset_confidential_data_is_masked_to_schema_only_no_rows()
        {
            var catalog = Catalog(DataClassification.Confidential, "[ { \"Ssn\": \"123\" } ]", "Ssn");
            catalog.Rows = new DatasetPreviewRows(false, "[]", "Raw rows are not available for Confidential data.");
            var source = new DatasetRowsExportSource(catalog);

            var table = Assert.Single(await ReadAsync(source, Args(DatasetId)));

            Assert.Equal(new[] { "Ssn" }, table.Columns);
            Assert.Empty(table.Rows);
        }

        [Fact]
        public async Task Dataset_confidential_rows_never_leave_even_when_the_catalogue_carries_them()
        {
            // The port's own: the gate is the catalogue's ALLOWED flag, not the presence of rows in its answer.
            var catalog = Catalog(DataClassification.Restricted, "[]", "Ssn");
            catalog.Rows = new DatasetPreviewRows(false, "[ { \"Ssn\": \"123\" } ]", "Refused.");
            var table = Assert.Single(await ReadAsync(new DatasetRowsExportSource(catalog), Args(DatasetId)));

            Assert.Empty(table.Rows);
        }

        [Fact]
        public async Task Dataset_unknown_id_throws()
        {
            var source = new DatasetRowsExportSource(new StubDatasetCatalog());

            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(source, Args(DatasetId)));
        }

        [Fact]
        public async Task Dataset_resolution_is_reproducible_from_the_same_reference()
        {
            var source = new DatasetRowsExportSource(Catalog(DataClassification.Internal, "[ { \"Name\": \"Widget\", \"Amount\": \"12.5\" } ]", "Name", "Amount"));
            var arguments = Args(DatasetId, "Amount", "Name");

            var first = await ReadAsync(source, arguments);
            var second = await ReadAsync(source, arguments);

            Assert.Equal(Shape(first), Shape(second));
            Assert.Equal("Amount|Name", string.Join("|", first[0].Columns));
        }

        [Fact]
        public async Task A_numbers_column_exports_decimals_and_a_value_that_does_not_parse_stays_text()
        {
            var descriptor = new DatasetDescriptor(DatasetId, "Sales", OriginKind.PipelineOutput, "p", DataClassification.Internal,
                new DatasetSchemaDescriptor(new[] { new DatasetColumnDescriptor("amount", ColumnDataType.Number, null, "money"), new DatasetColumnDescriptor("sku", ColumnDataType.String, null) }),
                new DatasetSummaryDescriptor(Array.Empty<DatasetColumnSummaryDescriptor>(), 0), DashboardsApplicationHarness.Start);
            var catalog = new StubDatasetCatalog().With(descriptor);
            catalog.Rows = new DatasetPreviewRows(true, "[ { \"amount\": \"12.50\", \"sku\": \"A\" }, { \"amount\": \"n/a\", \"sku\": 7 } ]", "OK");

            var table = Assert.Single(await ReadAsync(new DatasetRowsExportSource(catalog), Args(DatasetId)));

            Assert.Equal(new[] { ExportValueKind.Decimal, ExportValueKind.Text }, table.Kinds);
            Assert.Equal("money", table.Formats[0]); // the column's format reaches the cell formatter
            Assert.Equal(new object?[] { 12.50m, "A" }, table.Rows[0]);
            Assert.Equal(new object?[] { "n/a", 7L }, table.Rows[1]);
        }

        [Theory]
        [InlineData(null, "datasetId")]
        [InlineData("[]", "JSON object")]
        [InlineData("{\"datasetId\":\"x\"}", "datasetId")]
        [InlineData("{\"datasetId\":\"6f1c7a2e-7a41-4a44-8f7a-0d6f1b2c3d4e\",\"siteId\":2}", "'siteId' is not an argument")]
        [InlineData("{\"datasetId\":\"6f1c7a2e-7a41-4a44-8f7a-0d6f1b2c3d4e\",\"columns\":[1]}", "columns")]
        public void Dataset_arguments_are_refused_before_a_job_is_queued(string? json, string expected)
        {
            var refusal = new DatasetRowsExportSource(new StubDatasetCatalog()).Validate(new ExportSourceContext(1, 7, json));

            Assert.NotNull(refusal);
            Assert.Contains(expected, refusal, StringComparison.Ordinal);
        }

        // ---- the dashboard sources -------------------------------------------------------------------------------

        [Fact]
        public async Task DashboardGrid_resolves_a_single_table_from_the_reader()
        {
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(DatasetId, DataClassification.Internal, "amount"));
            catalog.Rows = new DatasetPreviewRows(true, "[ { \"amount\": 10 } ]", "OK");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Sales grid", WidgetKind.Grid, DatasetId, new[] { "amount" }, 0, 0, 6, 4));

            var tables = await harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardGridExportSource.SourceKey), GridArgs(boardId, widgetId)));

            var table = Assert.Single(tables);
            Assert.Equal("Sales grid", table.Name);
            Assert.Equal(new[] { "amount" }, table.Columns);
            Assert.Equal(new object?[] { 10L }, Assert.Single(table.Rows));
        }

        [Fact]
        public async Task DashboardGrid_without_view_permission_is_denied()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var (grid, report) = await harness.InScopeAsync(sp => Task.FromResult((Source(sp, DashboardGridExportSource.SourceKey), Source(sp, DashboardReportExportSource.SourceKey))));

            Assert.Equal((DashboardPermissions.View, TableCardinality.Single), (grid.RequiredPermission, grid.TableCardinality));
            Assert.Equal((DashboardPermissions.View, TableCardinality.Multiple), (report.RequiredPermission, report.TableCardinality)); // CSV refuses a report
        }

        [Fact]
        public async Task DashboardGrid_malformed_reference_throws()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var grid = await harness.InScopeAsync(sp => Task.FromResult(Source(sp, DashboardGridExportSource.SourceKey)));

            Assert.Contains("GUIDs", grid.Validate(new ExportSourceContext(1, 7, "{\"dashboardId\":\"not-a-ref\"}")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => grid.OpenAsync(new ExportSourceContext(1, 7, "not-a-ref")));
        }

        [Fact]
        public async Task DashboardReport_resolves_one_table_per_grid_widget()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var catalog = new StubDatasetCatalog()
                .With(StubDatasetCatalog.Descriptor(first, DataClassification.Internal, "x"))
                .With(StubDatasetCatalog.Descriptor(second, DataClassification.Internal, "y"));
            catalog.RowsFor[first] = new DatasetPreviewRows(true, "[ { \"x\": 1 } ]", "OK");
            catalog.RowsFor[second] = new DatasetPreviewRows(true, "[ { \"y\": 2 } ]", "OK");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            await harness.SendAsync(new AddWidgetCommand(boardId, "First", WidgetKind.Grid, first, new[] { "x" }, 0, 0, 4, 4));
            await harness.SendAsync(new AddWidgetCommand(boardId, "Count", WidgetKind.Kpi, first, Array.Empty<string>(), 4, 0, 4, 4));
            await harness.SendAsync(new AddWidgetCommand(boardId, "Second", WidgetKind.Grid, second, new[] { "y" }, 8, 0, 4, 4));

            var tables = await harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardReportExportSource.SourceKey), ReportArgs(boardId)));

            Assert.Equal(new[] { "First", "Second" }, tables.Select(table => table.Name)); // grid widgets only, in the board's order
            Assert.Equal(new object?[] { 2L }, tables[1].Rows[0]);
        }

        [Fact]
        public async Task DashboardReport_with_no_grid_widgets_throws()
        {
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(DatasetId, DataClassification.Internal, "x"));
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            await harness.SendAsync(new AddWidgetCommand(boardId, "Count", WidgetKind.Kpi, DatasetId, Array.Empty<string>(), 0, 0, 4, 4));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardReportExportSource.SourceKey), ReportArgs(boardId))));

            Assert.Contains("no grid widgets", ex.Message);
        }

        [Fact]
        public async Task A_widget_that_is_not_a_grid_and_a_restricted_grid_export_nothing_past_the_gate()
        {
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(DatasetId, DataClassification.Restricted, "contact"));
            catalog.Rows = new DatasetPreviewRows(false, "[ { \"contact\": \"ada@example.com\" } ]", "Refused.");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            var kpi = await harness.SendAsync(new AddWidgetCommand(boardId, "Count", WidgetKind.Kpi, DatasetId, Array.Empty<string>(), 0, 0, 4, 4));
            var grid = await harness.SendAsync(new AddWidgetCommand(boardId, "Contacts", WidgetKind.Grid, DatasetId, new[] { "contact" }, 4, 0, 4, 4));

            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardGridExportSource.SourceKey), GridArgs(boardId, kpi))));
            var table = Assert.Single(await harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardGridExportSource.SourceKey), GridArgs(boardId, grid))));

            Assert.Equal(new[] { "contact" }, table.Columns);
            Assert.Empty(table.Rows);
        }

        [Fact]
        public async Task Another_sites_board_does_not_exist_for_either_dashboard_source()
        {
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(DatasetId, DataClassification.Internal, "x"));
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var theirs = await harness.InScopeAsync(async sp =>
            {
                var board = DashboardAggregate.Create(Guid.NewGuid(), TenantId.Default, DashboardsApplicationHarness.SiteId + 1, "Theirs", LayoutGrid.Default, DashboardsApplicationHarness.Start);
                board.AddWidget(Widget.Create(Guid.NewGuid(), "Grid", WidgetKind.Grid, new DataBinding(DatasetId, new[] { "x" }), new GridPosition(0, 0, 4, 4), RefreshMode.Manual), DashboardsApplicationHarness.Start);
                await sp.GetRequiredService<IDashboardRepository>().AddAsync(board);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return board;
            });

            var report = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardReportExportSource.SourceKey), ReportArgs(theirs.Id))));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.InScopeAsync(sp => ReadAsync(Source(sp, DashboardGridExportSource.SourceKey), GridArgs(theirs.Id, theirs.Widgets[0].Id))));
            Assert.Contains("does not exist", report.Message);
        }

        // ---- the registry ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Registry_resolves_by_kind_and_throws_for_an_unregistered_kind()
        {
            // The registry is keyed by the canonical source key (the kind the source enumeration lists): the three module sources
            // register beside the event log without a collision, each found by its exact key and by nothing else.
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var dashboardSources = await harness.InScopeAsync(sp => Task.FromResult(sp.GetServices<IExportSource>().ToList()));
            var registry = new ExportSourceRegistry(dashboardSources.Append(new DatasetRowsExportSource(new StubDatasetCatalog())));

            Assert.Equal(new[] { DashboardGridExportSource.SourceKey, DashboardReportExportSource.SourceKey, DatasetRowsExportSource.SourceKey }, registry.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.True(registry.TryGet(DatasetRowsExportSource.SourceKey, out var found));
            Assert.IsType<DatasetRowsExportSource>(found);
            Assert.False(registry.TryGet("Datasets.Rows", out _));
            Assert.False(registry.TryGet("datasets.query", out _));
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        internal sealed record ReadTable(string Name, List<string> Columns, List<ExportValueKind> Kinds, List<string?> Formats, List<object?[]> Rows);

        internal static async Task<List<ReadTable>> ReadAsync(IExportSource source, string? argumentsJson)
        {
            var tables = new List<ReadTable>();
            await using var document = await source.OpenAsync(new ExportSourceContext(DashboardsApplicationHarness.SiteId, 7, argumentsJson));
            await foreach (var table in document.ReadTablesAsync())
            {
                var rows = new List<object?[]>();
                await foreach (var row in table.ReadRowsAsync())
                {
                    rows.Add(row);
                }

                tables.Add(new ReadTable(table.Name, table.Columns.Select(c => c.Name).ToList(), table.Columns.Select(c => c.Kind).ToList(), table.Columns.Select(c => c.Format).ToList(), rows));
            }

            return tables;
        }

        private static IExportSource Source(IServiceProvider services, string key) => services.GetServices<IExportSource>().Single(source => source.Key == key);

        private static StubDatasetCatalog Catalog(DataClassification classification, string rowsJson, params string[] columns)
        {
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(DatasetId, classification, columns));
            catalog.Rows = new DatasetPreviewRows(true, rowsJson, "OK");
            return catalog;
        }

        private static string Args(Guid datasetId, params string[] columns) =>
            columns.Length == 0
                ? $$"""{"datasetId":"{{datasetId}}"}"""
                : $$"""{"datasetId":"{{datasetId}}","columns":[{{string.Join(",", columns.Select(c => "\"" + c + "\""))}}]}""";

        private static string GridArgs(Guid dashboardId, Guid widgetId) => $$"""{"dashboardId":"{{dashboardId}}","widgetId":"{{widgetId}}"}""";

        private static string ReportArgs(Guid dashboardId) => $$"""{"dashboardId":"{{dashboardId}}"}""";

        private static string Shape(List<ReadTable> tables) =>
            string.Join(";", tables.Select(t => $"{t.Name}:{string.Join(",", t.Columns)}:" + string.Join("/", t.Rows.Select(r => string.Join(",", r)))));
    }
}
