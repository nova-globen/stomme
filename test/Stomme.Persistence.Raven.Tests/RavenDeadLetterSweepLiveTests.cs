using System.Globalization;
using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.Persistence.RavenDb;
using Stomme.Persistence.RavenDb.Messaging;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Rebus.Messages;
using Rebus.Transport;
using Xunit;

namespace Stomme.Persistence.Raven.Tests;

/// <summary>
/// BL-100 O2 (D109) — the ONLY real cover of the dead-letter sweep: <see cref="DeadLetterSweeper"/> over the real
/// <see cref="RavenDbDeadLetterQueue"/> on a live RavenDB server (the host's <c>DeadLetterSweepJob</c> delegates to it;
/// on every other transport the sweep is a stated no-op). Fail-not-skip per D46: an unreachable server fails
/// <see cref="InitializeAsync"/>.
/// </summary>
/// <remarks>
/// Lives here, not in Stomme.MessageBus.Live.Tests, because <c>RavenTransportMessage</c> and the transport are
/// internal and only this project and Stomme.MessageBus.Tests see them. Dead letters are produced through the REAL
/// <see cref="RavenDbTransport"/> where the scenario is a transport move (the send stamps <c>VisibleUtc</c>), and planted
/// as documents only where the scenario needs a visible time in the past. The scratch database is deleted through a
/// FRESH store, never the one the test used (the RavenTestDatabaseJanitor trap).
/// </remarks>
public sealed class RavenDeadLetterSweepLiveTests : IAsyncLifetime
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://localhost:8080";

    private readonly string _database = "stomme-dlq-sweep-" + Guid.NewGuid().ToString("N");
    private const string InputQueue = "sweep-input";
    private const string ErrorQueue = "sweep-input.error";
    private DocumentStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();

        using var fresh = new DocumentStore { Urls = [Url], Database = _database };
        fresh.Initialize();
        try
        {
            await fresh.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
        }
        catch
        {
            // best-effort cleanup; RavenTestDatabaseJanitor sweeps leftovers
        }
    }

    [Fact]
    public async Task The_sweep_ages_by_visible_time_so_old_sends_and_re_dead_letters_survive()
    {
        var now = DateTimeOffset.UtcNow;
        var transport = new RavenDbTransport(_store, InputQueue, TimeSpan.FromMinutes(5));
        var queue = new RavenDbDeadLetterQueue(_store, InputQueue, ErrorQueue);

        // 1. Sent 90 days ago, moved to the error queue NOW by the transport: VisibleUtc = now.
        await SendAsync(transport, ErrorQueue, Headers("sent-long-ago", now.AddDays(-90)));

        // InitializeStommeStore disables the identity-property convention, so ids are passed explicitly (as the transport does).
        // 2. Sent 120 days ago, dead-lettered 60 days ago, retried by an admin, failed again.
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new RavenTransportMessage
            {
                Id = "RavenTransportMessages/retried",
                Queue = ErrorQueue,
                Headers = Headers("retried", now.AddDays(-120)),
                Body = [1],
                VisibleUtc = now.AddDays(-60).UtcDateTime,
            }, "RavenTransportMessages/retried");

            // 3. A plain old dead letter: dead-lettered 60 days ago.
            await session.StoreAsync(new RavenTransportMessage
            {
                Id = "RavenTransportMessages/old",
                Queue = ErrorQueue,
                Headers = Headers("old", now.AddDays(-60)),
                Body = [1],
                VisibleUtc = now.AddDays(-60).UtcDateTime,
            }, "RavenTransportMessages/old");
            await session.SaveChangesAsync();
        }

        Assert.True(await queue.RetryAsync("RavenTransportMessages/retried"), "the retry must return it to the input queue");
        await ReDeadLetterAsync(transport);

        var before = await queue.ListAsync(1000);
        Assert.Equal(3, before.Count);
        var retried = Assert.Single(before, m => m.Headers[Rebus.Messages.Headers.MessageId] == "retried");
        // The mutant that aged by EnqueuedUtc would discard this one: its SentTime is 120 days old.
        Assert.True(retried.EnqueuedUtc < now.AddDays(-100), $"EnqueuedUtc is the original send time, was {retried.EnqueuedUtc:u}");
        Assert.True(retried.VisibleUtc > now.AddMinutes(-5), $"VisibleUtc is the re-dead-letter time, was {retried.VisibleUtc:u}");

        var result = await DeadLetterSweeper.SweepAsync([queue], DateTimeOffset.UtcNow, TimeSpan.FromDays(30));

        Assert.True(result.Swept);
        Assert.Equal(3, result.Examined);
        Assert.Equal(1, result.Discarded);

        var after = await queue.ListAsync(1000);
        Assert.Equal(
            new[] { "retried", "sent-long-ago" },
            after.Select(m => m.Headers[Rebus.Messages.Headers.MessageId]).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        using (var session = _store.OpenAsyncSession())
        {
            Assert.Null(await session.LoadAsync<RavenTransportMessage>("RavenTransportMessages/old"));
        }

        // A second pass in the same tick (the per-tenant loop) discards nothing.
        var second = await DeadLetterSweeper.SweepAsync([queue], DateTimeOffset.UtcNow, TimeSpan.FromDays(30));
        Assert.Equal(0, second.Discarded);
        Assert.Equal(2, await queue.CountAsync());
    }

    private static Dictionary<string, string> Headers(string messageId, DateTimeOffset sentTime) => new()
    {
        [Rebus.Messages.Headers.MessageId] = messageId,
        [Rebus.Messages.Headers.Type] = "Stomme.Tests.SweepProbe, Stomme.Tests",
        [Rebus.Messages.Headers.SentTime] = sentTime.ToString("O", CultureInfo.InvariantCulture),
        [Rebus.Messages.Headers.ErrorDetails] = "poisoned",
    };

    private static async Task SendAsync(RavenDbTransport transport, string destination, Dictionary<string, string> headers)
    {
        using var scope = new RebusTransactionScope();
        await transport.Send(destination, new TransportMessage(headers, [1]), scope.TransactionContext);
        await scope.CompleteAsync();
    }

    /// <summary>What Rebus does on a failed delivery: receive from the input queue, send the same message to the error queue, ack.</summary>
    private static async Task ReDeadLetterAsync(RavenDbTransport transport)
    {
        using var scope = new RebusTransactionScope();
        var received = await transport.Receive(scope.TransactionContext, CancellationToken.None);
        Assert.NotNull(received);
        await transport.Send(ErrorQueue, new TransportMessage(new Dictionary<string, string>(received.Headers), received.Body), scope.TransactionContext);
        await scope.CompleteAsync();
    }
}