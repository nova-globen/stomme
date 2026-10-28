#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Dashboards;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// BL-97 increment 11 - collaboration DRIVEN on installed hosts on SQLite and PostgreSQL, over HTTP, with TWO signed-in
    /// editors (D114's honest limit, met at the API): the host takes a board's lock and the second administrator is told who
    /// holds it; both join ONE session; the holder's change is seen by the other, whose own change is refused for want of a
    /// lock; presence moves; the overview shows the lock and the session; an administrator takes the lock over and the other
    /// then gets it; a board that does not exist and a malformed reference are refused; the last one out closes the session
    /// and gives up their locks; a user without the keys is refused. the predecessor platform's <c>CollaborationApiTests</c> covered the same
    /// surface over its in-memory host.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class CollaborationDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Collaboration";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Two_editors_are_coordinated_on_one_board_and_an_administrator_takes_the_lock_over(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(
                LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["Collaboration"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/collaboration", alias.SiteId)));

                using var ada = GateDrive.NewClient(app, Host);
                var adaCookies = await GateDrive.LoginAsync(app, ada, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var adaId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, "Administrators");
                using var bo = GateDrive.NewClient(app, Host);
                var boCookies = await GateDrive.LoginAsync(app, bo, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                var boId = await GateDrive.UserIdAsync(app, Host, GateDrive.GateUsername);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);

                // The board both will edit, through the Dashboards API.
                var board = await Ok<DashboardView>(app, ada, adaCookies, adaId, HttpMethod.Post, "/api/Dashboards", new CreateDashboardRequest { Name = "Shared board", Columns = 12, RowHeightPx = 40 }, "create the board");
                var lockRequest = new LockRequest(CollaborativeResourceTypes.Dashboard, board.Id.ToString(), LockScope.Resource, 300);

                // Ada takes the lock; Bo is TOLD who holds it - a 200, never an error.
                var taken = await Ok<ResourceLockResult>(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/locks/acquire", lockRequest, "Ada takes the lock");
                Assert.Equal((true, adaId), (taken.Acquired, taken.HolderUserId));
                var told = await Ok<ResourceLockResult>(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/locks/acquire", lockRequest, "Bo asks for it");
                Assert.Equal((false, adaId, taken.HolderDisplayName), (told.Acquired, told.HolderUserId, told.HolderDisplayName));

                // Both join ONE session.
                var boSession = await Ok<EditSessionView>(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions", new JoinSessionRequest(CollaborativeResourceTypes.Dashboard, board.Id.ToString()), "Bo joins");
                var adaSession = await Ok<EditSessionView>(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions", new JoinSessionRequest(CollaborativeResourceTypes.Dashboard, board.Id.ToString()), "Ada joins");
                Assert.Equal(boSession.Id, adaSession.Id);
                Assert.Equal(new[] { boId, adaId }, adaSession.Participants.Select(participant => participant.UserId));

                // The holder's change is seen by the other; the other's own change is refused for want of a lock.
                await Ok<EditSessionView>(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/changes", new ChangeRequest("layout changed"), "Ada announces a change");
                var refused = await Send(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/changes", new ChangeRequest("mine too"), "Bo announces a change");
                Assert.Equal(HttpStatusCode.Conflict, refused.Status);
                Assert.Equal("NoLiveLock", JsonSerializer.Deserialize<CollaborationRefusalView>(refused.Body, Json)!.Code);
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/presence", new PresenceRequest("widget:1", null), "Bo moves")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/heartbeat", null, "Bo's heartbeat")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plain, plainCookies, plainId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/heartbeat", null, "a plain user's heartbeat")).Status);
                var seen = await Ok<EditSessionView>(app, bo, boCookies, boId, HttpMethod.Get, $"{Api}/sessions/{adaSession.Id}", null, "Bo reads the session");
                Assert.Equal(("layout changed", adaId), (Assert.Single(seen.Changes).Descriptor, seen.Changes[0].AuthorUserId));
                Assert.Equal("widget:1", seen.Participants.Single(participant => participant.UserId == boId).ActiveRegion);

                // The administrator's overview, then the take-over: Bo (an administrator) releases Ada's lock and then holds it.
                var overview = await Ok<CollaborationOverviewView>(app, bo, boCookies, boId, HttpMethod.Get, $"{Api}/overview", null, "the overview");
                Assert.Equal(adaId, Assert.Single(overview.Locks).HolderUserId);
                Assert.Equal(2, Assert.Single(overview.Sessions).Participants.Count);

                // The overview page is served to an administrator (its module renders interactively, so its content is the API's).
                var page = await GateDrive.GetAsync(bo, boCookies, Host, "/admin/collaboration", output, "the collaboration page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Collaboration", page.Body);
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/locks/take-over", lockRequest, "Bo takes the lock over")).Status);
                Assert.True((await Ok<ResourceLockResult>(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/locks/acquire", lockRequest, "Bo takes the lock")).Acquired);
                var heartbeat = await Ok<ResourceLockResult>(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/locks/renew", lockRequest, "Ada's heartbeat after the take-over");
                Assert.Equal((false, boId), (heartbeat.Acquired, heartbeat.HolderUserId));

                // A board that does not exist, and a malformed reference, are refused.
                var missing = await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/locks/acquire", new LockRequest(CollaborativeResourceTypes.Dashboard, Guid.NewGuid().ToString()), "a board that does not exist");
                Assert.Equal(HttpStatusCode.NotFound, missing.Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions", new JoinSessionRequest("Bad Type", "x"), "a malformed reference")).Status);

                // A user without the keys is refused every surface.
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plain, plainCookies, plainId, HttpMethod.Post, $"{Api}/locks/acquire", lockRequest, "a plain user takes a lock")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plain, plainCookies, Host, $"{Api}/overview", output, "a plain user's overview")).Status);

                // The last one out closes the session and gives up their locks.
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/leave", null, "Ada leaves")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions/{adaSession.Id}/leave", null, "Bo leaves")).Status);
                var after = await Ok<CollaborationOverviewView>(app, ada, adaCookies, adaId, HttpMethod.Get, $"{Api}/overview", null, "the overview after");
                Assert.Empty(after.Locks);
                Assert.Empty(after.Sessions);

                // An administrator ends a session outright.
                var reopened = await Ok<EditSessionView>(app, bo, boCookies, boId, HttpMethod.Post, $"{Api}/sessions", new JoinSessionRequest(CollaborativeResourceTypes.Dashboard, board.Id.ToString()), "Bo opens again");
                Assert.NotEqual(adaSession.Id, reopened.Id);
                Assert.Equal(HttpStatusCode.NoContent, (await Send(app, ada, adaCookies, adaId, HttpMethod.Post, $"{Api}/sessions/{reopened.Id}/close", null, "Ada ends it")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(bo, boCookies, Host, $"{Api}/sessions/{reopened.Id}", output, "the ended session")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private async Task<T> Ok<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object? body, string label)
        {
            var reply = method == HttpMethod.Get
                ? await GateDrive.GetAsync(client, cookies, Host, path, output, label) is var got ? (got.Status, got.Body) : default
                : await Send(app, client, cookies, userId, method, path, body, label);
            Assert.True(reply.Status == HttpStatusCode.OK, $"{label}: {reply.Status} {reply.Body}");
            return JsonSerializer.Deserialize<T>(reply.Body, Json)!;
        }
    }
}
