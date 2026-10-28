using Stomme.Infrastructure.EventBus;
using Stomme.MessageBus.Tests.Ordering.Contracts;
using Stomme.MessageBus.Tests.Ordering.Domain;
using Stomme.MessageBus.Tests.Shipping;
using Stomme.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stomme.MessageBus.Tests;

/// <summary>
/// Phase 2 — the transactional outbox bridging domain events to integration events on a REAL relational
/// store (SQLite on disk, so every DI scope and the processor's own scope share one database file).
///
/// The chain under test: aggregate raises a domain event -> EfRepository writes the aggregate AND an
/// outbox row in one SaveChanges -> OutboxProcessor drains the row -> the bridge domain-event handler
/// publishes an integration event -> another module's integration handler receives it.
/// </summary>
public sealed class OutboxBridgeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TenantId Tenant = TenantId.From(Guid.NewGuid());

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"stomme-outbox-{Guid.NewGuid():n}.db");

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ShipmentLog>();
        builder.Services.AddSingleton<ITenantContext>(new TestTenantContext(Tenant));

        builder.Services.AddDbContext<OrderingDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        builder.Services.AddEfCorePersistence<OrderingDbContext>();

        // Registering the outbox (and with it IDomainEventSerializer) is what flips EfRepository from
        // in-process dispatch to writing outbox rows inside the aggregate's transaction.
        builder.Services.AddOutbox(
            options => options.RetryBackoff = TimeSpan.Zero,
            typeof(OutboxBridgeTests).Assembly);

        // Drop the background poller so the tests drive OutboxProcessor themselves: its first iteration
        // runs immediately on host start and would otherwise race the drain we want to observe. (This is
        // exactly why the processor is exposed as a callable unit and not only as a loop.)
        var poller = builder.Services.Single(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.Name == "OutboxBackgroundService");
        builder.Services.Remove(poller);

        builder.Services.AddMessaging(
            [typeof(OutboxBridgeTests).Assembly],
            configure: options =>
            {
                options.InputQueue = "stomme-outbox-tests";
                options.ErrorQueue = "stomme-outbox-tests.error";
            });
        builder.Services.AddBusSubscription<OrderPlaced>();
        return builder.Build();
    }

    [Fact]
    public async Task Domain_event_reaches_another_module_as_an_integration_event_through_the_outbox()
    {
        using var host = BuildHost();
        await Migrate(host);
        await host.StartAsync();
        try
        {
            var orderId = Guid.NewGuid();

            // 1. Ordering places an order. The aggregate + its outbox row commit together.
            using (var scope = host.Services.CreateScope())
            {
                var orders = scope.ServiceProvider.GetRequiredService<IRepository<Order, Guid>>();
                await orders.AddAsync(Order.Place(orderId, "grace@example.com"));
            }

            // 2. The outbox row is durably captured, pending, and stamped with the raising tenant.
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var pending = await db.Set<OutboxMessage>().SingleAsync();

                Assert.Null(pending.ProcessedOnUtc);
                Assert.Equal(Tenant.Value, pending.TenantId);
                Assert.Equal(typeof(OrderPlacedDomainEvent).FullName, pending.EventType);
                Assert.Contains(orderId.ToString(), pending.Payload);
                // The aggregate itself committed in the same transaction.
                Assert.NotNull(await db.Orders.FindAsync(orderId));
            }

            // 3. Drain the outbox: the bridge handler turns the domain event into an integration event.
            var processor = host.Services.GetRequiredService<OutboxProcessor>();
            var processed = await processor.ProcessPendingAsync();
            Assert.Equal(1, processed);

            // 4. The Shipping module receives it over the bus, having never seen the domain event.
            var log = host.Services.GetRequiredService<ShipmentLog>();
            var received = await log.Received.WaitAsync(Timeout);
            Assert.Equal(orderId, received.OrderId);
            Assert.Equal("grace@example.com", received.Customer);

            // 5. The row is marked processed, so a second drain is a no-op (at-least-once, not at-least-twice).
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                Assert.NotNull((await db.Set<OutboxMessage>().SingleAsync()).ProcessedOnUtc);
            }
            Assert.Equal(0, await processor.ProcessPendingAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Outbox_capture_is_rolled_back_with_the_aggregate()
    {
        using var host = BuildHost();
        await Migrate(host);

        // A failed transaction must leave no outbox row — otherwise an integration event would announce
        // an order that does not exist. Same SaveChanges => same transaction => all or nothing.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();

            var orders = scope.ServiceProvider.GetRequiredService<IRepository<Order, Guid>>();
            await orders.AddAsync(Order.Place(Guid.NewGuid(), "rolled-back@example.com"));

            await tx.RollbackAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync());
            Assert.Empty(await db.Orders.ToListAsync());
        }
    }

    private static async Task Migrate(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public void Dispose()
    {
        // SQLite pools connections; release THIS database's so the temp file can be deleted, by the exact string
        // AddDbContext used. ClearAllPools is process-wide and drops the pooled connections of whatever else is
        // running, mid-request (BL-104).
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
