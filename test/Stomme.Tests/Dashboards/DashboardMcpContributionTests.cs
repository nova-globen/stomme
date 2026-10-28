#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.FeatureManagement.Application;
using Stomme.Mcp;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// BL-96 increment 11 - the one read-only <c>IMcpContributor</c> over the module's real startup on the File store:
    /// the descriptors carry the feature flag AND the view key, the list and dashboard resources project the definition
    /// (never data), unknown or malformed ids are null, <c>get_widget_data</c> answers what the page's widget shows with
    /// the rows only when the classification allows them, another site's board is not found, and the expected failures
    /// are error results. The flag is declared by discovery and DEFAULT OFF.
    /// </summary>
    public sealed class DashboardMcpContributionTests
    {
        private const string Contributor = "stomme.dashboards";

        [Fact]
        public void The_flag_is_declared_by_a_discoverable_contributor_and_off_by_default()
        {
            var contributor = (IFeatureContributor)Activator.CreateInstance(typeof(DashboardsFeatureContributor))!;
            var flag = Assert.Single(contributor.Features);
            Assert.Equal((DashboardsFeatureContributor.Mcp, false, "MCP"), (flag.Key, flag.DefaultEnabled, flag.Category));
        }

        [Fact]
        public async Task Descriptors_carry_the_flag_and_the_view_key_and_the_tool_is_auto_and_not_mutating()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var contributor = await ContributorAsync(harness);

            Assert.Equal(new[] { DashboardsMcpContributor.ListUri, DashboardsMcpContributor.DashboardTemplate }, contributor.Resources.Select(resource => resource.Uri));
            Assert.All(contributor.Resources, resource =>
            {
                var gate = Assert.Single(resource.Requires);
                Assert.Equal((DashboardsFeatureContributor.Mcp, DashboardPermissions.View), (gate.Feature, gate.Permission));
            });
            var tool = Assert.Single(contributor.Tools);
            Assert.Equal(DashboardsMcpContributor.GetWidgetData, tool.Name);
            var toolGate = Assert.Single(tool.Requires);
            Assert.Equal((DashboardsFeatureContributor.Mcp, DashboardPermissions.View), (toolGate.Feature, toolGate.Permission));
            Assert.Equal(McpToolApproval.Auto, tool.Approval);
            Assert.False(McpToolNames.IsMutating(tool.Name));
            Assert.True(McpToolNames.IsCanonical(tool.Name));
            Assert.Null(contributor.ReaderFor("stomme://elsewhere"));
            Assert.Null(contributor.InvokerFor("delete_dashboard"));
        }

        [Fact]
        public async Task The_list_and_dashboard_resources_project_the_definition_and_never_a_row()
        {
            var datasetId = Guid.NewGuid();
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Internal, "sku", "amount"));
            catalog.Rows = new DatasetPreviewRows(true, "[ { \"sku\": \"SECRET-ROW\", \"amount\": \"10\" } ]", "OK");
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync("Operations");
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Revenue", WidgetKind.Grid, datasetId, new[] { "sku", "amount" }, 0, 0, 6, 4, RefreshKind.Interval, 60));
            await harness.CreateBoardAsync("Another");

            var list = await ReadAsync(harness, DashboardsMcpContributor.ListUri, DashboardsMcpContributor.ListUri, new Dictionary<string, string>());
            Assert.NotNull(list);
            Assert.Equal("application/json", list.MimeType);
            using (var json = JsonDocument.Parse(list.Text))
            {
                Assert.Equal(new[] { "Another", "Operations" }, json.RootElement.EnumerateArray().Select(board => board.GetProperty("name").GetString()));
            }

            Assert.DoesNotContain("SECRET-ROW", list.Text);

            var one = await ReadAsync(harness, DashboardsMcpContributor.DashboardTemplate, $"stomme://dashboards/{boardId}", new Dictionary<string, string> { ["dashboardId"] = boardId.ToString() });
            Assert.NotNull(one);
            using (var json = JsonDocument.Parse(one.Text))
            {
                Assert.Equal("Draft", json.RootElement.GetProperty("status").GetString());
                var widget = Assert.Single(json.RootElement.GetProperty("widgets").EnumerateArray());
                Assert.Equal((widgetId, "Grid", datasetId, "Interval", 60), (widget.GetProperty("id").GetGuid(), widget.GetProperty("kind").GetString(), widget.GetProperty("datasetId").GetGuid(), widget.GetProperty("refresh").GetString(), widget.GetProperty("intervalSeconds").GetInt32()));
                Assert.Equal(new[] { "sku", "amount" }, widget.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
            }

            Assert.DoesNotContain("SECRET-ROW", one.Text);
        }

        [Fact]
        public async Task An_unknown_or_malformed_dashboard_id_is_null()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            Assert.Null(await ReadAsync(harness, DashboardsMcpContributor.DashboardTemplate, $"stomme://dashboards/{Guid.NewGuid()}", new Dictionary<string, string> { ["dashboardId"] = Guid.NewGuid().ToString() }));
            Assert.Null(await ReadAsync(harness, DashboardsMcpContributor.DashboardTemplate, "stomme://dashboards/not-a-guid", new Dictionary<string, string> { ["dashboardId"] = "not-a-guid" }));
        }

        [Theory]
        [InlineData(DataClassification.Internal, true)]
        [InlineData(DataClassification.Confidential, false)]
        [InlineData(DataClassification.Restricted, false)]
        public async Task Get_widget_data_answers_what_the_widget_shows_with_the_rows_only_when_the_classification_allows(DataClassification classification, bool rowsExpected)
        {
            var datasetId = Guid.NewGuid();
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, classification, "sku", "amount"));
            catalog.Rows = rowsExpected ? new DatasetPreviewRows(true, "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]", "OK") : new DatasetPreviewRows(false, "[]", "Refused: " + classification);
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var boardId = await harness.CreateBoardAsync();
            var widgetId = await harness.SendAsync(new AddWidgetCommand(boardId, "Revenue", WidgetKind.Grid, datasetId, new[] { "sku" }, 0, 0, 6, 4));

            var result = await InvokeAsync(harness, $$"""{"dashboardId":"{{boardId}}","widgetId":"{{widgetId}}"}""");

            Assert.False(result.IsError);
            using var json = JsonDocument.Parse(result.ContentJson);
            Assert.Equal((datasetId, classification.ToString(), rowsExpected), (json.RootElement.GetProperty("datasetId").GetGuid(), json.RootElement.GetProperty("classification").GetString(), json.RootElement.GetProperty("rowsAllowed").GetBoolean()));
            Assert.Equal(rowsExpected ? 1 : 0, json.RootElement.GetProperty("rows").GetArrayLength());
            if (!rowsExpected)
            {
                Assert.DoesNotContain("A1", result.ContentJson);
            }
        }

        [Fact]
        public async Task Another_sites_board_is_not_listed_not_read_and_not_queried()
        {
            var datasetId = Guid.NewGuid();
            var catalog = new StubDatasetCatalog().With(StubDatasetCatalog.Descriptor(datasetId, DataClassification.Internal, "sku"));
            await using var harness = await DashboardsApplicationHarness.StartAsync(catalog);
            var theirs = await harness.InScopeAsync(async sp =>
            {
                var board = Stomme.Modules.Dashboards.Domain.DashboardAggregate.Create(Guid.NewGuid(), Stomme.Tenancy.Domain.TenantId.Default, DashboardsApplicationHarness.SiteId + 1, "Theirs", Stomme.Modules.Dashboards.Domain.LayoutGrid.Default, DashboardsApplicationHarness.Start);
                await sp.GetRequiredService<Stomme.Dashboards.Persistence.IDashboardRepository>().AddAsync(board);
                await sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
                return board.Id;
            });
            var mine = await harness.CreateBoardAsync("Mine");

            var list = await ReadAsync(harness, DashboardsMcpContributor.ListUri, DashboardsMcpContributor.ListUri, new Dictionary<string, string>());
            using var json = JsonDocument.Parse(list!.Text);
            Assert.Equal(new[] { mine }, json.RootElement.EnumerateArray().Select(board => board.GetProperty("id").GetGuid()));
            Assert.Null(await ReadAsync(harness, DashboardsMcpContributor.DashboardTemplate, $"stomme://dashboards/{theirs}", new Dictionary<string, string> { ["dashboardId"] = theirs.ToString() }));
            var call = await InvokeAsync(harness, $$"""{"dashboardId":"{{theirs}}","widgetId":"{{Guid.NewGuid()}}"}""");
            Assert.True(call.IsError);
            Assert.DoesNotContain("Theirs", call.ContentJson);
        }

        [Fact]
        public async Task Expected_failures_are_error_results()
        {
            await using var harness = await DashboardsApplicationHarness.StartAsync();
            var boardId = await harness.CreateBoardAsync();

            Assert.True((await InvokeAsync(harness, "not json")).IsError);
            Assert.True((await InvokeAsync(harness, """{"dashboardId":"x","widgetId":"y"}""")).IsError);
            var missingWidget = await InvokeAsync(harness, $$"""{"dashboardId":"{{boardId}}","widgetId":"{{Guid.NewGuid()}}"}""");
            Assert.True(missingWidget.IsError);
            Assert.Contains("is not on this dashboard", missingWidget.ContentJson);
        }

        private static Task<IMcpContributor> ContributorAsync(DashboardsApplicationHarness harness) =>
            harness.InScopeAsync(sp => Task.FromResult(sp.GetServices<IMcpContributor>().Single(contributor => contributor.ContributorId == Contributor)));

        private static Task<McpResourceContent?> ReadAsync(DashboardsApplicationHarness harness, string template, string uri, IReadOnlyDictionary<string, string> arguments) =>
            harness.InScopeAsync(async sp =>
            {
                var contributor = sp.GetServices<IMcpContributor>().Single(candidate => candidate.ContributorId == Contributor);
                return await contributor.ReaderFor(template)!.ReadAsync(uri, arguments);
            });

        private static Task<McpToolResult> InvokeAsync(DashboardsApplicationHarness harness, string argumentsJson) =>
            harness.InScopeAsync(async sp =>
            {
                var contributor = sp.GetServices<IMcpContributor>().Single(candidate => candidate.ContributorId == Contributor);
                return await contributor.InvokerFor(DashboardsMcpContributor.GetWidgetData)!.InvokeAsync(argumentsJson);
            });
    }
}
