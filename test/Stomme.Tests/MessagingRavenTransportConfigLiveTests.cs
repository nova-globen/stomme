using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Proves <c>Messaging:Transport=ravendb</c> — pure configuration through the host's own
    /// <c>AddStommeMessaging</c>, no code-passed configurator — composes the RavenDB-backed bus and
    /// round-trips a published integration event through a LIVE RavenDB server. Raven-ness is asserted,
    /// not assumed: the subscription this host registers at startup must exist as a document in this
    /// test's own database (the centralized <c>RavenDbSubscriptionStorage</c> only the ravendb branch
    /// wires), and the resolved <see cref="IDeadLetterQueue"/> must be the RavenDB implementation — an
    /// accidental in-memory fallback delivers the event too, but writes no documents and keeps the null
    /// DLQ, so it fails here.
    /// <para>
    /// Like the parity suites, this FAILS rather than skips when RavenDB is unreachable
    /// (<c>RAVENDB_TEST_URL</c>): a config-selected transport that was never driven against a real
    /// store proves nothing.
    /// </para>
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class MessagingRavenTransportConfigLiveTests : IAsyncLifetime
    {
        private const string DatabasePrefix = "stomme-bus-config-";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = DatabasePrefix + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        public async Task InitializeAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            // A KILLED prior run never reached DisposeAsync — sweep its leftovers before creating ours.
            await RavenTestDatabaseJanitor.SweepAsync(_store, DatabasePrefix);
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        public async Task DisposeAsync()
        {
            // Delete through a FRESH store: the shared store's executor may already be torn down, and a
            // delete through it throws into a swallowing catch (the historical Raven-leftovers bug).
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: an unreachable server leaves the database for the next run's janitor sweep
            }
        }

        [Fact]
        public async Task Config_selected_ravendb_transport_round_trips_a_published_event()
        {
            var queue = "stomme-cfg-" + Guid.NewGuid().ToString("n")[..8];

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:Enabled"] = "true",
                ["Messaging:Transport"] = "ravendb",
                ["Messaging:InputQueue"] = queue,
            });

            // What the real host wires via AddRavenFrameworkStore (lazily); the messaging extension must
            // consume this registration rather than build a store of its own.
            builder.Services.AddSingleton<IDocumentStore>(_store);
            builder.Services.AddStommeMessaging(builder.Configuration);
            builder.Services.AddBusSubscription<RavenConfigProbeEvent>();

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var probeId = Guid.NewGuid();
                var delivered = RavenConfigProbeDeliveries.Expect(probeId);
                using (var scope = host.Services.CreateScope())
                {
                    var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                    await bus.PublishAsync(new RavenConfigProbeEvent(probeId, "state-of-the-art"), CancellationToken.None);
                }

                var received = await delivered.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(probeId, received.ProbeId);
                Assert.Equal("state-of-the-art", received.Payload);

                // The startup subscription must be a DOCUMENT in this run's database — written by
                // RavenDbSubscriptionStorage, which only the ravendb transport branch registers.
                using (var session = _store.OpenAsyncSession())
                {
                    var subscriptions = await session.Advanced.LoadStartingWithAsync<SubscriptionProbe>("RavenSubscriptions/");
                    Assert.Contains(
                        subscriptions,
                        s => s.Topic != null && s.Topic.Contains(nameof(RavenConfigProbeEvent), StringComparison.Ordinal)
                            && s.Address == queue);
                }

                // The DLQ admin port is the RavenDB one over the transport's queue documents; a clean run
                // dead-letters nothing.
                var deadLetterQueue = host.Services.GetRequiredService<IDeadLetterQueue>();
                Assert.Equal("RavenDbDeadLetterQueue", deadLetterQueue.GetType().Name);
                Assert.Equal(0, await deadLetterQueue.CountAsync());
            }
            finally
            {
                await host.StopAsync();
            }
        }

        /// <summary>Shape-only projection of the transport's subscription documents.</summary>
        private sealed class SubscriptionProbe
        {
            public string? Topic { get; set; }

            public string? Address { get; set; }
        }
    }

    /// <summary>The integration event round-tripped through the config-selected RavenDB transport.</summary>
    public sealed record RavenConfigProbeEvent(Guid ProbeId, string Payload) : IntegrationEvent;

    /// <summary>
    /// Static, probe-id-keyed delivery sink. Static on purpose: <c>AddStommeMessaging</c>'s AppDomain
    /// scan discovers every <see cref="IIntegrationEventHandler{TEvent}"/> in every loaded Stomme
    /// assembly — INCLUDING this test assembly — so <see cref="RavenConfigProbeConsumer"/> is also
    /// registered when <c>HostCompositionTests</c> composes the REAL host under ValidateOnBuild. A
    /// constructor dependency on a test-local sink fails that composition ("unable to resolve ...
    /// RavenConfigDeliverySink", measured); a parameterless handler routing through this class is
    /// constructible in any host that scans it.
    /// </summary>
    public static class RavenConfigProbeDeliveries
    {
        private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<RavenConfigProbeEvent>> Pending = new();

        public static Task<RavenConfigProbeEvent> Expect(Guid probeId) => Source(probeId).Task;

        public static void Record(RavenConfigProbeEvent @event) => Source(@event.ProbeId).TrySetResult(@event);

        private static TaskCompletionSource<RavenConfigProbeEvent> Source(Guid probeId) =>
            Pending.GetOrAdd(probeId, _ => new TaskCompletionSource<RavenConfigProbeEvent>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// Discovered by AddStommeMessaging's own AppDomain scan (this test assembly passes the framework's
    /// Stomme assembly filter) — the same convention path a module's handler rides. Parameterless: see
    /// <see cref="RavenConfigProbeDeliveries"/>.
    /// </summary>
    public sealed class RavenConfigProbeConsumer : IIntegrationEventHandler<RavenConfigProbeEvent>
    {
        public Task HandleAsync(RavenConfigProbeEvent @event, CancellationToken ct = default)
        {
            RavenConfigProbeDeliveries.Record(@event);
            return Task.CompletedTask;
        }
    }
}
