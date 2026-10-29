#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What the two anonymous health endpoints may and may not do (D104 binding items 4, 5 and 15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the security half of the row, and each case is a specific failure the design review caught before
    /// anything was written: an orchestrator restart-looping a fresh deployment because liveness asked a question
    /// that needs a tenant; an unauthenticated caller driving database round-trips, cache writes and SMTP connects
    /// by refreshing a page; and a 503 silently re-executed into the site's 404 page, so the orchestrator reads
    /// "fine" where the host said "not ready".
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthEndpointExposureTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HealthEndpointExposureTests(ITestOutputHelper output) => _output = output;

        /// <summary>A probe that counts how often anything ran it. It must never be run by a request.</summary>
        private sealed class CountingProbe : IHealthProbe
        {
            private int _runs;

            public string Name => "counted";
            public string Dependency => "Counted Dependency";
            public HealthProbeScope Scope => HealthProbeScope.Installation;
            public int Runs => Volatile.Read(ref _runs);

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _runs);
                return Task.FromResult(new HealthProbeResult(Name, Dependency, HealthState.Healthy, "fine", TimeSpan.Zero));
            }
        }

        private sealed class CountingContributor(CountingProbe probe) : IHealthContributor
        {
            public string ContributorId => "tests.counting";
            public IReadOnlyList<IHealthProbe> Probes { get; } = new IHealthProbe[] { probe };
            public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = Array.Empty<HealthMetricDescriptor>();
            public IHealthMetricSource? SourceFor(string metricName) => null;
        }

        /// <summary>
        /// The probe job OFF, through the override file the host reads. Probing ships ON (<c>Health:Enabled</c> in the
        /// shipped appsettings.json since BL-83 closed), so on a host left at its defaults the job's first tick races the
        /// first request: it ran the counting probe once and made readiness 200 before the test asked
        /// (BL-186's gate). These cases are about what a REQUEST may do, and what the host says before any tick,
        /// so no tick may happen - the job itself is driven by <c>HealthProbeJobGateTests</c> and <c>OverrideFileSwitchTests</c>.
        /// </summary>
        private static Task<ScratchGateInstall> CloneWithProbingOffAsync(ITestOutputHelper output) =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = false } });

        [Fact]
        public async Task Liveness_answers_on_an_installed_host_with_one_word_and_no_probe_runs()
        {
            var probe = new CountingProbe();

            using var scratch = await CloneWithProbingOffAsync(_output);
            var app = await GateDrive.InstallAsync(scratch, _output, builder =>
                builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthContributor>(new CountingContributor(probe))),
                start: false);

            try
            {
                await app.StartAsync();
                using var client = app.GetTestClient();

                var live = await client.GetAsync(HealthEndpoints.LivenessPath);
                var body = await live.Content.ReadAsStringAsync();
                _output.WriteLine($"[healthz] {(int)live.StatusCode} '{body}' ({body.Length} chars)");

                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal("alive", body.Trim());

                // One word, not a page: no probe name, no dependency, no host, no connection string.
                Assert.DoesNotContain("<", body);
                Assert.True(body.Length < 32, $"liveness answered {body.Length} characters; it should answer one word");

                // Hammer it. Not ONE probe execution may be attributable to a request.
                for (var i = 0; i < 25; i++)
                {
                    await client.GetAsync(HealthEndpoints.LivenessPath);
                    await client.GetAsync(HealthEndpoints.ReadinessPath);
                }

                _output.WriteLine($"[probe runs after 50 anonymous requests] {probe.Runs}");
                Assert.Equal(0, probe.Runs);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Readiness_is_503_with_one_word_before_the_first_tick_and_is_not_re_executed_into_the_404_page()
        {
            using var scratch = await CloneWithProbingOffAsync(_output);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await app.StartAsync();
                using var client = app.GetTestClient();

                var ready = await client.GetAsync(HealthEndpoints.ReadinessPath);
                var body = await ready.Content.ReadAsStringAsync();
                _output.WriteLine($"[readyz before any tick] {(int)ready.StatusCode} '{body.Trim()}' ({body.Length} chars)");

                // 503, NOT 404, and one word rather than a page. An orchestrator that read 404 and a page of HTML
                // here would conclude the host is fine.
                //
                // WHAT THIS DOES NOT PROVE, measured rather than assumed: it is not the status-code skip-list
                // entry that makes this pass. Mutating that entry away leaves this green, because
                // UseStatusCodePagesWithReExecute does not re-execute a response that has already written a body
                // and this endpoint writes one. D104 binding item 15 assumed the re-execute applied here; it does
                // not. The entry is kept as belt-and-braces for a future body-less response, and this test is
                // honest about covering the behaviour rather than that entry.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                Assert.Equal("not-ready", body.Trim());
                Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("<!DOCTYPE", body, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Readiness_follows_the_last_tick_and_treats_degraded_as_ready()
        {
            // The verdicts are recorded by hand below; a real tick in between would overwrite one.
            using var scratch = await CloneWithProbingOffAsync(_output);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await app.StartAsync();
                using var client = app.GetTestClient();
                var snapshot = app.Services.GetRequiredService<HealthSnapshot>();

                snapshot.Record(HealthState.Healthy, DateTime.UtcNow);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthEndpoints.ReadinessPath)).StatusCode);

                // Degraded is READY. Pulling a host out of rotation because a dependency is SLOW turns "the
                // database is slow" into "there is no capacity left", which is how a slow day becomes an outage.
                snapshot.Record(HealthState.Degraded, DateTime.UtcNow);
                var degraded = await client.GetAsync(HealthEndpoints.ReadinessPath);
                _output.WriteLine($"[readyz when degraded] {(int)degraded.StatusCode}");
                Assert.Equal(HttpStatusCode.OK, degraded.StatusCode);

                snapshot.Record(HealthState.Unhealthy, DateTime.UtcNow);
                var unhealthy = await client.GetAsync(HealthEndpoints.ReadinessPath);
                var body = await unhealthy.Content.ReadAsStringAsync();
                _output.WriteLine($"[readyz when unhealthy] {(int)unhealthy.StatusCode} '{body.Trim()}'");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);

                // Still says nothing about WHY: the reason is privileged and lives behind the gated API.
                Assert.Equal("not-ready", body.Trim());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_health_api_refuses_an_anonymous_caller()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await app.StartAsync();
                using var client = app.GetTestClient();

                var response = await client.GetAsync("/api/Health");
                _output.WriteLine($"[api/Health anonymous] {(int)response.StatusCode}");

                // The endpoints above are the ONLY anonymous health surface. Everything with detail on it is
                // gated, and an anonymous caller gets nothing - not a 200 with an empty dashboard.
                Assert.True(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                    $"api/Health answered {(int)response.StatusCode} to an anonymous caller; it must refuse.");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
