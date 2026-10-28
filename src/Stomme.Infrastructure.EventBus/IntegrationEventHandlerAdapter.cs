using Stomme.EventBus;
using Rebus.Handlers;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Bridges Rebus' <see cref="IHandleMessages{TMessage}"/> to the platform's transport-agnostic
/// <see cref="IIntegrationEventHandler{TEvent}"/>. Modules depend only on the platform abstraction;
/// this adapter fans an incoming message out to every registered handler for its type. Registered
/// only for event types that actually have a handler, so Rebus never treats an unhandled message as
/// handled.
/// </summary>
internal sealed class IntegrationEventHandlerAdapter<TEvent>(IEnumerable<IIntegrationEventHandler<TEvent>> handlers)
    : IHandleMessages<TEvent>
    where TEvent : class
{
    public async Task Handle(TEvent message)
    {
        foreach (var handler in handlers)
        {
            await handler.HandleAsync(message).ConfigureAwait(false);
        }
    }
}
