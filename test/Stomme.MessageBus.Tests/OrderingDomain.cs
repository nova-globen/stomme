using Stomme.EventBus;
using Stomme.MessageBus.Tests.Ordering.Contracts;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Stomme.MessageBus.Tests.Ordering.Domain;

/// <summary>
/// Raised inside the Ordering bounded context when an order is placed. A DOMAIN event: internal to the
/// context, never a cross-module contract. It reaches other contexts only by being bridged to the
/// <see cref="OrderPlaced"/> integration event — transactionally, via the outbox.
/// </summary>
public sealed record OrderPlacedDomainEvent(Guid OrderId, string Customer) : DomainEvent;

/// <summary>The Ordering aggregate. Raises its domain event on creation; the repository captures it.</summary>
public sealed class Order : AggregateRoot<Guid>
{
    // EF materialization ctor.
    private Order()
    {
    }

    private Order(Guid id, string customer)
    {
        Id = id;
        Customer = customer;
        RaiseDomainEvent(new OrderPlacedDomainEvent(id, customer));
    }

    public string Customer { get; private set; } = string.Empty;

    public static Order Place(Guid id, string customer) => new(id, customer);
}

/// <summary>Ordering's relational store. Inherits the outbox table mapping from the base context.</summary>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options, ITenantContext tenantContext)
    : StommeDbContext(options, tenantContext)
{
    public DbSet<Order> Orders => Set<Order>();
}

/// <summary>
/// THE BRIDGE: a domain-event handler that republishes an internal domain event as a cross-context
/// integration event. The outbox processor invokes it only after the aggregate's transaction committed,
/// so the integration event cannot escape for an order that was rolled back.
/// </summary>
public sealed class OrderPlacedBridge(IEventBus bus) : IDomainEventHandler<OrderPlacedDomainEvent>
{
    public Task HandleAsync(OrderPlacedDomainEvent domainEvent, CancellationToken ct = default) =>
        bus.PublishAsync(new OrderPlaced(domainEvent.OrderId, domainEvent.Customer), ct);
}

/// <summary>Fixed-tenant context for the tests (the outbox stamps this tenant on captured rows).</summary>
public sealed class TestTenantContext(TenantId? tenant) : ITenantContext
{
    public TenantId? CurrentTenantId => tenant;

    public bool IsSystemScope => tenant is null;
}
