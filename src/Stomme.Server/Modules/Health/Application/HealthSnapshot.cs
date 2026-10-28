#nullable enable
using System;
using System.Threading;
using Stomme.Health;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// The last tick's verdict, held in memory, so readiness can be answered without probing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists so that an anonymous endpoint executes NO probe.</b> The obvious implementation of
    /// <c>/readyz</c> — map the health checks and let them run — means an unauthenticated caller can drive a
    /// database round-trip, a cache write (to L2 Redis, if configured) and a TCP connect to the site's configured
    /// SMTP host, once per request, simply by refreshing. That is an amplification vector originating from the
    /// site's own IP, and the response time is a timing oracle for how the dependencies are doing. Readiness
    /// therefore reports what the last JOB tick found, and nothing else.
    /// </para>
    /// <para>
    /// <b>Singleton, and deliberately not persisted.</b> Readiness is a question about THIS process — "should
    /// traffic come here" — so a value read from a shared store would answer for the wrong host. A process that
    /// has not ticked yet reports not-ready, which is the correct answer for a host that has not checked anything.
    /// </para>
    /// </remarks>
    public sealed class HealthSnapshot
    {
        private long _atTicks;
        private int _state = (int)HealthState.Unhealthy;

        /// <summary>Records the verdict of a completed tick.</summary>
        public void Record(HealthState state, DateTime atUtc)
        {
            Interlocked.Exchange(ref _state, (int)state);
            Interlocked.Exchange(ref _atTicks, atUtc.Ticks);
        }

        /// <summary>The last verdict, and when it was reached. <c>null</c> when nothing has ticked yet.</summary>
        public (HealthState State, DateTime AtUtc)? Last
        {
            get
            {
                var ticks = Interlocked.Read(ref _atTicks);
                return ticks == 0
                    ? null
                    : ((HealthState)Volatile.Read(ref _state), new DateTime(ticks, DateTimeKind.Utc));
            }
        }

        /// <summary>
        /// Whether this host should take traffic: it has ticked, and the tick did not find anything Unhealthy.
        /// </summary>
        /// <remarks>
        /// Degraded is READY. A slow dependency is a reason to look, not a reason to take the host out of a load
        /// balancer — doing that turns "the database is slow" into "there is no capacity left", which is how a
        /// slow day becomes an outage.
        /// </remarks>
        public bool IsReady => Last is { State: not HealthState.Unhealthy };
    }
}
