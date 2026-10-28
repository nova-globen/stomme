#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Domain;

namespace Stomme.Modules.Health.Application
{
    /// <summary>Everything an alert needs to say, without anything it must not say.</summary>
    /// <remarks>
    /// <b>The probe RESULT travels, the message does not.</b> A sink may use the exception TYPE and the latency;
    /// it must not put <see cref="HealthProbeResult.Description"/> into a notification, because that carries the
    /// raw exception text — host, port, database and user — to every recipient by email. The description reaches
    /// people only through the permission-gated API, where the row keeps it.
    /// </remarks>
    public sealed record HealthAlertNotice(
        HealthProbeScope Scope,
        int SiteId,
        HealthSubject Subject,
        HealthState State,
        HealthState PreviousState,
        HealthProbeResult Result,
        HealthAlertStateAggregate Row);

    /// <summary>
    /// Where a raised or cleared alert goes.
    /// </summary>
    /// <remarks>
    /// A seam rather than a direct call to the notification dispatcher, for two reasons that both turned out to
    /// matter. It lets the transition machine be driven end to end without the notification stack — the predecessor platform's
    /// policy could not be tested without it, which is why its recovery branch was never exercised. And it keeps
    /// alerting OPTIONAL: an install with alerting off gets the no-op sink, and the probe job still records
    /// everything it observes, so the history is there when someone turns alerting on.
    /// </remarks>
    public interface IHealthAlertSink
    {
        Task RaisedAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default);

        Task RecoveredAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default);
    }

    /// <summary>The sink an install with <c>health.alerting</c> off uses: it records nothing and tells nobody.</summary>
    public sealed class NoOpHealthAlertSink : IHealthAlertSink
    {
        public Task RaisedAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecoveredAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
