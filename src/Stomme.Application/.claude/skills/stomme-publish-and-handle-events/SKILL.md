---
name: stomme-publish-and-handle-events
description: Use when one part of a Nova Globen Stomme app must react to something that happened in another - raising and handling a domain event, publishing an in-process notification, sending an integration event over the message bus to other modules or instances, subscribing to a published event, or making code react after an aggregate is saved.
---

# Publish and handle events

You will have an aggregate that raises a **domain event** when it is created, handlers that react to it in the same scope,
a **mediator notification** other code can listen to, and an **integration event** published on the message bus when the
bus is enabled, with an idempotent handler that runs for the right tenant and site. The example extends the Notes module
(the module the `stomme-create-module` skill builds), whose `Note` already raises `NoteCreatedEvent`. Read
[events and messaging](../../../docs/services/events-and-messaging.md) to choose between them; aggregates and repositories
are explained in [module persistence](../../../docs/persistence/module-persistence.md) and built by the
`stomme-add-aggregate` skill.

Choose the smallest mechanism that works:

- The reaction belongs to the same write (audit, a derived row, a notice): **domain event**.
- Other code in the same process should react without the publisher knowing it: **mediator notification**.
- Another instance or a separately deployed consumer must receive it, or the work must survive a restart: **integration
  event** on the bus (`Messaging:Enabled`, with a broker or the `ravendb` transport for more than one instance).
- Open pages must update live: a `SiteSignal<T>` and/or a SignalR hub (see the doc); not covered here.

## Steps

All files are in the Server project, in the module's folders.

1. **The domain event** - the aggregate raises it and the unit of work dispatches it. In the Notes module it is already
   there, in `Server/Modules/Notes/Domain/Note.cs`:

```csharp
    public sealed record NoteCreatedEvent(Guid NoteId, int SiteId, int AuthorUserId) : DomainEvent;
```

   raised at the end of `Note.Create`, after the note is complete:

```csharp
            note.RaiseDomainEvent(new NoteCreatedEvent(note.Id, note.SiteId, note.AuthorUserId));
```

   and handled by `NoteCreatedHandler : IDomainEventHandler<NoteCreatedEvent>`, registered in `NotesServerStartup`. The
   command handler stores the note and then calls `IUnitOfWork.SaveChangesAsync`, which runs every registered
   `IDomainEventHandler<NoteCreatedEvent>` in the request's scope. A domain event handler may have several siblings: each is
   one more registration.

