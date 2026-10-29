#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Datasets;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DemoContent
{
    /// <summary>
    /// The demo set over HTTP on installed hosts on PostgreSQL and RavenDB, across a
    /// restart: the two connections listed with their connector installed (the demo types install `rest` and
    /// `postgresql`), the PostgreSQL one refused for its never-stored password (a verdict the restart keeps) and the
    /// REST one left Untested (a Test would reach the internet); the three datasets listed with the customers set
    /// Restricted, no address in the body and its preview refused, the sales set previewed; the second boot adding nothing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DemoContentDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_demo_set_is_served_over_http_and_survives_a_restart(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["DemoContent"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await AssertDemoSetAsync(app, "first boot", firstBoot: true);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }

            app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await AssertDemoSetAsync(app, "second boot", firstBoot: false);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task AssertDemoSetAsync(Microsoft.AspNetCore.Builder.WebApplication app, string label, bool firstBoot)
        {
            using var client = GateDrive.NewClient(app, Host);
            var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
            var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

            var connections = JsonSerializer.Deserialize<List<ConnectionView>>((await GateDrive.GetAsync(client, cookies, Host, "/api/Connections", output, label + " connections")).Body, Json)!;
            Assert.Equal(new[] { "Demo PostgreSQL", "Demo REST API" }, connections.Select(c => c.Name).OrderBy(n => n, System.StringComparer.Ordinal));
            // The two demo types install their connectors, so a Test is decided by the runner, not by "not installed".
            Assert.All(connections, c => Assert.True(c.ConnectorInstalled, c.Name));
            var rest = connections.Single(c => c.Name == "Demo REST API");
            var postgres = connections.Single(c => c.Name == "Demo PostgreSQL");
            Assert.Equal("Untested", rest.Status); // never tested by this drive: it would reach the internet
            // The first boot's Test below records Failing on the PostgreSQL connection (its password was never stored); the second boot finds that verdict kept.
            Assert.Equal(firstBoot ? "Untested" : "Failing", postgres.Status);

            var test = JsonSerializer.Deserialize<ConnectionTestView>((await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Connections/{postgres.Id}/test", null, output, label + " test")).Body, Json)!;
            Assert.Equal((false, ConnectorOutcome.CredentialsMissing), (test.Success, test.Outcome));

            var datasetsResponse = await GateDrive.GetAsync(client, cookies, Host, "/api/Datasets", output, label + " datasets");
            var datasets = JsonSerializer.Deserialize<List<DatasetDescriptor>>(datasetsResponse.Body, Json)!;
            Assert.Equal(new[] { "Cloud budget status", "Demo customers", "Demo inventory", "Demo sales", "Multi-cloud cost", "Underutilized resources" }, datasets.Select(d => d.Name).OrderBy(n => n, System.StringComparer.Ordinal)); // with the cloud cost board's three
            Assert.DoesNotContain("@example.com", datasetsResponse.Body);
            var customers = datasets.Single(d => d.Name == "Demo customers");
            Assert.Equal((DataClassification.Restricted, true), (customers.Classification, customers.IsSensitive));

            var refused = JsonSerializer.Deserialize<DatasetPreviewRows>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Datasets/{customers.Id}/preview", output, label + " preview customers")).Body, Json)!;
            Assert.False(refused.Allowed);
            var sales = datasets.Single(d => d.Name == "Demo sales");
            var previewed = JsonSerializer.Deserialize<DatasetPreviewRows>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Datasets/{sales.Id}/preview", output, label + " preview sales")).Body, Json)!;
            Assert.True(previewed.Allowed);
            Assert.Contains("A-100", previewed.RowsJson);
        }
    }
}
