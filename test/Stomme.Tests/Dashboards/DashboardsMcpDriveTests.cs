#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Modules.Dashboards.Mcp;
using Stomme.Tests.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// The dashboards' MCP surface DRIVEN over HTTP on installed hosts on SQLite and PostgreSQL
    /// with <c>Mcp:Server:Enabled</c> and the <c>dashboards.mcp</c> flag turned on through configuration: the Host
    /// lists the tool and the resources, reads the list and one board, calls <c>get_widget_data</c> for a widget over
    /// an open dataset (rows) and one over a Restricted dataset (refused, the address never in the body); a plain user
    /// holding neither key is refused; and with the flag at its default the endpoint lists nothing for dashboards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DashboardsMcpDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_dashboards_are_readable_over_MCP_and_the_widget_data_is_classification_gated(string storeName)
        {
            var settings = McpCatalogDriveTests.McpEnabled();
            settings["FeatureManagement"] = new JsonObject { [DashboardsFeatureContributor.Mcp] = true };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var tools = await McpRpc.ToolNamesAsync(hostClient, hostCookies, Host, output, "host tools");
                Assert.Contains(DashboardsMcpContributor.GetWidgetData, tools);
                var resources = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/list", null, output, "host resources");
                Assert.Contains(DashboardsMcpContributor.ListUri, resources?["result"]?["resources"]?.AsArray().Select(resource => resource?["uri"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
                var templates = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/templates/list", null, output, "host templates");
                Assert.Contains(DashboardsMcpContributor.DashboardTemplate, templates?["result"]?["resourceTemplates"]?.AsArray().Select(template => template?["uriTemplate"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());

                // Two datasets and a board with a widget over each, through the APIs.
                var open = JsonSerializer.Deserialize<DatasetDescriptor>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-1", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]" }, output, "register Sales")).Body, Json)!;
                var restricted = JsonSerializer.Deserialize<DatasetDescriptor>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Customers", OriginKind = OriginKind.Upload, OriginReference = "file-1", SampleJson = "[ { \"contact\": \"ada@example.com\" } ]", Classification = DataClassification.Public }, output, "register Customers")).Body, Json)!;
                Assert.Equal(DataClassification.Restricted, restricted.Classification);
                var board = JsonSerializer.Deserialize<DashboardView>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Dashboards", new CreateDashboardRequest { Name = "Operations" }, output, "create the board")).Body, Json)!;
                board = JsonSerializer.Deserialize<DashboardView>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, $"/api/Dashboards/{board.Id}/widgets",
                    new AddWidgetRequest { Title = "Sales", Kind = WidgetKind.Grid, DatasetId = open.Id, Columns = new() { "sku", "amount" }, Column = 0, Row = 0, ColumnSpan = 6, RowSpan = 4 }, output, "add the sales grid")).Body, Json)!;
                board = JsonSerializer.Deserialize<DashboardView>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, $"/api/Dashboards/{board.Id}/widgets",
                    new AddWidgetRequest { Title = "Customers", Kind = WidgetKind.Grid, DatasetId = restricted.Id, Columns = new() { "contact" }, Column = 6, Row = 0, ColumnSpan = 6, RowSpan = 4 }, output, "add the customers grid")).Body, Json)!;
                var salesWidget = board.Widgets.Single(widget => widget.Title == "Sales");
                var customersWidget = board.Widgets.Single(widget => widget.Title == "Customers");

                var list = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = DashboardsMcpContributor.ListUri }, output, "read the list");
                var listText = list?["result"]?["contents"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains("Operations", listText);
                Assert.DoesNotContain("@example.com", listText);
                var one = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = $"stomme://dashboards/{board.Id}" }, output, "read the board");
                var oneText = one?["result"]?["contents"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains(salesWidget.Id.ToString(), oneText);

                var rows = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = DashboardsMcpContributor.GetWidgetData, ["arguments"] = new JsonObject { ["dashboardId"] = board.Id.ToString(), ["widgetId"] = salesWidget.Id.ToString() } }, output, "sales widget data");
                var rowsText = ToolText(rows);
                Assert.Contains("\"rowsAllowed\":true", rowsText);
                Assert.Contains("A1", rowsText);
                var refused = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = DashboardsMcpContributor.GetWidgetData, ["arguments"] = new JsonObject { ["dashboardId"] = board.Id.ToString(), ["widgetId"] = customersWidget.Id.ToString() } }, output, "customers widget data");
                var refusedText = ToolText(refused);
                Assert.Contains("\"rowsAllowed\":false", refusedText);
                Assert.DoesNotContain("@example.com", refusedText);

                var plainCall = await McpRpc.PostAsync(plainClient, plainCookies, Host, McpRpc.Body("tools/call", new JsonObject { ["name"] = DashboardsMcpContributor.GetWidgetData, ["arguments"] = new JsonObject { ["dashboardId"] = board.Id.ToString(), ["widgetId"] = salesWidget.Id.ToString() } }), output, "plain call");
                output.WriteLine($"[plain] {plainCall.Status}: {GateDrive.Truncate(plainCall.Body)}");
                Assert.DoesNotContain("A1", plainCall.Body);
                Assert.DoesNotContain("\"rowsAllowed\":true", plainCall.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_flag_at_its_default_the_endpoint_lists_nothing_for_dashboards()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                Assert.DoesNotContain(DashboardsMcpContributor.GetWidgetData, await McpRpc.ToolNamesAsync(client, cookies, Host, output, "tools with the flag off"));
                var resources = await McpRpc.CallAsync(client, cookies, Host, "resources/list", null, output, "resources with the flag off");
                Assert.DoesNotContain(DashboardsMcpContributor.ListUri, resources?["result"]?["resources"]?.AsArray().Select(resource => resource?["uri"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string ToolText(JsonNode? call) =>
            call?["result"]?["content"]?.AsArray().Select(item => item?["text"]?.GetValue<string>()).FirstOrDefault(text => text is not null)
            ?? call?["result"]?["structuredContent"]?.ToJsonString()
            ?? call?.ToJsonString() ?? string.Empty;
    }
}
