#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Application;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D104 binding item 9: one hung or throwing probe must not be able to stop health monitoring.
    /// </summary>
    /// <remarks>
    /// The failure this guards against was measured on the predecessor platform, not imagined: its job body took no cancellation
    /// token, its <c>MaximumDuration</c> auto-heal could only run on a LATER pass of the loop (which cannot start
    /// while the body is stuck), and one catch wrapped every tenant. A single SMTP connect to a host that
    /// black-holes packets therefore stopped health for the whole installation, and one tenant's exception
    /// skipped every later tenant.
    /// </remarks>
    public class HealthProbeRunnerTests
    {
        private readonly ITestOutputHelper _output;

        public HealthProbeRunnerTests(ITestOutputHelper output) => _output = output;

        private static readonly HealthProbeContext Context = new(SiteId: 1);

        /// <summary>A probe that ignores its token and blocks — the connect that does not care about cancellation.</summary>
        private sealed class HangingProbe : IHealthProbe
        {
            private readonly TaskCompletionSource _never = new();

            public string Name => "hangs";
            public string Dependency => "Black Hole";
            public HealthProbeScope Scope => HealthProbeScope.Installation;

            public async Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                // Deliberately NOT observing the token: that is the whole point.
                await _never.Task.ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            }

            public void Release() => _never.TrySetResult();
        }

        private sealed class ThrowingProbe : IHealthProbe
        {
            public string Name => "throws";
            public string Dependency => "Rude Dependency";
            public HealthProbeScope Scope => HealthProbeScope.Installation;

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("the contributed probe blew up with a secret: server=db01;password=hunter2");
        }

        private sealed class HealthyProbe : IHealthProbe
        {
            public HealthyProbe(string name) => Name = name;

            public string Name { get; }
            public string Dependency => "Fine";
            public HealthProbeScope Scope => HealthProbeScope.Installation;
            public bool Ran { get; private set; }

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                Ran = true;
                return Task.FromResult(new HealthProbeResult(Name, Dependency, HealthState.Healthy, "fine", TimeSpan.FromMilliseconds(5)));
            }
        }

        [Fact]
        public async Task A_probe_that_ignores_its_token_is_abandoned_and_the_next_probe_still_runs()
        {
            var hanging = new HangingProbe();
            var after = new HealthyProbe("after");
            var runner = new HealthProbeRunner();

            try
            {
                var started = DateTime.UtcNow;
                var results = await runner.RunAsync(new IHealthProbe[] { hanging, after }, Context, TimeSpan.FromMilliseconds(200));
                var elapsed = DateTime.UtcNow - started;

                _output.WriteLine($"[elapsed] {elapsed.TotalMilliseconds:0} ms for {results.Count} result(s)");
                foreach (var result in results)
                {
                    _output.WriteLine($"[result] {result.Name} = {result.State} :: {result.Description}");
                }

                // The hung probe was given up on ...
                Assert.Equal(2, results.Count);
                Assert.Equal(HealthState.Unhealthy, results[0].State);
                Assert.Contains("abandoned", results[0].Description);

                // ... and the probe AFTER it still ran, which is the property that matters.
                Assert.True(after.Ran, "the probe after the hung one never ran - one bad dependency stopped the tick");
                Assert.Equal(HealthState.Healthy, results[1].State);

                // And it did not wait anywhere near for ever.
                Assert.True(elapsed < TimeSpan.FromSeconds(5), $"the runner waited {elapsed.TotalSeconds:0.0}s despite a 200 ms bound");
            }
            finally
            {
                hanging.Release();
            }
        }

        [Fact]
        public async Task A_probe_that_throws_becomes_an_unhealthy_result_and_costs_no_other_probe()
        {
            var after = new HealthyProbe("after");
            var runner = new HealthProbeRunner();

            var results = await runner.RunAsync(new IHealthProbe[] { new ThrowingProbe(), after }, Context, TimeSpan.FromSeconds(5));

            foreach (var result in results)
            {
                _output.WriteLine($"[result] {result.Name} = {result.State} :: {result.Description}");
            }

            Assert.Equal(HealthState.Unhealthy, results[0].State);
            Assert.True(after.Ran);
            Assert.Equal(HealthState.Healthy, results[1].State);

            // The exception TYPE is captured as data - that is the part a notification may repeat. The message
            // stays in the privileged description, which only the gated API shows.
            Assert.Equal(nameof(InvalidOperationException), results[0].Data!["exceptionType"]);
        }

        [Fact]
        public void The_worst_state_wins_and_an_empty_set_is_not_silently_healthy()
        {
            var results = new[]
            {
                new HealthProbeResult("a", "A", HealthState.Healthy, null, TimeSpan.Zero),
                new HealthProbeResult("b", "B", HealthState.Unhealthy, null, TimeSpan.Zero),
                new HealthProbeResult("c", "C", HealthState.Degraded, null, TimeSpan.Zero)
            };

            Assert.Equal(HealthState.Unhealthy, HealthProbeRunner.Worst(results, HealthState.Healthy));
            Assert.Equal(HealthState.Degraded, HealthProbeRunner.Worst(results.Where(r => r.State != HealthState.Unhealthy).ToList(), HealthState.Healthy));

            // the predecessor platform's aggregator folded an EMPTY set to Healthy, so an installation whose probes all failed to
            // REGISTER reported perfect health. Here the caller must say what empty means.
            Assert.Equal(HealthState.Unhealthy, HealthProbeRunner.Worst(Array.Empty<HealthProbeResult>(), HealthState.Unhealthy));
            Assert.Equal(HealthState.Healthy, HealthProbeRunner.Worst(Array.Empty<HealthProbeResult>(), HealthState.Healthy));
        }

        [Fact]
        public async Task A_probe_that_is_slower_than_the_degraded_threshold_is_degraded_not_unhealthy()
        {
            // The engine lifted from the predecessor platform: slow is a different answer from broken, and collapsing them is how
            // a monitor cries wolf.
            var slow = new DependencyHealthProbe(
                "slow", "Slow Thing", HealthProbeScope.Installation,
                async (_, ct) => await Task.Delay(120, ct).ConfigureAwait(false),
                degradedThreshold: TimeSpan.FromMilliseconds(20),
                timeout: TimeSpan.FromSeconds(5));

            var result = await slow.CheckAsync(Context);
            _output.WriteLine($"[slow] {result.State} :: {result.Description}");

            Assert.Equal(HealthState.Degraded, result.State);
            Assert.Contains("responded slowly", result.Description);
        }

        [Fact]
        public async Task A_probe_whose_dependency_is_unreachable_is_unhealthy_and_keeps_the_reason()
        {
            var broken = new DependencyHealthProbe(
                "broken", "Missing Thing", HealthProbeScope.Tenant,
                (_, _) => throw new TimeoutException("no route to host db01:5432"),
                degradedThreshold: TimeSpan.FromMilliseconds(500),
                timeout: TimeSpan.FromSeconds(5));

            var result = await broken.CheckAsync(Context);
            _output.WriteLine($"[broken] {result.State} :: {result.Description}");

            Assert.Equal(HealthState.Unhealthy, result.State);
            Assert.Contains("is unreachable", result.Description);
            Assert.Equal(nameof(TimeoutException), result.Data!["exceptionType"]);
        }

        [Fact]
        public async Task Caller_cancellation_is_not_turned_into_a_health_verdict()
        {
            // "The host is stopping" and "the dependency is down" are different facts. Reporting the first as the
            // second would fill the alert ledger every time the app restarts.
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var probe = new DependencyHealthProbe(
                "any", "Any", HealthProbeScope.Installation,
                async (_, ct) => await Task.Delay(1000, ct).ConfigureAwait(false),
                degradedThreshold: TimeSpan.FromMilliseconds(10),
                timeout: TimeSpan.FromSeconds(30));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(Context, cancelled.Token));
        }
    }
}
