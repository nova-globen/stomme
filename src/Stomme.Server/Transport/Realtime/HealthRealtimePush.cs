using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Stomme.Transport.Realtime
{
    /// <summary>
    /// What a health check pushes (BL-83 / D104): the site's worst state as ONE word, and when it was decided - nothing
    /// else. No subject, no latency, no description: a connection that is open is not a request that was authorized for
    /// detail, so the push is a NUDGE and the page re-reads <c>api/Health</c>, which decides <c>health:view</c> again.
    /// </summary>
    public sealed record HealthRealtimeMessage(string State, DateTime CheckedUtc);

    /// <summary>
    /// The transport-agnostic port the health job and "run probes now" push through, without referencing SignalR. The host
    /// registers <see cref="HealthRealtimePush"/> when the SignalR transport is enabled; when it is not, the port is absent
    /// and the producers simply do not push (the page still reads on load and on Refresh).
    /// </summary>
    public interface IHealthRealtimePush
    {
        /// <summary>Pushes to the connections of ONE site - <c>(tenantId, siteId)</c> is the framework's
        /// <see cref="Stomme.Models.Alias.SiteKey"/> - and to nobody else.</summary>
        Task PushAsync(int tenantId, int siteId, HealthRealtimeMessage message, CancellationToken cancellationToken = default);
    }

    /// <summary>Host adapter for <see cref="IHealthRealtimePush"/>, over <see cref="HealthHub"/>'s per-site groups.</summary>
    public sealed class HealthRealtimePush : IHealthRealtimePush
    {
        private readonly IHubContext<HealthHub> _hub;

        public HealthRealtimePush(IHubContext<HealthHub> hub)
        {
            _hub = hub;
        }

        public Task PushAsync(int tenantId, int siteId, HealthRealtimeMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            return _hub.Clients.Group(HealthHub.GroupFor(tenantId, siteId))
                .SendAsync(HealthHub.StateMethod, message, cancellationToken);
        }
    }
}
