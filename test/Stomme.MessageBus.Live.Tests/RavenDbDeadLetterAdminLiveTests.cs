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
/// Drives the FULL dead-letter admin lifecycle against a REAL RavenDB server: a poisoned
/// consumer exhausts delivery attempts → the message lands in the DLQ (List/Count see it) → RetryAsync
/// returns it to the input queue and, once the poison is released, the consumer RECEIVES it →
/// DiscardAsync permanently removes a second poisoned message. This is the port the admin UI's
/// controller fronts; the UI mapping is pinned separately in Stomme.Tests.
/// </summary>
public sealed class RavenDbDeadLetterAdminLiveTests : IAsyncLifetime
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") is { Length: > 0 } url ? url : "http://127.0.0.1:7000";

    private static bool Available => Probe.HttpOk(Url);

    private readonly string _database = "stomme-dlq-" + Guid.NewGuid().ToString("N");
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
    public async Task Dead_letters_can_be_listed_retried_to_redelivery_and_discarded()
    {
        Assert.True(Available, $"No RavenDB server reachable at {Url}.");

        var queue = "stomme-dlq-" + Guid.NewGuid().ToString("n")[..8];
        using var host = BuildHost(queue);
        await host.StartAsync();
        try
        {
            var dlq = host.Services.GetRequiredService<IDeadLetterQueue>();

            // ---- 1. a poisoned message exhausts its single delivery attempt and dead-letters ----------
            PoisonState.Poisoned = true;
            var invoiceId = Guid.NewGuid();
            using (var scope = host.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishAsync(new InvoiceIssued(invoiceId, 13.13m), CancellationToken.None);
            }

            var deadLetter = await WaitForDeadLetterAsync(dlq, TimeSpan.FromSeconds(60));
            Assert.Contains(nameof(InvoiceIssued), deadLetter.MessageType ?? "");
            Assert.False(string.IsNullOrEmpty(deadLetter.ErrorDetails), "the captured failure details must surface");
            Assert.Equal(1, await dlq.CountAsync());

            // ---- 2. retry returns it to the input queue; with the poison released it is DELIVERED -----
            PoisonState.Poisoned = false;
            Assert.True(await dlq.RetryAsync(deadLetter.Id), "retry of an existing dead letter must succeed");
            var received = await PoisonState.DeliveredFor(invoiceId).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(invoiceId, received.InvoiceId);
            Assert.Equal(0, await dlq.CountAsync());

            // ---- 3. a second poisoned message is DISCARDED permanently --------------------------------
            PoisonState.Poisoned = true;
            using (var scope = host.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishAsync(new InvoiceIssued(Guid.NewGuid(), 66.60m), CancellationToken.None);
            }
            var second = await WaitForDeadLetterAsync(dlq, TimeSpan.FromSeconds(60));

            Assert.True(await dlq.DiscardAsync(second.Id), "discard of an existing dead letter must succeed");
            Assert.Equal(0, await dlq.CountAsync());
            Assert.False(await dlq.RetryAsync(second.Id), "a discarded message must be gone for good");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<DeadLetterMessage> WaitForDeadLetterAsync(IDeadLetterQueue dlq, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var messages = await dlq.ListAsync();
            if (messages.Count > 0)
            {
                return messages[0];
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"No message reached the dead-letter queue within {timeout.TotalSeconds:0}s.");
    }

    private IHost BuildHost(string queue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<DeliverySink>();
        builder.Services.AddSingleton<IDocumentStore>(_store!);

        builder.Services.AddMessaging(
            [typeof(RavenDbDeadLetterAdminLiveTests).Assembly],
            configure: options =>
            {
                options.InputQueue = queue;
                options.ErrorQueue = queue + ".error";
                // One shot: the first failed delivery dead-letters the message, keeping the drive fast.
                options.MaxDeliveryAttempts = 1;
            },
            transport: (configurer, provider) =>
                configurer.UseRavenDb(provider.GetRequiredService<IDocumentStore>(), queue));

        builder.Services.AddRavenDbDeadLetterQueue(queue, queue + ".error");
        builder.Services.AddBusSubscription<InvoiceIssued>();
        return builder.Build();
    }
}

/// <summary>
/// STATIC poison switch + success-only sink. Static (and the consumer parameterless) because handler
/// discovery scans the whole assembly: a constructor dependency here would break the OTHER live test's
/// host, whose container does not register it (the host validates its container at build, so an unregistered
/// dependency stops the host from starting). The sink only
/// completes on an UNPOISONED delivery, so an await on it cannot be satisfied by the failed attempt
/// that dead-lettered the message.
/// </summary>
public static class PoisonState
{
    public static volatile bool Poisoned;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<InvoiceIssued>> _delivered = new();

    public static Task<InvoiceIssued> DeliveredFor(Guid invoiceId) =>
        _delivered.GetOrAdd(invoiceId, _ => new TaskCompletionSource<InvoiceIssued>(
            TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void RecordDelivered(InvoiceIssued @event) =>
        _delivered.GetOrAdd(@event.InvoiceId, _ => new TaskCompletionSource<InvoiceIssued>(
            TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(@event);
}

/// <summary>
/// The poisonable consumer. It handles the same <see cref="InvoiceIssued"/> event as
/// <see cref="InvoiceIssuedConsumer"/> (LiveContracts) — both are discovered and dispatched; a throw
/// from either fails the whole delivery, which is exactly what the poison needs.
/// </summary>
public sealed class PoisonableInvoiceConsumer : IIntegrationEventHandler<InvoiceIssued>
{
    public Task HandleAsync(InvoiceIssued @event, CancellationToken ct = default)
    {
        if (PoisonState.Poisoned)
        {
            throw new InvalidOperationException("poisoned - this delivery must fail so the message dead-letters");
        }
        PoisonState.RecordDelivered(@event);
        return Task.CompletedTask;
    }
}
