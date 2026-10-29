using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Stomme.Transport.Realtime
{
    // HubRoutes moved to Stomme.Shared (src/Stomme.Shared/Transport/HubRoutes.cs, same namespace) so the Client's
    // live notification feed can name the route; AssemblyInfo.cs forwards the type.

    /// <summary>
    /// In-app realtime notifications (brief 04 / D10). The server pushes a rendered notification to the
    /// <see cref="NotificationMethod"/> client method, targeted to the recipient user — SignalR's per-user
    /// grouping (<c>Clients.User</c>) resolves every connection authenticated as that user id. Authenticated;
    /// carries no client-callable methods (server-push only). Adopted from the predecessor platform's <c>NotificationHub</c>.
    /// </summary>
    [Authorize]
    public sealed class NotificationHub : Hub
    {
        /// <summary>The client method invoked with each pushed in-app notification.</summary>
        public const string NotificationMethod = "notification";
    }

    /// <summary>
    /// Realtime health (brief 04 / D10): the health page's live update, and the live connection count
    /// <see cref="RealtimeConnectionCounter"/> reports. Adopted from the predecessor platform's <c>HealthHub</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One group per SITE, and a connection joins only its own.</b> The group is named by the site key
    /// (<c>{tenantId}:{siteId}</c>, <see cref="Stomme.Models.Alias.SiteKey"/>) of the principal that opened the
    /// connection - the claim <c>PrincipalValidator</c> already holds equal to the request's alias, so a cookie issued
    /// for another site is rejected before it gets here. The CLAIM rather than the request's alias because the alias
    /// lives in <c>HttpContext.Items</c>, which SignalR does not carry into the context of a long-polling connection.
    /// A principal with no site key joins no group and receives nothing.
    /// </para>
    /// <para>
    /// The connection itself is gated by <see cref="HubPolicies.Health"/> (<c>health:view</c>), and the push carries only
    /// a state word and an instant; the page re-reads the detail through <c>api/Health</c>, which decides again.
    /// </para>
    /// </remarks>
    [Authorize]
    public sealed class HealthHub : Hub
    {
        /// <summary>The client method a health push invokes (<see cref="HealthRealtimeMessage"/>).</summary>
        public const string StateMethod = "health";

        private readonly RealtimeConnectionCounter _counter;

        public HealthHub(RealtimeConnectionCounter counter)
        {
            _counter = counter;
        }

        /// <summary>The group of one site's connections.</summary>
        public static string GroupFor(int tenantId, int siteId) => GroupFor(tenantId + ":" + siteId);

        private static string GroupFor(string siteKey) => "health:" + siteKey;

        public override async Task OnConnectedAsync()
        {
            _counter.Opened();

            var siteKey = Context.User?.SiteKey();
            if (!string.IsNullOrEmpty(siteKey))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(siteKey));
            }

            await base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            _counter.Closed();
            return base.OnDisconnectedAsync(exception);
        }
    }

    /// <summary>Process-wide live-connection gauge for the realtime transport (incremented/decremented by
    /// <see cref="HealthHub"/>). A singleton so every hub connection shares one counter.</summary>
    public sealed class RealtimeConnectionCounter
    {
        private int _active;

        public int Active => Volatile.Read(ref _active);

        public void Opened() => Interlocked.Increment(ref _active);

        public void Closed() => Interlocked.Decrement(ref _active);
    }
}
