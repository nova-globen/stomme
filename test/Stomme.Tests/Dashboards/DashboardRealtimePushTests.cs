#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Modules.Dashboards.Realtime;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// The dashboards page's live update on an installed host with the SignalR transport on: a real
    /// hub connection, a real dataset refreshed through the Datasets API, a real push - the dataset's id and the kind of
    /// change, NOTHING else, addressed to the site's group alone; the hub is <c>dashboards:view</c>, so a plain user and
    /// an anonymous caller are refused at negotiate.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class DashboardRealtimePushTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public DashboardRealtimePushTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_dashboard_viewer_is_pushed_the_dataset_change_after_a_refresh_and_only_for_its_own_site()
        {
            var settings = new JsonObject
            {
                ["Transport"] = new JsonObject { ["SignalR"] = new JsonObject { ["Enabled"] = true } },
                ["Health"] = new JsonObject { ["Enabled"] = false },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var alias = await GateDrive.AliasAsync(app, Host);

                var registered = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-sales", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]", Classification = DataClassification.Internal },
                    _output, "register Sales");
                Assert.Equal(HttpStatusCode.OK, registered.Status);
                var datasetId = JsonSerializer.Deserialize<DatasetDescriptor>(registered.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Id;

                var received = new BlockingCollection<JsonElement>();
                await using var hub = Connect(app, cookies);
                hub.On<JsonElement>(DashboardHubMethods.DatasetChanged, message => received.Add(message.Clone()));
                await hub.StartAsync();
                _output.WriteLine($"[hub] connected as host, site key {alias.SiteKey}");

                // ---- a refresh with the same schema pushes ONE change, Refreshed, carrying the id and the kind and nothing else ----
                var refresh = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Datasets/{datasetId}/refresh",
                    new RefreshDatasetRequest { SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"12\" }, { \"sku\": \"B2\", \"amount\": \"3\" } ]" }, _output, "refresh, same schema");
                Assert.Equal(HttpStatusCode.OK, refresh.Status);
                var pushed = Next(received, "the push after the refresh");
                _output.WriteLine($"[hub] received {pushed}");
                Assert.Equal(new[] { "datasetId", "kind" }, pushed.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
                Assert.Equal(datasetId, pushed.GetProperty("datasetId").GetGuid());
                Assert.Equal("Refreshed", pushed.GetProperty("kind").GetString());

                // ---- a refresh that changes the schema pushes SchemaChanged and then Refreshed, in the aggregate's order ----
                var reshape = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Datasets/{datasetId}/refresh",
                    new RefreshDatasetRequest { SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"12\", \"region\": \"north\" } ]" }, _output, "refresh, new column");
                Assert.Equal(HttpStatusCode.OK, reshape.Status);
                Assert.Equal("SchemaChanged", Next(received, "the schema push").GetProperty("kind").GetString());
                Assert.Equal("Refreshed", Next(received, "the refresh push after the schema push").GetProperty("kind").GetString());

                // ---- another site's push, and another tenant's, never arrive: ordered delivery on one connection means the
                //      first message after them must be the one addressed to THIS site. ----------------------------------------
                var push = app.Services.GetRequiredService<IDashboardRealtimePush>();
                Assert.IsType<DashboardRealtimePush>(push);
                var elsewhere = Guid.NewGuid();
                await push.PushAsync(alias.TenantId, alias.SiteId + 1, new DashboardDatasetChange(elsewhere, "Refreshed"));
                await push.PushAsync(alias.TenantId + 1, alias.SiteId, new DashboardDatasetChange(elsewhere, "Refreshed"));
                var here = Guid.NewGuid();
                await push.PushAsync(alias.TenantId, alias.SiteId, new DashboardDatasetChange(here, "Refreshed"));
                Assert.Equal(here, Next(received, "this site's push").GetProperty("datasetId").GetGuid());

                // ---- the hub is dashboards:view, not "any authenticated user" ---------------------------------------------
                await GateDrive.AddUserAsync(app, Host, "dash-hub-plain", "Pla1n!DashHub$2026", "dash-hub-plain@gate.test");
                var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, "dash-hub-plain", "Pla1n!DashHub$2026", _output);
                await using (var plainHub = Connect(app, plainCookies))
                {
                    var refused = await Assert.ThrowsAnyAsync<Exception>(() => plainHub.StartAsync());
                    _output.WriteLine($"[hub] plain user refused: {refused.GetType().Name}: {GateDrive.Truncate(refused.Message)}");
                }

                await using (var anonymousHub = Connect(app, cookies: null))
                {
                    await Assert.ThrowsAnyAsync<Exception>(() => anonymousHub.StartAsync());
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static JsonElement Next(BlockingCollection<JsonElement> received, string what)
        {
            Assert.True(received.TryTake(out var message, TimeSpan.FromSeconds(15)), $"Nothing was pushed within 15 s: {what}.");
            return message;
        }

        private static HubConnection Connect(WebApplication app, CookieContainer? cookies)
        {
            var url = new Uri("http://" + Host + HubRoutes.Dashboards);
            return new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                    var header = cookies?.GetCookieHeader(url);
                    if (!string.IsNullOrEmpty(header))
                    {
                        options.Headers["Cookie"] = header;
                    }
                })
                .Build();
        }
    }
}
