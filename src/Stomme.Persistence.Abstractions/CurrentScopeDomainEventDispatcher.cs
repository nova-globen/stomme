using System.Collections.Concurrent;
using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// The framework's <see cref="IDomainEventDispatcher"/>: resolves and invokes the registered
/// <see cref="IDomainEventHandler{TEvent}"/> handlers for each domain event <b>in the current DI scope</b>
/// — the same scope, and therefore the same unit of work, that raised the events (docs/decisions.md D40).
/// </summary>
/// <remarks>
/// <para>
/// CURRENT SCOPE IS THE DECISION, AND THE TYPE NAME IS THE DECISION. This class takes
/// <see cref="IServiceProvider"/>, not <c>IServiceScopeFactory</c>: there is no scope to create, so
/// new-scope dispatch is not a configuration mistake away — it is unrepresentable here. D9 puts domain
/// events *inside* one bounded context and writes the outbox row *inside the aggregate's transaction*;
/// dispatching in the current scope is that model. A child scope would put the handler outside the
/// aggregate's transaction, which is exactly what the outbox does deliberately, for integration events.
/// </para>
/// <para>
/// WHAT CURRENT SCOPE BUYS. The scoped tenant context flows into handlers with no re-propagation (get that
/// wrong and you get the silent wrong-tenant class this project already hit once), and it is the only option
/// under which the audit trail CAN be atomic with the change it audits — commit in scope A and audit in scope
/// B, and a failing B leaves a change with no audit record. Note "can": today it is not, because the seam has
/// no transaction and <c>IUnitOfWork.SaveChangesAsync</c> dispatches without saving. Current scope keeps the
/// option open; docs/decisions.md D41 is the step that takes it.
/// </para>
/// <para>
/// WHAT IT COSTS, AND THE CONTRACT THAT PAYS FOR IT. Handlers share the caller's unit of work, so a handler
/// that manages that unit of work disturbs its caller. The contract on
/// <see cref="IDomainEventHandler{TEvent}"/> — stage work, never manage it — is what makes current-scope
/// dispatch safe, and <c>DomainEventHandlerContractTests</c> enforces it at build time.
/// </para>
/// <para>
/// Registered <b>scoped</b> by every caller: dispatch is per unit of work, and its consumer
/// (<c>IUnitOfWork</c>) and its handlers are scoped. Resolution goes through the raw
/// <see cref="IServiceProvider"/> rather than the <c>GetServices</c> extension so this assembly keeps its
/// zero-package-dependency surface.
/// </para>
/// </remarks>
public sealed class CurrentScopeDomainEventDispatcher(IServiceProvider services) : IDomainEventDispatcher
{
    // Type-erased shims so the strongly-typed handler interface can be invoked over IDomainEvent. Cached:
    // the reflection is per event TYPE, not per event, and dispatch sits on the write path.
    private static readonly ConcurrentDictionary<Type, EventInvoker> Invokers = new();

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            var invoker = Invokers.GetOrAdd(domainEvent.GetType(), static type => new EventInvoker(type));
            await invoker.InvokeAsync(services, domainEvent, ct);
        }
    }

    /// <summary>Caches the reflection needed to fan one event type out to its handlers.</summary>
    private sealed class EventInvoker
    {
        private readonly Type _handlerCollectionType;
        private readonly Func<object, IDomainEvent, CancellationToken, Task> _handle;

        public EventInvoker(Type eventType)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
            _handlerCollectionType = typeof(IEnumerable<>).MakeGenericType(handlerType);

            var method = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;
            _handle = (handler, domainEvent, ct) => (Task)method.Invoke(handler, [domainEvent, ct])!;
        }

        public async Task InvokeAsync(IServiceProvider services, IDomainEvent domainEvent, CancellationToken ct)
        {
            // GetService(IEnumerable<IDomainEventHandler<TEvent>>) is what the GetServices extension does;
            // calling it directly keeps Microsoft.Extensions.DependencyInjection.Abstractions off this
            // assembly's dependency list. No registered handlers resolves to an empty sequence, not null.
            if (services.GetService(_handlerCollectionType) is not IEnumerable<object?> handlers)
            {
                return;
            }

            foreach (var handler in handlers)
            {
                if (handler is null)
                {
                    continue;
                }

                await _handle(handler, domainEvent, ct);
            }
        }
    }
}
