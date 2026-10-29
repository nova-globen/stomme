#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Dashboards;
using Stomme.Models.Pipelines;
using Stomme.Modules.Collaboration.Realtime;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// Presence and the change feed DRIVEN on an installed host with the SignalR transport on: Bo's hub
    /// connection is told, in order, of Ada's lock, her joining, her announced change, her save of the board through the
    /// Dashboards API, her save of a pipeline's graph, and her giving the lock up - each notice the resource, the kind and who,
    /// NOTHING else, addressed to the site's group alone. The hub is <c>collaboration:view</c>: a plain user and an anonymous
    /// caller are refused at negotiate. The <c>CollaborationHubE2ETests</c> cases, over the REST surface the hub now leaves
    /// every act to.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CollaborationRealtimePushTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Collaboration";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task An_editor_is_pushed_each_thing_another_editor_does_to_the_resource_and_only_for_its_own_site()
        {
            var settings = new JsonObject
            {
                ["Transport"] = new JsonObject { ["SignalR"] = new JsonObject { ["Enabled"] = true } },
                ["Collaboration"] = new JsonObject { ["Enabled"] = true },
                ["Pipelines"] = new JsonObject { ["Enabled"] = true },
                ["Health"] = new JsonObject { ["Enabled"] = false },
            };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var ada = GateDrive.NewClient(app, Host);
                var adaCookies = await GateDrive.LoginAsync(app, ada, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var adaId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, "Administrators");
                using var bo = GateDrive.NewClient(app, Host);
                var boCookies = await GateDrive.LoginAsync(app, bo, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                var alias = await GateDrive.AliasAsync(app, Host);

                var board = Ok<DashboardView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, "/api/Dashboards", new CreateDashboardRequest { Name = "Shared board", Columns = 12, RowHeightPx = 40 }, "create the board"), "create the board");
                var boardId = board.Id.ToString("D");

                var received = new BlockingCollection<JsonElement>();
                await using var hub = Connect(app, boCookies);
                hub.On<JsonElement>(CollaborationHubMethods.Changed, message => received.Add(message.Clone()));
                await hub.StartAsync();
                output.WriteLine($"[hub] Bo connected, site key {alias.SiteKey}");

                // ---- Ada's lock, her joining, her change: one notice each, of its kind, naming the board and Ada -------------------
                var lockRequest = new LockRequest(CollaborativeResourceTypes.Dashboard, boardId, LockScope.Resource, 300);
                Ok<ResourceLockResult>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/locks/acquire", lockRequest, "Ada takes the lock"), "Ada takes the lock");
                var locked = Next(received, "the lock");
                Assert.Equal(new[] { "byUserId", "kind", "resourceId", "resourceType" }, locked.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
                Assert.Equal(("dashboard", boardId, CollaborationNoticeKinds.Lock, adaId), Read(locked));

                var session = Ok<EditSessionView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions", new JoinSessionRequest(CollaborativeResourceTypes.Dashboard, boardId), "Ada joins"), "Ada joins");
                Assert.Equal(("dashboard", boardId, CollaborationNoticeKinds.Presence, adaId), Read(Next(received, "Ada's presence")));
                Ok<EditSessionView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions/{session.Id}/changes", new ChangeRequest("layout changed"), "Ada announces"), "Ada announces");
                Assert.Equal(("dashboard", boardId, CollaborationNoticeKinds.Change, adaId), Read(Next(received, "Ada's change")));

                // ---- the resource stored by the module that owns it: a board renamed, a pipeline's graph saved ---------------------
                Assert.Equal(HttpStatusCode.OK, (await Send(app, ada, adaCookies, adaId, HttpMethod.Put, $"/api/Dashboards/{boardId}", new RenameDashboardRequest { Name = "Renamed board" }, "Ada renames the board")).Status);
                Assert.Equal(("dashboard", boardId, CollaborationNoticeKinds.Saved, adaId), Read(Next(received, "the board saved")));

                var pipeline = Ok<PipelineView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Shared pipeline" }, "create a pipeline"), "create a pipeline");
                var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Inputs = { ["Text"] = "hi" } } } };
                Assert.Equal(HttpStatusCode.OK, (await Send(app, ada, adaCookies, adaId, HttpMethod.Put, $"/api/Pipelines/{pipeline.DefinitionId}/graph", graph, "Ada saves the graph")).Status);
                Assert.Equal(("pipeline", pipeline.DefinitionId, CollaborationNoticeKinds.Saved, adaId), Read(Next(received, "the pipeline saved")));

                // ---- Ada leaves: her presence, then her lock given up as the last one out ------------------------------------------
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions/{session.Id}/leave", null, "Ada leaves")).Status);
                var afterLeave = new[] { Read(Next(received, "the leave, first")), Read(Next(received, "the leave, second")) };
                Assert.Contains(("dashboard", boardId, CollaborationNoticeKinds.Presence, adaId), afterLeave);
                Assert.Contains(("dashboard", boardId, CollaborationNoticeKinds.Lock, adaId), afterLeave);

                // ---- another site's notice, and another tenant's, never arrive: on one connection the first message after them
                //      must be the one addressed to THIS site. --------------------------------------------------------------------
                var push = app.Services.GetRequiredService<ICollaborationRealtimePush>();
                Assert.IsType<CollaborationRealtimePush>(push);
                await push.PushAsync(alias.TenantId, alias.SiteId + 1, new CollaborationNotice("dashboard", "elsewhere", CollaborationNoticeKinds.Saved, 1));
                await push.PushAsync(alias.TenantId + 1, alias.SiteId, new CollaborationNotice("dashboard", "elsewhere", CollaborationNoticeKinds.Saved, 1));
                await push.PushAsync(alias.TenantId, alias.SiteId, new CollaborationNotice("dashboard", "here", CollaborationNoticeKinds.Saved, 1));
                Assert.Equal("here", Next(received, "this site's notice").GetProperty("resourceId").GetString());

                // ---- the hub is collaboration:view, not "any authenticated user" -----------------------------------------------------
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                await using (var plainHub = Connect(app, plainCookies))
                {
                    var refused = await Assert.ThrowsAnyAsync<Exception>(() => plainHub.StartAsync());
                    output.WriteLine($"[hub] plain user refused: {refused.GetType().Name}: {GateDrive.Truncate(refused.Message)}");
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

        private static (string, string, string, int) Read(JsonElement notice) =>
            (notice.GetProperty("resourceType").GetString()!, notice.GetProperty("resourceId").GetString()!, notice.GetProperty("kind").GetString()!, notice.GetProperty("byUserId").GetInt32());

        private static JsonElement Next(BlockingCollection<JsonElement> received, string what)
        {
            Assert.True(received.TryTake(out var message, TimeSpan.FromSeconds(15)), $"Nothing was pushed within 15 s: {what}.");
            return message;
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private static T Ok<T>((HttpStatusCode Status, string Body) answer, string label)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        private static HubConnection Connect(WebApplication app, CookieContainer? cookies)
        {
            var url = new Uri("http://" + Host + HubRoutes.Collaboration);
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
