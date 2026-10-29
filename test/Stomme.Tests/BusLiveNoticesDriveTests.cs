#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Dashboards;
using Stomme.Datasets;
using Stomme.Models.Pipelines;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// With the message bus switched on, the framework's published notices still arrive. They were published on the
    /// bus and nothing subscribed their topics, so a SignalR client received no collaboration notice, a dashboard no dataset
    /// change, and a pipeline's Event trigger never started - and nothing was logged. Driven on an installed host with
    /// <c>Messaging:Enabled</c> (the in-memory transport): an editor's lock reaches the collaboration hub, a dataset refresh
    /// reaches the dashboards hub, and an event raised through the site's endpoint starts its pipeline.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BusLiveNoticesDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task With_the_bus_on_the_collaboration_dataset_and_pipeline_event_notices_are_delivered()
        {
            var settings = new JsonObject
            {
                ["Messaging"] = new JsonObject { ["Enabled"] = true, ["InputQueue"] = "stomme-bl249-" + Guid.NewGuid().ToString("N")[..8] },
                ["Transport"] = new JsonObject { ["SignalR"] = new JsonObject { ["Enabled"] = true } },
                ["Collaboration"] = new JsonObject { ["Enabled"] = true },
                ["Pipelines"] = new JsonObject { ["Enabled"] = true },
                ["Health"] = new JsonObject { ["Enabled"] = false },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // ---- collaboration: a lock taken reaches the hub -------------------------------------------------------------
                var collaboration = new BlockingCollection<JsonElement>();
                await using var collaborationHub = Connect(app, cookies, HubRoutes.Collaboration);
                collaborationHub.On<JsonElement>(CollaborationHubMethods.Changed, message => collaboration.Add(message.Clone()));
                await collaborationHub.StartAsync();
                var board = Ok<DashboardView>(await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Dashboards", new CreateDashboardRequest { Name = "Bus board", Columns = 12, RowHeightPx = 40 }, "create the board"), "create the board");
                var boardId = board.Id.ToString("D");
                var locked = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Collaboration/locks/acquire", new LockRequest(CollaborativeResourceTypes.Dashboard, boardId, LockScope.Resource, 300), "take the lock");
                Assert.Equal(HttpStatusCode.OK, locked.Status);
                var notice = Next(collaboration, "the lock's notice");
                Assert.Equal((boardId, CollaborationNoticeKinds.Lock), (notice.GetProperty("resourceId").GetString(), notice.GetProperty("kind").GetString()));

                // ---- dashboards: a dataset refresh reaches the hub -----------------------------------------------------------
                var registered = Ok<DatasetDescriptor>(await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Datasets",
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-sales", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" } ]", Classification = DataClassification.Internal },
                    "register Sales"), "register Sales");
                var dashboards = new BlockingCollection<JsonElement>();
                await using var dashboardHub = Connect(app, cookies, HubRoutes.Dashboards);
                dashboardHub.On<JsonElement>(DashboardHubMethods.DatasetChanged, message => dashboards.Add(message.Clone()));
                await dashboardHub.StartAsync();
                var refreshed = await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Datasets/{registered.Id}/refresh",
                    new RefreshDatasetRequest { SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"12\" } ]" }, "refresh");
                Assert.Equal(HttpStatusCode.OK, refreshed.Status);
                var change = Next(dashboards, "the refresh's change");
                Assert.Equal((registered.Id, "Refreshed"), (change.GetProperty("datasetId").GetGuid(), change.GetProperty("kind").GetString()));

                // ---- pipelines: an event raised through the site's endpoint starts its pipeline --------------------------------
                var evented = Ok<PipelineView>(await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "On order paid" }, "create the pipeline"), "create the pipeline");
                Ok<PipelineView>(await Send(app, client, cookies, hostId, HttpMethod.Put, $"/api/Pipelines/{evented.DefinitionId}/graph", new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "e", Type = "Stomme.Event", Name = "Order paid", Inputs = { ["EventName"] = "order.paid" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "paid", ["Level"] = "Information" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "e", TargetId = "log" } },
                }, "save the graph"), "save the graph");
                Assert.True(Ok<PublishResult>(await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{evented.DefinitionId}/publish", new { }, "publish"), "publish").Succeeded);
                var raised = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Pipelines/events/order.paid?correlationId=bus-249", new { order = 249 }, "raise the event");
                Assert.Equal(HttpStatusCode.Accepted, raised.Status);
                Assert.Equal("bus-249", await UntilRunAsync(client, cookies, evented.DefinitionId));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string?> UntilRunAsync(HttpClient client, CookieContainer cookies, string definitionId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "the event's run");
                Assert.Equal(HttpStatusCode.OK, answer.Status);
                var runs = JsonSerializer.Deserialize<List<PipelineRunView>>(answer.Body, Json)!;
                if (runs.Count > 0)
                {
                    return runs[0].CorrelationId;
                }

                await Task.Delay(200);
            }

            throw new Xunit.Sdk.XunitException("The event raised with the bus on started no run within 30 s.");
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private static T Ok<T>((HttpStatusCode Status, string Body) answer, string label)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        private static JsonElement Next(BlockingCollection<JsonElement> received, string what)
        {
            Assert.True(received.TryTake(out var message, TimeSpan.FromSeconds(15)), $"Nothing was pushed within 15 s: {what}.");
            return message;
        }

        private static HubConnection Connect(WebApplication app, CookieContainer cookies, string route)
        {
            var url = new Uri("http://" + Host + route);
            return new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                    var header = cookies.GetCookieHeader(url);
                    if (!string.IsNullOrEmpty(header))
                    {
                        options.Headers["Cookie"] = header;
                    }
                })
                .Build();
        }
    }
}
