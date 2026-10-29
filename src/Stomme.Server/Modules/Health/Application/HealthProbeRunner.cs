#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// Runs a set of probes and returns what each said, never throwing and never hanging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A probe is somebody else's code, so the runner trusts it with nothing.</b> Each probe is caught
    /// individually — a contributed probe that throws becomes an Unhealthy result for that probe and costs no
    /// other probe its turn.
    /// </para>
    /// <para>
    /// <b>And each probe is bounded from the OUTSIDE, not only by its own token.</b> A token is a request, and the
    /// calls these probes make are exactly the ones that ignore it: a TCP connect to an SMTP host that is
    /// black-holing packets does not return because somebody cancelled a token. Without an outer bound, one such
    /// probe stops health monitoring for the whole installation — for as long as the connect takes to give up,
    /// which can be minutes. <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/> is the bound.
    /// </para>
    /// <para>
    /// <b>The honest cost of that bound:</b> abandoning the await does not kill the probe. The task keeps running
    /// until whatever it is blocked on returns, and its result is discarded. That is a leak of one pending
    /// operation per hung probe per tick, which is the price of not letting one dependency freeze the monitor. It
    /// is bounded in practice because the underlying connect has its own OS-level timeout — and a probe that hangs
    /// every tick reports Unhealthy every tick, so it gets noticed rather than hidden.
    /// </para>
    /// </remarks>
    public sealed class HealthProbeRunner
    {
        private readonly TimeProvider _clock;

        public HealthProbeRunner(TimeProvider? timeProvider = null) => _clock = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Runs each probe in <paramref name="probes"/> for <paramref name="context"/>, bounding each at
        /// <paramref name="perProbeTimeout"/>.
        /// </summary>
        public async Task<IReadOnlyList<HealthProbeResult>> RunAsync(
            IReadOnlyList<IHealthProbe> probes,
            HealthProbeContext context,
            TimeSpan perProbeTimeout,
            CancellationToken cancellationToken = default)
        {
            var results = new List<HealthProbeResult>(probes.Count);

            foreach (var probe in probes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                results.Add(await RunOneAsync(probe, context, perProbeTimeout, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }

        private async Task<HealthProbeResult> RunOneAsync(
            IHealthProbe probe,
            HealthProbeContext context,
            TimeSpan perProbeTimeout,
            CancellationToken cancellationToken)
        {
            var start = _clock.GetTimestamp();

            try
            {
                var check = probe.CheckAsync(context, cancellationToken);
                return perProbeTimeout > TimeSpan.Zero
                    ? await check.WaitAsync(perProbeTimeout, cancellationToken).ConfigureAwait(false)
                    : await check.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The probe outran its bound - it is still running somewhere, and we are no longer waiting.
                return Failed(probe, start, "did not answer within its budget and was abandoned", nameof(TimeoutException));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The tick is being abandoned, not the dependency judged.
                return Failed(probe, start, "was cancelled because the run is stopping", nameof(OperationCanceledException));
            }
            catch (Exception ex)
            {
                // The contract says a probe must not throw. This is what happens when one does anyway.
                return Failed(probe, start, $"threw instead of reporting: {ex.Message}", ex.GetType().Name);
            }
        }

        private HealthProbeResult Failed(IHealthProbe probe, long start, string what, string exceptionType)
        {
            var elapsed = _clock.GetElapsedTime(start);
            return new HealthProbeResult(
                probe.Name,
                probe.Dependency,
                HealthState.Unhealthy,
                $"{probe.Dependency} {what}.",
                elapsed,
                new Dictionary<string, string>
                {
                    ["latencyMs"] = ((long)elapsed.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["exceptionType"] = exceptionType
                });
        }

        /// <summary>
        /// The worst state in a set - a fold that relies on
        /// <see cref="HealthState"/>'s numeric order.
        /// </summary>
        /// <remarks>
        /// <b>An empty set is reported as <paramref name="whenEmpty"/>, not silently as Healthy.</b> An aggregator that
        /// folded nothing to Healthy would mean an installation whose probes all failed to be
        /// REGISTERED reports perfect health — the one answer a monitor must never give by accident. Callers that
        /// genuinely have nothing to check (readiness before anything is installed) pass Healthy deliberately.
        /// </remarks>
        public static HealthState Worst(IReadOnlyCollection<HealthProbeResult> results, HealthState whenEmpty)
        {
            if (results.Count == 0)
            {
                return whenEmpty;
            }

            var worst = HealthState.Healthy;
            foreach (var result in results)
            {
                if (result.State > worst)
                {
                    worst = result.State;
                }
            }

            return worst;
        }
    }
}
