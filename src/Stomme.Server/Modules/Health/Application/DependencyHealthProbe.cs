#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The generic probe engine: time a round-trip, and map how it went to a state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// the predecessor platform's <c>DependencyHealthProbe</c>, lifted: a throw is <see cref="HealthState.Unhealthy"/>, a success
    /// slower than the degraded threshold is <see cref="HealthState.Degraded"/>, anything else is Healthy, and a
    /// round-trip that outruns the timeout is cancelled and reported Unhealthy. The ping delegate is supplied by
    /// whoever composes the probe, which is what keeps this type free of any persistence or transport reference.
    /// </para>
    /// <para>
    /// <b>The ping must actually TALK to the dependency.</b> A delegate that merely resolves a client from the
    /// container will never throw and never be slow, so the probe reports Healthy forever — a green light wired to
    /// nothing. That is not hypothetical: two of the predecessor platform's five probes did exactly that, and a scanner in this
    /// module's gates now refuses them.
    /// </para>
    /// <para>
    /// <b>Caller cancellation is re-thrown, a timeout is not.</b> They mean different things: the host is stopping
    /// (which is not a health verdict) versus the dependency did not answer in time (which is).
    /// </para>
    /// </remarks>
    public sealed class DependencyHealthProbe : IHealthProbe
    {
        private readonly Func<HealthProbeContext, CancellationToken, Task> _ping;
        private readonly TimeSpan _degradedThreshold;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _clock;

        public DependencyHealthProbe(
            string name,
            string dependency,
            HealthProbeScope scope,
            Func<HealthProbeContext, CancellationToken, Task> ping,
            TimeSpan degradedThreshold,
            TimeSpan timeout,
            TimeProvider? timeProvider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency);
            ArgumentNullException.ThrowIfNull(ping);

            Name = name;
            Dependency = dependency;
            Scope = scope;
            _ping = ping;
            _degradedThreshold = degradedThreshold;
            _timeout = timeout;
            _clock = timeProvider ?? TimeProvider.System;
        }

        public string Name { get; }

        public string Dependency { get; }

        public HealthProbeScope Scope { get; }

        public async Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
        {
            var start = _clock.GetTimestamp();
            using var timeoutSource = _timeout > TimeSpan.Zero
                ? new CancellationTokenSource(_timeout, _clock)
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            try
            {
                await _ping(context, linked.Token).ConfigureAwait(false);
                var elapsed = _clock.GetElapsedTime(start);
                var degraded = elapsed > _degradedThreshold;
                return new HealthProbeResult(
                    Name,
                    Dependency,
                    degraded ? HealthState.Degraded : HealthState.Healthy,
                    degraded ? $"{Dependency} responded slowly ({Ms(elapsed)} ms)." : $"{Dependency} is healthy.",
                    elapsed,
                    Latency(elapsed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER is stopping. That is not a verdict about the dependency, so it is not turned into one.
                throw;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                var elapsed = _clock.GetElapsedTime(start);
                return new HealthProbeResult(Name, Dependency, HealthState.Unhealthy,
                    $"{Dependency} timed out after {Ms(elapsed)} ms.", elapsed, Latency(elapsed));
            }
            catch (Exception ex)
            {
                var elapsed = _clock.GetElapsedTime(start);

                // The raw message goes in the description, which is PRIVILEGED - it carries host, port, database
                // and user. It reaches the gated admin API and never a notification or an anonymous endpoint.
                return new HealthProbeResult(Name, Dependency, HealthState.Unhealthy,
                    $"{Dependency} is unreachable: {ex.Message}", elapsed, Latency(elapsed, ex));
            }
        }

        private static string Ms(TimeSpan elapsed) =>
            ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        private static IReadOnlyDictionary<string, string> Latency(TimeSpan elapsed) =>
            new Dictionary<string, string> { ["latencyMs"] = Ms(elapsed) };

        /// <summary>
        /// The failure detail. The exception TYPE is carried separately from the message, because that is the part
        /// a notification is allowed to repeat.
        /// </summary>
        private static IReadOnlyDictionary<string, string> Latency(TimeSpan elapsed, Exception ex) =>
            new Dictionary<string, string>
            {
                ["latencyMs"] = Ms(elapsed),
                ["exceptionType"] = ex.GetType().Name
            };
    }
}
