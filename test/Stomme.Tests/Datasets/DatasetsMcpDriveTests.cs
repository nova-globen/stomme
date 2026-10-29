#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Mcp;
using Stomme.Tests.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The datasets over the real MCP endpoint on an installed host, on PostgreSQL and
    /// RavenDB with <c>Mcp:Server:Enabled</c> and the <c>datasets.mcp</c> flag turned on through configuration: the Host
    /// sees the resources and the tool, reads the list and one summary (metadata only), queries an open dataset's rows
    /// and is refused a Restricted one's; a registered user sees neither the tool nor a way to call it. With the flag at
    /// its default (off) the endpoint lists nothing for datasets.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DatasetsMcpDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_datasets_are_readable_over_MCP_and_the_preview_is_classification_gated(string storeName)
        {
            var settings = McpCatalogDriveTests.McpEnabled();
            settings["FeatureManagement"] = new JsonObject { [DatasetsFeatureContributor.Mcp] = true };
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
                Assert.Contains(DatasetsMcpContributor.QueryDataset, tools);
                var resources = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/list", null, output, "host resources");
                Assert.Contains(DatasetsMcpContributor.ListUri, resources?["result"]?["resources"]?.AsArray().Select(resource => resource?["uri"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
                var templates = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/templates/list", null, output, "host templates");
                Assert.Contains(DatasetsMcpContributor.SummaryTemplate, templates?["result"]?["resourceTemplates"]?.AsArray().Select(template => template?["uriTemplate"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());

                // Two datasets through the API: an open one and one the classifier makes Restricted.
                var open = JsonSerializer.Deserialize<DatasetDescriptor>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-1", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]" }, output, "register Sales")).Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var restricted = JsonSerializer.Deserialize<DatasetDescriptor>((await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Customers", OriginKind = OriginKind.Upload, OriginReference = "file-1", SampleJson = "[ { \"contact\": \"ada@example.com\" } ]", Classification = DataClassification.Public }, output, "register Customers")).Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(DataClassification.Restricted, restricted.Classification);

                var list = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = DatasetsMcpContributor.ListUri }, output, "read the list");
                var listText = list?["result"]?["contents"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains("Sales", listText);
                Assert.Contains("Customers", listText);
                Assert.DoesNotContain("ada@example.com", listText);

                var summary = await McpRpc.CallAsync(hostClient, hostCookies, Host, "resources/read", new JsonObject { ["uri"] = $"stomme://datasets/{restricted.Id}/summary" }, output, "read a summary");
                var summaryText = summary?["result"]?["contents"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains("\"classification\":\"Restricted\"", summaryText);
                Assert.DoesNotContain("ada@example.com", summaryText);

                var rows = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = DatasetsMcpContributor.QueryDataset, ["arguments"] = new JsonObject { ["datasetId"] = open.Id.ToString() } }, output, "query Sales");
                var rowsText = rows?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains("\"allowed\":true", rowsText);
                Assert.Contains("A1", rowsText);

                var refused = await McpRpc.CallAsync(hostClient, hostCookies, Host, "tools/call", new JsonObject { ["name"] = DatasetsMcpContributor.QueryDataset, ["arguments"] = new JsonObject { ["datasetId"] = restricted.Id.ToString() } }, output, "query Customers");
                var refusedText = refused?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
                Assert.Contains("\"allowed\":false", refusedText);
                Assert.DoesNotContain("ada@example.com", refusedText);

                // A registered user holds no datasets key: the tool is not listed and a call is refused.
                Assert.DoesNotContain(DatasetsMcpContributor.QueryDataset, await McpRpc.ToolNamesAsync(plainClient, plainCookies, Host, output, "plain tools"));
                var plainCall = await McpRpc.PostAsync(plainClient, plainCookies, Host, McpRpc.Body("tools/call", new JsonObject { ["name"] = DatasetsMcpContributor.QueryDataset, ["arguments"] = new JsonObject { ["datasetId"] = open.Id.ToString() } }), output, "plain call");
                Assert.True(plainCall.Status != HttpStatusCode.OK || plainCall.Body.Contains("error", StringComparison.OrdinalIgnoreCase), $"{(int)plainCall.Status} {plainCall.Body}");
                Assert.DoesNotContain("A1", plainCall.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_flag_at_its_default_the_endpoint_lists_nothing_for_datasets()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);

                Assert.DoesNotContain(DatasetsMcpContributor.QueryDataset, await McpRpc.ToolNamesAsync(client, cookies, Host, output, "tools with the flag off"));
                var resources = await McpRpc.CallAsync(client, cookies, Host, "resources/list", null, output, "resources with the flag off");
                Assert.DoesNotContain(DatasetsMcpContributor.ListUri, resources?["result"]?["resources"]?.AsArray().Select(resource => resource?["uri"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
