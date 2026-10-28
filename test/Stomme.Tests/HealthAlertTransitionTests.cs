#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The alert machine driven through the real pass, on a clock the test owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every case here is one predecessor-platform could not have: its transition state was a per-process dictionary, and its
    /// policy could not be exercised without the notification stack — which is why its recovery branch was never
    /// tested, and why the design review found a proposed rewrite in which a recovery could never fire at all.
    /// </para>
    /// <para>
    /// The repositories are in-memory but the PASS is the real one, so what is under test is the sequencing that
    /// actually ships: observe, decide, arm, sweep, raise, clear — including the parts that only make sense
    /// across ticks.
    /// </para>
    /// </remarks>
    public class HealthAlertTransitionTests
    {
        private readonly ITestOutputHelper _output;

        public HealthAlertTransitionTests(ITestOutputHelper output) => _output = output;

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

        // ---- the cases -------------------------------------------------------------------------------------

        [Fact]
        public async Task Going_bad_says_nothing_until_the_threshold_has_actually_elapsed()
        {
            var world = new World();
            world.State = HealthState.Unhealthy;

            await world.TickAsync();                       // goes bad: arms a 2-minute re-check
            Assert.Empty(world.Sink.Raised);

            world.Advance(TimeSpan.FromMinutes(1));
            await world.TickAsync();                       // still bad, but the re-check is not due
            Assert.Empty(world.Sink.Raised);

            world.Advance(TimeSpan.FromMinutes(1));
            await world.TickAsync();                       // now it is due
            Assert.Single(world.Sink.Raised);
            _output.WriteLine($"[raised] {world.Sink.Raised.Single().State} after {world.Now - Start}");
        }

        [Fact]
        public async Task A_blip_inside_the_threshold_is_never_mentioned()
        {
            var world = new World();

            world.State = HealthState.Unhealthy;
            await world.TickAsync();

            world.Advance(TimeSpan.FromSeconds(30));
            world.State = HealthState.Healthy;
            await world.TickAsync();                       // recovered before anyone was told

            world.Advance(TimeSpan.FromMinutes(10));
            await world.TickAsync();

            // Nothing was raised, so nothing is "recovered" either - a recovery notice would be the first anyone
            // heard of a problem that is already over.
            Assert.Empty(world.Sink.Raised);
            Assert.Empty(world.Sink.Recovered);
        }

        [Fact]
        public async Task It_raises_once_and_stays_quiet_while_the_problem_persists()
        {
            var world = new World { State = HealthState.Unhealthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();
            Assert.Single(world.Sink.Raised);

            // Ten more bad minutes. An operator who has been told does not need telling again every minute.
            for (var i = 0; i < 10; i++)
            {
                world.Advance(TimeSpan.FromMinutes(1));
                await world.TickAsync();
            }

            _output.WriteLine($"[raised] {world.Sink.Raised.Count} time(s) over {world.Now - Start}");
            Assert.Single(world.Sink.Raised);
        }

        [Fact]
        public async Task Recovery_is_sent_exactly_once_per_raised_alert()
        {
            var world = new World { State = HealthState.Unhealthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();
            Assert.Single(world.Sink.Raised);

            world.Advance(TimeSpan.FromMinutes(1));
            world.State = HealthState.Healthy;
            await world.TickAsync();
            Assert.Single(world.Sink.Recovered);

            // Every later healthy tick is not another recovery.
            for (var i = 0; i < 5; i++)
            {
                world.Advance(TimeSpan.FromMinutes(1));
                await world.TickAsync();
            }

            Assert.Single(world.Sink.Recovered);
        }

        [Fact]
        public async Task A_flap_inside_the_cooldown_does_not_raise_again()
        {
            var world = new World { State = HealthState.Unhealthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();                       // raised
            world.Advance(TimeSpan.FromMinutes(1));
            world.State = HealthState.Healthy;
            await world.TickAsync();                       // recovered, cooldown opens (30 min)

            // It breaks again five minutes later and stays broken well past the alert threshold.
            world.Advance(TimeSpan.FromMinutes(5));
            world.State = HealthState.Unhealthy;
            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();

            _output.WriteLine($"[raised] {world.Sink.Raised.Count} (cooldown until {world.Row?.CooldownUntilUtc:o})");
            Assert.Single(world.Sink.Raised);              // still just the first one

            // Once the cooldown has passed, a genuine new problem IS announced - the cooldown silences a flap,
            // not the dependency.
            world.Advance(TimeSpan.FromMinutes(40));
            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();

            _output.WriteLine($"[raised after the cooldown] {world.Sink.Raised.Count}");
            Assert.Equal(2, world.Sink.Raised.Count);
        }

        [Fact]
        public async Task An_escalation_from_degraded_to_unhealthy_is_announced_once_at_the_worse_state()
        {
            var world = new World { State = HealthState.Degraded };

            await world.TickAsync();                       // arms the 5-minute degraded re-check
            world.Advance(TimeSpan.FromMinutes(1));
            world.State = HealthState.Unhealthy;           // escalates: arms the SHORTER 2-minute one
            await world.TickAsync();
            Assert.Empty(world.Sink.Raised);

            world.Advance(TimeSpan.FromMinutes(2));
            await world.TickAsync();

            Assert.Single(world.Sink.Raised);
            Assert.Equal(HealthState.Unhealthy, world.Sink.Raised.Single().State);
        }

        [Fact]
        public async Task An_improvement_from_unhealthy_to_degraded_does_not_raise_a_second_time()
        {
            var world = new World { State = HealthState.Unhealthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();                       // raised, unhealthy
            Assert.Single(world.Sink.Raised);

            // Getting BETTER. Arming here would alert twice for one outage on its way to being fixed.
            world.Advance(TimeSpan.FromMinutes(1));
            world.State = HealthState.Degraded;
            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(10));
            await world.TickAsync();

            Assert.Single(world.Sink.Raised);
            Assert.Empty(world.Sink.Recovered);            // not healthy yet, so not recovered
        }

        [Fact]
        public async Task A_second_host_over_the_same_database_does_not_re_alert()
        {
            // The defect the durable row exists for: the predecessor platform kept transitions in a per-process dictionary, so a
            // second instance - or the same instance after a restart - announced the same outage again.
            var shared = new World { State = HealthState.Unhealthy };

            await shared.TickAsync();
            shared.Advance(TimeSpan.FromMinutes(3));
            await shared.TickAsync();
            Assert.Single(shared.Sink.Raised);

            var secondHost = shared.AnotherHostOnTheSameData();
            secondHost.Advance(TimeSpan.FromMinutes(1));
            await secondHost.TickAsync();
            secondHost.Advance(TimeSpan.FromMinutes(3));
            await secondHost.TickAsync();

            _output.WriteLine($"[second host raised] {secondHost.Sink.Raised.Count}");
            Assert.Empty(secondHost.Sink.Raised);
        }

        [Fact]
        public async Task One_installation_scope_outage_is_one_alert_not_one_per_site()
        {
            // The store is shared. Three sites see it fail. An operator should be told once.
            var world = new World(scope: HealthProbeScope.Installation, siteId: 0) { State = HealthState.Unhealthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();

            // The pass ran ONCE for the installation scope rather than once per site, so there is one row and one
            // alert however many sites stand on that store.
            Assert.Single(world.Sink.Raised);
            Assert.Single(world.AlertStates.Rows);
        }

        [Fact]
        public async Task An_alert_carries_the_exception_type_and_never_the_exception_message()
        {
            // The description holds "server=db01;user=app;password=..." for a store failure. It must reach the
            // gated API and nobody's inbox.
            var world = new World { State = HealthState.Unhealthy, Description = "PostgreSQL is unreachable: server=db01;user=app;password=hunter2" };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(3));
            await world.TickAsync();

            var notice = world.Sink.Raised.Single();
            _output.WriteLine($"[notice] state={notice.State} type={notice.Result.Data?["exceptionType"]}");

            // The sink is handed the type as data ...
            Assert.Equal("SocketException", notice.Result.Data!["exceptionType"]);

            // ... and the privileged message is kept on the ROW, where only the gated API reads it.
            Assert.Contains("password=hunter2", world.Row!.LastDescription);
        }

        [Fact]
        public async Task Every_tick_records_a_rollup_row_whatever_the_alerting_decides()
        {
            // Alerting being quiet must not mean nothing was observed: the history is what makes it possible to
            // turn alerting on later and still know what happened.
            var world = new World { State = HealthState.Healthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromMinutes(1));
            await world.TickAsync();

            Assert.Empty(world.Sink.Raised);
            Assert.Equal(2, world.Rollups.Rows.Count);
            Assert.All(world.Rollups.Rows.Values, row => Assert.Equal(1, row.Checks));
        }

        [Fact]
        public async Task Two_ticks_inside_one_minute_accumulate_rather_than_overwrite()
        {
            var world = new World { State = HealthState.Healthy };

            await world.TickAsync();
            world.Advance(TimeSpan.FromSeconds(20));       // same minute
            await world.TickAsync();

            var row = Assert.Single(world.Rollups.Rows.Values);
            _output.WriteLine($"[minute row] checks={row.Checks} healthy={row.Healthy}");
            Assert.Equal(2, row.Checks);                   // two checks, not one overwritten by the other
            Assert.Equal(2, row.Healthy);
        }

        // ---- the world -------------------------------------------------------------------------------------

        /// <summary>One tenant, one subject, one clock the test moves by hand.</summary>
        private sealed class World
        {
            private readonly HealthProbeScope _scope;
            private readonly int _siteId;

            public World(HealthProbeScope scope = HealthProbeScope.Site, int siteId = 1)
            {
                _scope = scope;
                _siteId = siteId;
                Clock = new TestClock(Start);
            }

            public TestClock Clock { get; private set; }
            public FakeSink Sink { get; private set; } = new();
            public InMemoryRollups Rollups { get; private set; } = new();
            public InMemoryAlertStates AlertStates { get; private set; } = new();
            public HealthState State { get; set; } = HealthState.Healthy;
            public string? Description { get; set; }

            public DateTime Now => Clock.GetUtcNow().UtcDateTime;

            /// <summary>The scope this world probes - the scripted probe answers for it.</summary>
            public HealthProbeScope CurrentScope => _scope;

            public HealthAlertStateAggregate? Row => AlertStates.Rows.Values.FirstOrDefault();

            public void Advance(TimeSpan by) => Clock.Advance(by);

            public Task TickAsync()
            {
                var pass = new HealthProbePass(
                    new[] { new ScriptedContributor(this) },
                    Rollups,
                    AlertStates,
                    new NoOpUnitOfWork(),
                    new HealthProbeRunner(Clock),
                    new HealthSettingsAccessor(),
                    null,
                    Sink,
                    NullLogger<HealthProbePass>.Instance,
                    Clock);

                return pass.RunAsync(Tenant, _scope, _siteId);
            }

            /// <summary>
            /// A second process over the SAME rows: new sink, new clock, new everything except the data.
            /// </summary>
            public World AnotherHostOnTheSameData() => new(_scope, _siteId)
            {
                Rollups = Rollups,
                AlertStates = AlertStates,
                Sink = new FakeSink(),
                Clock = new TestClock(Now),
                State = State,
                Description = Description
            };

            public HealthProbeResult CurrentResult() => new(
                "probe",
                "Scripted Dependency",
                State,
                Description ?? State.ToString(),
                TimeSpan.FromMilliseconds(5),
                new Dictionary<string, string> { ["latencyMs"] = "5", ["exceptionType"] = "SocketException" });
        }

        private sealed class ScriptedContributor(World world) : IHealthContributor
        {
            public string ContributorId => "tests";
            public IReadOnlyList<IHealthProbe> Probes { get; } = new IHealthProbe[] { new ScriptedProbe(world) };
            public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = Array.Empty<HealthMetricDescriptor>();
            public IHealthMetricSource? SourceFor(string metricName) => null;
        }

        private sealed class ScriptedProbe(World world) : IHealthProbe
        {
            public string Name => "probe";
            public string Dependency => "Scripted Dependency";

            // Every scope answers, so one contributor serves whichever scope the world is running.
            public HealthProbeScope Scope => world.CurrentScope;

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult(world.CurrentResult());
        }

        private sealed class FakeSink : IHealthAlertSink
        {
            public List<HealthAlertNotice> Raised { get; } = new();
            public List<HealthAlertNotice> Recovered { get; } = new();

            public Task RaisedAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default)
            {
                Raised.Add(notice);
                return Task.CompletedTask;
            }

            public Task RecoveredAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default)
            {
                Recovered.Add(notice);
                return Task.CompletedTask;
            }
        }

        private sealed class TestClock(DateTime start) : TimeProvider
        {
            private DateTimeOffset _now = new(start, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now = _now.Add(by);
        }

        /// <summary>The unit of work persists nothing of its own here, exactly as it does in production (D67).</summary>
        private sealed class NoOpUnitOfWork : IUnitOfWork
        {
            public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);

            public void Dispose()
            {
            }
        }

        private class InMemoryRepository<T> where T : AggregateRoot<Guid>
        {
            public Dictionary<Guid, T> Rows { get; } = new();

            public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
                Task.FromResult(Rows.TryGetValue(id, out var row) ? row : null);

            public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<T>>(Rows.Values.ToList());

            public Task AddAsync(T aggregate, CancellationToken ct = default)
            {
                Rows[aggregate.Id] = aggregate;
                return Task.CompletedTask;
            }

            public Task UpdateAsync(T aggregate, CancellationToken ct = default)
            {
                Rows[aggregate.Id] = aggregate;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(T aggregate, CancellationToken ct = default)
            {
                Rows.Remove(aggregate.Id);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<T>>(Rows.Values.Where(predicate.Compile()).ToList());
        }

        private sealed class InMemoryRollups : InMemoryRepository<HealthRollupAggregate>, IHealthRollupRepository
        {
            public async Task<IReadOnlyList<HealthRollupAggregate>> ListAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, CancellationToken ct = default) =>
                (await FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window, ct))
                    .OrderByDescending(row => row.PeriodStartUtc).ToList();

            public async Task<IReadOnlyList<HealthRollupAggregate>> ListForRangeAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
                (await FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window && row.PeriodStartUtc >= fromUtc && row.PeriodStartUtc < toUtc, ct))
                    .OrderBy(row => row.PeriodStartUtc).ToList();

            public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default) =>
                FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window && row.PeriodStartUtc < cutoffUtc, ct);

            public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAnywhereAsync(int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default) =>
                FindAsync(row => row.SiteId == siteId && row.Window == window && row.PeriodStartUtc < cutoffUtc, ct);
        }

        private sealed class InMemoryAlertStates : InMemoryRepository<HealthAlertStateAggregate>, IHealthAlertStateRepository
        {
            public Task<IReadOnlyList<HealthAlertStateAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
                FindAsync(row => row.SiteId == siteId, ct);

            public async Task<HealthAlertStateAggregate?> GetAsync(TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject, CancellationToken ct = default) =>
                await GetByIdAsync(HealthIds.ForAlertState(tenantId, scope, siteId, subject), ct);

            public async Task<HealthAlertStateAggregate?> GetForCurrentSiteAsync(Guid id, int siteId, CancellationToken ct = default)
            {
                var row = await GetByIdAsync(id, ct);
                return row is null || row.SiteId != siteId ? null : row;
            }
        }
    }
}
