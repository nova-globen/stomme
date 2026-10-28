using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.Persistence.RavenDb;
using Stomme.Persistence.RavenDb.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.MessageBus.Live.Tests;

/// <summary>
/// Phase 3 — proves Stomme's custom Rebus RavenDB transport round-trips a real message through a REAL
/// RavenDB server: queue documents, optimistic-concurrency leases, and the centralized subscription
/// storage that makes publish/subscribe work. Also exercises the RavenDB-backed dead-letter admin port.
/// Requires a live server; see the csproj header for the docker run command.
/// </summary>
public sealed class RavenDbTransportLiveTests : IAsyncLifetime
{
    // BL-15: gated in Stomme.slnx; defaults to the infra UAT tier (the PostgresTestEndpoint convention —
    // env-overridable via RAVENDB_TEST_URL, defaulting to the shared instance). The old defaults are all
    // RETIRED: local :7000 (uninstalled 2026-07-23), Docker-era :8091/:8090.
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://localhost:8080";

    private static bool Available => Probe.HttpOk(Url);

    private readonly string _database = "stomme-bus-" + Guid.NewGuid().ToString("N");
    private DocumentStore? _store;

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
    }

    public async Task DisposeAsync()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
        }
        catch
        {
            // best-effort cleanup
        }

        _store.Dispose();
    }

    [Fact]
    public async Task Integration_event_round_trips_over_the_live_ravendb_transport()
    {
        Assert.True(Available, $"No RavenDB server reachable at {Url}. See the csproj header for the docker run command.");

        var queue = "stomme-live-" + Guid.NewGuid().ToString("n")[..8];
        using var host = BuildHost(queue);
        await host.StartAsync();
        try
        {
            var invoiceId = Guid.NewGuid();
            using (var scope = host.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                // The token argument also disambiguates the headers overload (see REPORT.md).
                await bus.PublishAsync(new InvoiceIssued(invoiceId, 99.95m), CancellationToken.None);
            }

            var sink = host.Services.GetRequiredService<DeliverySink>();
            var received = await sink.Received.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(invoiceId, received.InvoiceId);
            Assert.Equal(99.95m, received.Amount);

            // The DLQ admin port is backed by the transport's own queue documents; a clean run has none.
            var dlq = host.Services.GetRequiredService<IDeadLetterQueue>();
            Assert.Equal(0, await dlq.CountAsync());
            Assert.False(await dlq.RetryAsync("RavenTransportMessages/does-not-exist"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private IHost BuildHost(string queue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<DeliverySink>();
        builder.Services.AddSingleton<IDocumentStore>(_store!);

        // The host supplies the transport configurator: the RavenDB transport needs the shared store,
        // so it is selected by code rather than by the Messaging:Transport config string.
        builder.Services.AddMessaging(
            [typeof(RavenDbTransportLiveTests).Assembly],
            configure: options =>
            {
                options.InputQueue = queue;
                options.ErrorQueue = queue + ".error";
            },
            transport: (configurer, provider) =>
                configurer.UseRavenDb(provider.GetRequiredService<IDocumentStore>(), queue));

        // Overrides the null fallback AddMessaging registers.
        builder.Services.AddRavenDbDeadLetterQueue(queue, queue + ".error");
        builder.Services.AddBusSubscription<InvoiceIssued>();
        return builder.Build();
    }
}
