#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Connectivity;
using Stomme.Datasets;
using Stomme.Modules.CloudUsage.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.CloudUsage
{
    /// <summary>
    /// BL-97 increment 4 - CloudUsage DRIVEN over HTTP on installed hosts on SQLite and PostgreSQL: the page served from the
    /// module's template; an <c>azurecost</c> connection created through the Connections API with its token in the site's
    /// secret store; a sync through <c>api/CloudUsage</c> recording the site's rows and refreshing a dataset the Datasets API
    /// then lists; the rollups and a budget read back over HTTP; a <c>rest</c> connection refused for the Azure source by
    /// name; a plain user refused at the door; the token in no response body.
    /// </summary>
    /// <remarks>
    /// The billing API itself is the one thing not reached: the host's egress refuses loopback (the SSRF guard), and nothing
    /// here may reach the internet. The payload source is replaced BEFORE the host composes (the module's registration is a
    /// TryAdd), and it asserts it was handed the host's egress and the credential from the secret store - the network half
    /// is <c>ConnectorCostPayloadSourceTests</c>'.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class CloudUsageModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/CloudUsage";
        private const string Token = "drive-billing-token-7f3a";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Cloud_usage_syncs_through_a_cost_connection_and_rolls_up_per_site(string storeName)
        {
            var fixture = new FixturePayloadSource();
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<ICloudCostPayloadSource>(fixture));
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var bodies = new List<string>();

                // ---- the page, served from the module's own template ------------------------------------------------------
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<Stomme.Repository.IPageRepository>().GetPageAsync("admin/cloud-usage", alias.SiteId)));
                var page = await GateDrive.GetAsync(client, cookies, Host, "/admin/cloud-usage", output, "the page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Cloud Usage", page.Body, StringComparison.OrdinalIgnoreCase);

                // ---- two connections: the Azure cost connector with its token, and a REST one --------------------------------
                var azure = await Post<ConnectionView>(app, client, cookies, hostId, "/api/Connections", new ConnectionRequest
                {
                    Name = "Azure billing", ConnectorType = "azurecost", Endpoint = "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.CostManagement/query?api-version=2023-03-01",
                    Settings = new() { ["subscriptionId"] = "sub-1" }, Credentials = new() { ["token"] = Token }, TimeoutSeconds = 10,
                }, "azure cost connection", bodies);
                var rest = await Post<ConnectionView>(app, client, cookies, hostId, "/api/Connections", new ConnectionRequest
                {
                    Name = "Some REST API", ConnectorType = "rest", Endpoint = "https://api.example.test/status", TimeoutSeconds = 10,
                }, "rest connection", bodies);

                // ---- a sync: the rows recorded for the site, a dataset refreshed through the registrar ------------------------
                var synced = await Post<CloudCostSyncView>(app, client, cookies, hostId, $"{Api}/sync",
                    new SyncCloudCostsRequest { ConnectionId = azure.Id, Provider = "azure", TargetDataset = "Azure costs" }, "sync", bodies);
                Assert.True(synced.Success, synced.Reason);
                Assert.Equal((CloudProvider.Azure, 2, "Azure costs"), (synced.Provider!.Value, synced.SyncedRows, synced.Dataset));
                Assert.Equal(Token, fixture.LastCredential); // from the site's secret store
                Assert.True(fixture.HadEgress); // the host's egress, the only way out

                var datasets = await Get<List<DatasetDescriptor>>(client, cookies, "/api/Datasets", "datasets", bodies);
                var costs = Assert.Single(datasets, dataset => dataset.Name == "Azure costs");
                Assert.Equal((OriginKind.ExternalQuery, azure.Id.ToString()), (costs.OriginKind, costs.OriginReference));

                // ---- the rollups and a budget over HTTP -------------------------------------------------------------------------
                var summary = await Get<CloudCostSummaryView>(client, cookies, $"{Api}/summary?from=2026-07-01&to=2026-07-31", "summary", bodies);
                Assert.Equal(2, summary.RecordCount);
                Assert.Equal(("USD", 12.90m), (Assert.Single(summary.TotalsByCurrency).Currency, summary.TotalsByCurrency[0].TotalCost));
                var byService = await Get<CloudCostByServiceView>(client, cookies, $"{Api}/by-service?from=2026-07-01&to=2026-07-31&top=1", "by service", bodies);
                Assert.Equal(("Virtual Machines", CloudServiceCategory.Compute), (Assert.Single(byService.ByService).Service, byService.ByService[0].ServiceCategory));

                var budget = await Send<CloudBudgetView>(app, client, cookies, hostId, HttpMethod.Put, $"{Api}/budgets", new SetCloudBudgetRequest { MonthlyAmount = 10m, Currency = "usd" }, "set a budget", bodies);
                Assert.Equal((10m, "USD"), (budget.MonthlyAmount, budget.Currency));
                var status = await Get<CloudBudgetStatusView>(client, cookies, $"{Api}/budget-status?month=2026-07-01", "budget status", bodies);
                var line = Assert.Single(status.Budgets);
                Assert.Equal((12.90m, 129m), (line.ActualSpend, line.PercentUsed));
                var refusedBudget = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"{Api}/budgets", new SetCloudBudgetRequest { MonthlyAmount = 0m, Currency = "USD" }, output, "a zero budget");
                Assert.Equal(HttpStatusCode.BadRequest, refusedBudget.Status);

                // ---- a connection of another type is refused by name, and nothing is fetched ---------------------------------
                fixture.LastCredential = null;
                var mismatch = await Post<CloudCostSyncView>(app, client, cookies, hostId, $"{Api}/sync", new SyncCloudCostsRequest { ConnectionId = rest.Id, Provider = "azure" }, "sync through rest", bodies);
                Assert.Equal((false, "CONNECTION_TYPE_MISMATCH"), (mismatch.Success, mismatch.Code));
                Assert.Null(fixture.LastCredential);

                // ---- a plain user is refused at the door --------------------------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, $"{Api}/summary", output, "plain summary")).Status);

                // ---- the token never came back in any answer ------------------------------------------------------------------
                Assert.All(bodies, body => Assert.DoesNotContain(Token, body));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<T> Post<T>(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path, object body, string label, List<string> bodies) =>
            await Send<T>(app, client, cookies, userId, HttpMethod.Post, path, body, label, bodies);

        private async Task<T> Send<T>(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object body, string label, List<string> bodies)
        {
            var response = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            bodies.Add(response.Body);
            return JsonSerializer.Deserialize<T>(response.Body, Json)!;
        }

        private async Task<T> Get<T>(HttpClient client, CookieContainer cookies, string path, string label, List<string> bodies)
        {
            var response = await GateDrive.GetAsync(client, cookies, Host, path, output, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            bodies.Add(response.Body);
            return JsonSerializer.Deserialize<T>(response.Body, Json)!;
        }

        /// <summary>the predecessor platform's captured Azure sample, handed back for any window; it records what the sync handed it.</summary>
        private sealed class FixturePayloadSource : ICloudCostPayloadSource
        {
            public string? LastCredential { get; set; }

            public bool HadEgress { get; private set; }

            public Task<string> ReadAsync(CloudCostQuery query, CancellationToken ct = default)
            {
                LastCredential = query.ConnectionSettings.TryGetValue(ConnectorCostPayloadSource.CredentialSettingKey, out var credential) ? credential : null;
                HadEgress = query.Egress is not null;
                return Task.FromResult(CloudUsageApplicationHarness.AzurePayload.Replace("20260714", "20260710", StringComparison.Ordinal));
            }
        }
    }
}