2. **The notification, the integration event, and the handler that publishes them** -
   `Server/Modules/Notes/Application/NoteCreatedMessages.cs`. A second domain event handler turns "a note was stored" into
   both messages, so the command handler stays as it is. It runs after the write (repositories write eagerly), so a consumer
   never hears about a note that was not stored. The integration event carries the framework tenant id and site id, because
   the bus restores the tenant but not the site.

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Enums;
using Stomme.EventBus;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;
using LogLevel = Stomme.Shared.LogLevel;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>In-process: any code in this app may handle it; the publisher does not know who does.</summary>
    public sealed record NoteCreatedNotification(Guid NoteId, int SiteId, int AuthorUserId) : INotification;

    /// <summary>On the message bus. Carries the framework tenant id and site id: the bus restores the tenant, not the site.</summary>
    public sealed record NoteCreatedIntegrationEvent(int TenantId, int SiteId, Guid NoteId) : IntegrationEvent;

    /// <summary>
    /// A second handler of the domain event: it turns "a note was stored" into a notification and, when the bus is on, an
    /// integration event. It runs inside SaveChangesAsync, after the write, in the request's scope.
    /// </summary>
    public sealed class NoteCreatedSignal(IMediator mediator, SiteState siteState, IEnumerable<IEventBus> buses) : IDomainEventHandler<NoteCreatedEvent>
    {
        public async Task HandleAsync(NoteCreatedEvent domainEvent, CancellationToken ct = default)
        {
            await mediator.Publish(new NoteCreatedNotification(domainEvent.NoteId, domainEvent.SiteId, domainEvent.AuthorUserId), ct);

            // Empty when Messaging:Enabled is off - never inject IEventBus itself.
            if (buses.FirstOrDefault() is { } bus && siteState.Alias is { } alias)
            {
                // The host runs no outbox: a crash between the write and this line loses the message.
                await bus.PublishAsync(new NoteCreatedIntegrationEvent(alias.TenantId, domainEvent.SiteId, domainEvent.NoteId), ct);
            }
        }
    }

    /// <summary>Mediator notification handler: in-process, in the publisher's scope.</summary>
    public sealed class NoteCreatedNotificationHandler(ILogger<NoteCreatedNotificationHandler> logger) : INotificationHandler<NoteCreatedNotification>
    {
        public Task Handle(NoteCreatedNotification notification, CancellationToken ct)
        {
            // React here: forget a cache entry, update a read model, ...
            logger.LogInformation("Notification: note {NoteId} created on site {SiteId}.", notification.NoteId, notification.SiteId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The bus's handler: discovered by the bus, not registered. Delivery is at least once, so it must be idempotent. The
    /// tenant scope is restored from the message; the site is not, so set it from the event before anything site-scoped.
    /// </summary>
    public sealed class NoteCreatedIntegrationHandler(ITenantManager tenantManager, ILogManager log) : IIntegrationEventHandler<NoteCreatedIntegrationEvent>
    {
        public async Task HandleAsync(NoteCreatedIntegrationEvent @event, CancellationToken ct = default)
        {
            await tenantManager.SetAliasAsync(@event.TenantId, @event.SiteId);

            // Idempotent: check whether this NoteId was already processed before doing anything that must happen once.
            log.Log(@event.SiteId, LogLevel.Information, this, LogFunction.Other, "Note {NoteId} Received On The Bus", @event.NoteId);
        }
    }
}
```

3. **Register** - add these lines (and `using Stomme.Infrastructure.EventBus;` and
   `using Microsoft.Extensions.Configuration;`) to `NotesServerStartup.ConfigureServices` in
   `Server/Modules/Notes/Startup/NotesServerStartup.cs`, before the `INotesService` line that is registered last. The
   integration handler is **not** registered here; the bus discovers it.

```csharp
            // More reactions to a created note: a notification, and an integration event when the bus is on.
            services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedSignal>();
            services.AddScoped<INotificationHandler<NoteCreatedNotification>, NoteCreatedNotificationHandler>();

            // A PUBLISHED event reaches this host's handlers only with a subscription - and the subscription needs the
            // bus, so add it only while the bus is on (the host's configuration, override file included).
            if (HostConfiguration.Resolve(services)?.GetValue<bool>("Messaging:Enabled") == true)
            {
                services.AddBusSubscription<NoteCreatedIntegrationEvent>();
            }
```

## Verify

1. `dotnet build`, `cd Server`, `dotnet run`, sign in, add a note on the Notes page (or `POST api/Notes`).
2. The server console shows "Note {id} created on site 1." (the module's own domain event handler) and "Notification: note
   {id} created on site 1." (the notification handler) - once each.
3. With `Messaging:Enabled` false (the default): the app starts, adding a note works, and **Admin > Event Log** has no
   "Received On The Bus" entry. This proves the module does not depend on the bus.
4. Turn the bus on in the installation's `appsettings.override.json`: `"Messaging": { "Enabled": true }` (the in-memory
   transport), restart (the console logs `Bus "Rebus 1" started`), add a note. **Admin > Event Log** now shows "Note {id}
   Received On The Bus", on the site where the note was added. Remove the `AddBusSubscription` line and repeat: the note is
   saved and the entry never appears - nothing reports the lost message.
5. With two sites, each note's entry appears only on its own site's Event Log.
6. For several instances, repeat step 4 with `Messaging:Transport` set to `rabbitmq` (and a connection string) or `ravendb`,
   giving each instance its own `Messaging:InputQueue` if every instance must receive the event.

## Common mistakes

- **The app does not start, or the service cannot be resolved, while the bus is off.** Cause: a constructor takes
  `IEventBus`. Fix: take `IEnumerable<IEventBus>`.
- **Startup fails with the bus off after adding the subscription**, with "Unable to resolve service for type
  'Rebus.Bus.IBus' while attempting to activate 'Stomme.Infrastructure.EventBus.BusSubscriptionInitializer'". Cause:
  `AddBusSubscription` called unconditionally; its hosted service needs the bus. Fix: guard it with `Messaging:Enabled` as
  in step 3.
- **The domain event handler never runs.** Cause: `IUnitOfWork.SaveChangesAsync` not called after the repository write, or
  the handler not registered. Fix: both.
- **A handler's work happens twice, or is lost on an error.** Cause: the domain event handler calls `SaveChangesAsync`,
  creates a scope or expects a transaction. Fix: stage work and return; the write is already stored when the handler runs.
- **The bus handler never runs, and nothing is logged.** Causes: no `AddBusSubscription<T>()` for a published event (the
  publish succeeds and reaches no one); the bus is off; or the handler type is abstract or not in a `*.Stomme`
  assembly. Fix accordingly. `SendLocalAsync` needs no subscription.
- **The bus handler acts on the wrong site, or logs to `error.log`.** Cause: the site was not set. Fix: carry tenant and site
  ids in the event and call `SetAliasAsync` first.
- **Processing happens twice.** Cause: at-least-once delivery. Fix: make the handler idempotent (check before acting, use
  deterministic ids).
- **Another instance never receives the event.** Cause: the `inmemory` transport, or all instances sharing one input queue
  (they compete). Fix: a broker or `ravendb`, and one input queue per instance for broadcast.
- **Looking for the outbox.** Stomme contains outbox types but the host does not activate them; do not enable or depend
  on them.
