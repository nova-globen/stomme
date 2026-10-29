#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// The Dashboards module on installed hosts on SQLite and PostgreSQL, over HTTP: the admin page is
    /// served from the module's own template; a board is created, a dataset registered through the Datasets API and a
    /// chart widget bound to it whose data comes back with its rows; a grid bound to a Restricted dataset has its rows
    /// refused; an overlapping widget is refused with the board's own reason; the dataset a widget binds cannot be
    /// deleted; a user holding no key is refused; a publish and a clone; a delete. Nothing here reaches the internet.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DashboardsModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Dashboards";
        private const string BarChartJson = """{ "series": [ { "type": "bar", "encode": { "y": "$amount" } } ] }""";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_page_is_served_and_the_api_builds_a_board_over_the_sites_datasets_under_its_keys(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/dashboards", alias.SiteId)));
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var page = await GateDrive.GetAsync(client, cookies, Host, "/admin/dashboards", output, "the dashboards page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Dashboards", page.Body);

                // A user holding no dashboards key is refused at the door.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, Api, output, "list as a plain user")).Status);

                // The datasets the widgets bind: an open one with rows, a Restricted one whose rows are refused.
                var sales = await Post<DatasetDescriptor>(app, client, cookies, hostId, "/api/Datasets", new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-sales", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" }, { \"sku\": \"B2\", \"amount\": \"20\" } ]", Classification = DataClassification.Internal }, "register Sales");
                var customers = await Post<DatasetDescriptor>(app, client, cookies, hostId, "/api/Datasets", new RegisterDatasetRequest { Name = "Customers", OriginKind = OriginKind.Upload, OriginReference = "file-1", SampleJson = "[ { \"name\": \"Ada\", \"contact\": \"ada@example.com\" } ]", Classification = DataClassification.Public }, "register Customers");
                Assert.Equal(DataClassification.Restricted, customers.Classification);

                // A board, a chart bound to Sales, a grid bound to Customers.
                var board = await Post<DashboardView>(app, client, cookies, hostId, Api, new CreateDashboardRequest { Name = "Operations", Columns = 12, RowHeightPx = 40 }, "create the board");
                Assert.Equal((DashboardStatus.Draft, 12), (board.Status, board.Columns));
                var list = JsonSerializer.Deserialize<List<DashboardView>>((await GateDrive.GetAsync(client, cookies, Host, Api, output, "list")).Body, Json)!;
                Assert.Contains(list, dashboard => dashboard.Id == board.Id);

                board = await Post<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/widgets", new AddWidgetRequest { Title = "Revenue", Kind = WidgetKind.Chart, DatasetId = sales.Id, Columns = new() { "amount" }, Column = 0, Row = 0, ColumnSpan = 6, RowSpan = 5, ChartSpecJson = BarChartJson }, "add the chart");
                board = await Post<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/widgets", new AddWidgetRequest { Title = "Customers", Kind = WidgetKind.Grid, DatasetId = customers.Id, Columns = new() { "name" }, Column = 6, Row = 0, ColumnSpan = 6, RowSpan = 5, RefreshKind = RefreshKind.Interval, IntervalSeconds = 60 }, "add the grid");
                Assert.Equal(new[] { "Revenue", "Customers" }, board.Widgets.Select(widget => widget.Title));
                var chart = board.Widgets[0];
                var grid = board.Widgets[1];
                Assert.Equal((BarChartJson, RefreshKind.Interval, 60), (chart.ChartSpecJson, grid.RefreshKind, grid.IntervalSeconds));

                // The chart's data carries the open dataset's rows; the grid's has the Restricted rows refused and its summary masked.
                var chartData = JsonSerializer.Deserialize<WidgetDataView>((await GateDrive.GetAsync(client, cookies, Host, $"{Api}/{board.Id}/widgets/{chart.Id}/data", output, "the chart's data")).Body, Json)!;
                Assert.Equal((sales.Id, DataClassification.Internal, true), (chartData.DatasetId, chartData.Classification, chartData.RowsAllowed));
                Assert.Contains("\"amount\"", chartData.RowsJson);
                var gridData = await GateDrive.GetAsync(client, cookies, Host, $"{Api}/{board.Id}/widgets/{grid.Id}/data", output, "the grid's data");
                var refused = JsonSerializer.Deserialize<WidgetDataView>(gridData.Body, Json)!;
                Assert.Equal((DataClassification.Restricted, false, "[]"), (refused.Classification, refused.RowsAllowed, refused.RowsJson));
                Assert.DoesNotContain("ada@example.com", gridData.Body);
                Assert.All(refused.Summary.Columns, column => Assert.Empty(column.Samples));

                // An overlapping widget is refused with the board's own reason; a widget outside the grid too.
                var overlap = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{board.Id}/widgets", new AddWidgetRequest { Title = "Clash", Kind = WidgetKind.Kpi, DatasetId = sales.Id, Column = 2, Row = 1, ColumnSpan = 4, RowSpan = 3 }, output, "an overlapping widget");
                Assert.Equal(HttpStatusCode.BadRequest, overlap.Status);
                Assert.Contains("overlap", overlap.Body, StringComparison.OrdinalIgnoreCase);
                var outside = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{board.Id}/widgets", new AddWidgetRequest { Title = "Far", Kind = WidgetKind.Kpi, DatasetId = sales.Id, Column = 10, Row = 8, ColumnSpan = 4, RowSpan = 3 }, output, "a widget outside the grid");
                Assert.Equal(HttpStatusCode.BadRequest, outside.Status);

                // The dataset a widget binds cannot be deleted: the boards answer the Datasets delete guard.
                var deleteSales = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Datasets/{sales.Id}", null, output, "delete a bound dataset");
                Assert.NotEqual(HttpStatusCode.NoContent, deleteSales.Status);
                Assert.Contains("Revenue", deleteSales.Body);

                // Rename, layout, grid state, reorder, publish, clone, remove a widget, delete the board.
                board = await Put<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}", new RenameDashboardRequest { Name = "Operations board" }, "rename");
                Assert.Equal("Operations board", board.Name);
                var orphaning = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"{Api}/{board.Id}/layout", new LayoutRequest { Columns = 6, RowHeightPx = 40 }, output, "a layout that orphans the grid");
                Assert.Equal(HttpStatusCode.BadRequest, orphaning.Status);
                board = await Put<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/widgets/{grid.Id}/grid-state", new GridStateRequest { ColumnWidths = new() { new ColumnWidthView("name", 160) }, SortColumn = "name", SortDescending = true }, "save the grid state");
                Assert.Equal(("name", true, 160), (board.Widgets[1].GridState.SortColumn, board.Widgets[1].GridState.SortDescending, board.Widgets[1].GridState.ColumnWidths.Single().WidthPx));
                board = await Post<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/widgets/{chart.Id}/reorder", new ReorderWidgetRequest { Forward = true }, "reorder");
                Assert.Equal(new[] { "Customers", "Revenue" }, board.Widgets.Select(widget => widget.Title));
                board = await Post<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/publish", new { }, "publish");
                Assert.Equal(DashboardStatus.Published, board.Status);
                var clone = await Post<DashboardView>(app, client, cookies, hostId, $"{Api}/{board.Id}/clone", new CloneDashboardRequest { NewName = "Operations (copy)" }, "clone");
                Assert.Equal((DashboardStatus.Draft, 2), (clone.Status, clone.Widgets.Count));
                Assert.DoesNotContain(clone.Widgets, widget => widget.Id == chart.Id);
                var removed = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"{Api}/{board.Id}/widgets/{grid.Id}", null, output, "remove the grid");
                Assert.Equal(HttpStatusCode.OK, removed.Status);
                Assert.Single(JsonSerializer.Deserialize<DashboardView>(removed.Body, Json)!.Widgets);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"{Api}/{board.Id}", null, output, "delete the board")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, $"{Api}/{board.Id}", output, "the deleted board")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<T> Post<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string path, object body, string label)
        {
            var reply = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, path, body, output, label);
            Assert.True(reply.Status == HttpStatusCode.OK, $"{label}: {reply.Status} {reply.Body}");
            return JsonSerializer.Deserialize<T>(reply.Body, Json)!;
        }

        private async Task<T> Put<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string path, object body, string label)
        {
            var reply = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Put, path, body, output, label);
            Assert.True(reply.Status == HttpStatusCode.OK, $"{label}: {reply.Status} {reply.Body}");
            return JsonSerializer.Deserialize<T>(reply.Body, Json)!;
        }
    }
}
