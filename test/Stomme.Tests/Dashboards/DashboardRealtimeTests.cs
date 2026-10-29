#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.EventBus;
using Stomme.Health;
using Stomme.Models;
using Stomme.Models.Health;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Modules.Dashboards.Realtime;
using Stomme.Modules.Datasets.Domain;
using Stomme.Shared;
using Stomme.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.Dashboards
{
    /// <summary>
    /// The real-time signal (the DashboardDataSignal cases, by name): a dataset's domain event in a
    /// site's scope becomes one notice - on the bus when one is composed, delivered in-process otherwise - and the notice
    /// reaches the push port with the site and the dataset, nothing else. No host: the handler over a service collection.
    /// </summary>
    public class DashboardRealtimeTests
    {
        [Fact]
        public async Task A_refresh_in_a_sites_scope_is_pushed_to_that_site_as_Refreshed()
        {
            var push = new RecordingPush();
            var datasetId = Guid.NewGuid();
            var signal = Signal(push, bus: null, tenantId: 3, siteId: 9);

            await signal.HandleAsync(new DatasetRefreshedEvent(datasetId));

            var pushed = Assert.Single(push.Pushes);
            Assert.Equal((3, 9), (pushed.TenantId, pushed.SiteId));
            Assert.Equal(new DashboardDatasetChange(datasetId, "Refreshed"), pushed.Change);
        }

        [Fact]
        public async Task A_schema_change_is_pushed_as_SchemaChanged()
        {
            var push = new RecordingPush();
            var datasetId = Guid.NewGuid();
            var signal = Signal(push, bus: null, tenantId: 1, siteId: 1);

            await signal.HandleAsync(new DatasetSchemaChangedEvent(datasetId));

            Assert.Equal("SchemaChanged", Assert.Single(push.Pushes).Change.Kind);
        }

        [Fact]
        public async Task Outside_a_sites_scope_nothing_is_pushed()
        {
            var push = new RecordingPush();
            var signal = Signal(push, bus: null, tenantId: null, siteId: null);

            await signal.HandleAsync(new DatasetRefreshedEvent(Guid.NewGuid()));

            Assert.Empty(push.Pushes);
        }

        [Fact]
        public async Task With_a_bus_composed_the_notice_rides_the_bus_and_its_handler_pushes()
        {
            var push = new RecordingPush();
            var bus = new RecordingBus();
            var datasetId = Guid.NewGuid();
            var signal = Signal(push, bus, tenantId: 2, siteId: 4);

            await signal.HandleAsync(new DatasetRefreshedEvent(datasetId));

            Assert.Empty(push.Pushes); // the signal published; it did not deliver
            var notice = Assert.IsType<DatasetChangedNotice>(Assert.Single(bus.Published));
            Assert.Equal(new DatasetChangedNotice(2, 4, datasetId, "Refreshed"), notice);

            var services = new ServiceCollection().AddSingleton<IDashboardRealtimePush>(push).BuildServiceProvider();
            await new DatasetChangedNoticeHandler(services, NullLogger<DatasetChangedNoticeHandler>.Instance).HandleAsync(notice);
            var pushed = Assert.Single(push.Pushes);
            Assert.Equal((2, 4, datasetId), (pushed.TenantId, pushed.SiteId, pushed.Change.DatasetId));
        }

        [Fact]
        public async Task The_in_process_signal_is_raised_for_the_sites_key_alongside_the_push()
        {
            var push = new RecordingPush();
            var signal = new DashboardDataSignal();
            var raised = new List<(string SiteKey, DashboardDatasetChange Change)>();
            signal.DatasetChanged += (siteKey, change) => raised.Add((siteKey, change));
            var services = new ServiceCollection().AddSingleton<IDashboardRealtimePush>(push).AddSingleton<IDashboardDataSignal>(signal).BuildServiceProvider();
            var datasetId = Guid.NewGuid();

            await new DatasetChangedNoticeHandler(services, NullLogger<DatasetChangedNoticeHandler>.Instance).HandleAsync(new DatasetChangedNotice(5, 6, datasetId, "Refreshed"));

            Assert.Equal((new Alias { TenantId = 5, SiteId = 6 }.SiteKey, new DashboardDatasetChange(datasetId, "Refreshed")), Assert.Single(raised));
            Assert.Single(push.Pushes);
        }

        [Fact]
        public async Task Without_a_push_port_the_notice_is_dropped_not_thrown()
        {
            var services = new ServiceCollection().BuildServiceProvider();
            await new DatasetChangedNoticeHandler(services, NullLogger<DatasetChangedNoticeHandler>.Instance).HandleAsync(new DatasetChangedNotice(1, 1, Guid.NewGuid(), "Refreshed"));
        }

        [Fact]
        public void The_push_is_a_no_op_while_the_SignalR_transport_is_off()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new TransportOptions());
            new Stomme.Modules.Dashboards.Startup.ServerStartup().ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            Assert.IsType<NoDashboardRealtimePush>(provider.GetRequiredService<IDashboardRealtimePush>());
        }

        [Fact]
        public void The_hub_group_is_the_site_key()
        {
            Assert.Equal("dashboards:" + new Alias { TenantId = 7, SiteId = 11 }.SiteKey, DashboardHub.GroupFor(7, 11));
        }

        // ---- the pivot widget's domain rules ----------------------------------------------------------------------

        [Fact]
        public void A_pivot_widget_binds_at_least_the_column_it_groups_by()
        {
            var unbound = new DataBinding(Guid.NewGuid(), Array.Empty<string>());
            Assert.Throws<InvalidOperationException>(() => Widget.Create(Guid.NewGuid(), "By region", WidgetKind.Pivot, unbound, new GridPosition(0, 0, 4, 4), RefreshMode.Manual));

            var bound = new DataBinding(Guid.NewGuid(), new[] { "region", "amount" });
            var widget = Widget.Create(Guid.NewGuid(), "By region", WidgetKind.Pivot, bound, new GridPosition(0, 0, 4, 4), RefreshMode.Manual);
            Assert.Equal(WidgetKind.Pivot, widget.Kind);
            Assert.Throws<InvalidOperationException>(() => Widget.Create(Guid.NewGuid(), "By region", WidgetKind.Pivot, bound, new GridPosition(0, 0, 4, 4), RefreshMode.Manual, chartSpec: new ChartSpec("{\"series\":[]}")));
        }

        // ---- the pivot's computation (Shared, BCL-only) -------------------------------------------------------------

        [Fact]
        public void A_pivot_groups_in_first_seen_order_counts_and_sums_the_numeric_value_column()
        {
            var rows = Rows(("region", "north", "amount", "10"), ("region", "south", "amount", "2.5"), ("region", "north", "amount", "5"));

            var pivot = PivotTable.Compute(rows, "region", "amount");

            Assert.Equal(new[] { new PivotRow("north", 2, 15), new PivotRow("south", 1, 2.5) }, pivot);
        }

        [Fact]
        public void A_pivot_without_a_value_column_counts_only()
        {
            var rows = Rows(("region", "north", "amount", "10"), ("region", "north", "amount", "x"));

            Assert.Equal(new[] { new PivotRow("north", 2, null) }, PivotTable.Compute(rows, "region", null));
        }

        [Fact]
        public void A_non_numeric_value_makes_the_groups_sum_unavailable_never_wrong_and_a_missing_key_is_the_empty_group()
        {
            var rows = new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["region"] = "north", ["amount"] = "10" },
                new Dictionary<string, string> { ["region"] = "north", ["amount"] = "ten" },
                new Dictionary<string, string> { ["amount"] = "3" },
            };

            var pivot = PivotTable.Compute(rows, "region", "amount");

            Assert.Equal(new[] { new PivotRow("north", 2, null), new PivotRow(string.Empty, 1, 3) }, pivot);
        }

        // ---- the health sparklines (Shared, BCL-only) -------------------------------------------------------------

        [Fact]
        public void A_sparkline_orders_the_subjects_periods_oldest_first_with_full_availability_at_the_top()
        {
            var at = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            var rollups = new[]
            {
                Rollup("db", at.AddHours(2), 50),
                Rollup("db", at, 100),
                Rollup("cache", at, 0),
                Rollup("db", at.AddHours(1), 0),
            };

            var points = HealthSparkline.Points(rollups, "db");

            Assert.Equal("1,1 48,19 95,10", points);
            var svg = HealthSparkline.Svg(rollups, "DB");
            Assert.Contains("points=\"1,1 48,19 95,10\"", svg);
            Assert.Contains("stroke=\"currentColor\"", svg);
            Assert.DoesNotContain("#", svg); // no colour of its own: the theme's text colour
        }

        [Fact]
        public void A_subject_with_fewer_than_two_periods_draws_nothing()
        {
            var rollups = new[] { Rollup("db", DateTime.UtcNow, 100) };

            Assert.Equal(string.Empty, HealthSparkline.Svg(rollups, "db"));
            Assert.Equal(string.Empty, HealthSparkline.Svg(rollups, "missing"));
        }

        [Fact]
        public void The_subject_is_escaped_in_the_label()
        {
            var at = DateTime.UtcNow;
            var svg = HealthSparkline.Svg(new[] { Rollup("a<b", at, 1), Rollup("a<b", at.AddHours(1), 1) }, "a<b");

            Assert.Contains("aria-label=\"a&lt;b availability\"", svg);
        }

        private static HealthRollupView Rollup(string subject, DateTime periodStart, double availability) =>
            new(subject, HealthProbeScope.Installation, "Hour", periodStart, 10, 10, 0, 0, 1, 2, availability);

        private static List<IReadOnlyDictionary<string, string>> Rows(params (string K1, string V1, string K2, string V2)[] rows) =>
            rows.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { [row.K1] = row.V1, [row.K2] = row.V2 }).ToList();

        private static DatasetChangeSignal Signal(RecordingPush push, RecordingBus? bus, int? tenantId, int? siteId)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IDashboardRealtimePush>(push);
            if (bus is not null)
            {
                services.AddSingleton<IEventBus>(bus);
            }

            var siteState = new SiteState { Alias = tenantId is { } tenant && siteId is { } site ? new Alias { AliasId = 1, TenantId = tenant, SiteId = site, Name = "localhost" } : null };
            return new DatasetChangeSignal(services.BuildServiceProvider(), siteState, NullLogger<DatasetChangeSignal>.Instance);
        }

        internal sealed class RecordingPush : IDashboardRealtimePush
        {
            private readonly ConcurrentQueue<(int TenantId, int SiteId, DashboardDatasetChange Change)> _queue = new();

            public IReadOnlyList<(int TenantId, int SiteId, DashboardDatasetChange Change)> Pushes => _queue.ToList();

            public Task PushAsync(int tenantId, int siteId, DashboardDatasetChange change, CancellationToken cancellationToken = default)
            {
                _queue.Enqueue((tenantId, siteId, change));
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingBus : IEventBus
        {
            public List<object> Published { get; } = new();

            public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : class
            {
                Published.Add(@event);
                return Task.CompletedTask;
            }

            public Task PublishAsync<TEvent>(TEvent @event, IDictionary<string, string>? headers = null, CancellationToken ct = default) where TEvent : class => PublishAsync(@event, ct);

            public Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task SendAsync<TCommand>(TCommand command, string destinationQueue, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : class => throw new NotSupportedException();

            public Task DeferAsync<TMessage>(TimeSpan delay, TMessage message, CancellationToken ct = default) where TMessage : class => throw new NotSupportedException();
        }
    }
}
