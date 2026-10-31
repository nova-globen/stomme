# Events and messaging

Nova Globen Stomme has four ways to say "this happened", each for a different distance: **domain events** (inside one module, after an
aggregate is written), **mediator notifications** (in-process fan-out to any handler), the **message bus** (integration events
between modules and between instances, off by default), and **real-time push** (SignalR hubs and an in-process site signal,
to the pages people have open). Framework entity changes also raise **sync events**, described in
[caching and sync](caching-and-sync.md).

| Mechanism | Reaches | Durable | Switch |
|---|---|---|---|
| Domain event (`IDomainEventHandler<T>`) | handlers in the same DI scope, same process | no | always on |
| Mediator notification (`INotificationHandler<T>`) | handlers in the same process | no | always on |
| Integration event (`IEventBus`, `IIntegrationEventHandler<T>`) | handlers in this and (with a broker transport) other instances | depends on transport | `Messaging:Enabled` |
| `SiteSignal<T>` | Interactive Server pages rendered by this instance | no | always on |
| SignalR hub | browsers (WebAssembly pages) connected to this instance | no | `Transport:SignalR:Enabled` |

## How it works

### Domain events

- An aggregate (derived from `Stomme.SharedKernel.Domain.AggregateRoot<TId>`) calls the protected
  `RaiseDomainEvent(IDomainEvent)`. An event is usually a record derived from `Stomme.SharedKernel.Domain.DomainEvent`,
  which sets `OccurredOn`.
- The storage-blind repositories write **eagerly**: every `AddAsync` / `UpdateAsync` / `DeleteAsync` commits at once and
  enrols the aggregate in the scope's tracker.
- `Stomme.SharedKernel.Domain.IUnitOfWork.SaveChangesAsync()` saves nothing itself. It takes the aggregates written in
  this scope, clears their events, and dispatches the events to every registered
  `Stomme.SharedKernel.Domain.IDomainEventHandler<TEvent>` **in the current DI scope** (same tenant, same user). If you never
  call it, no handler runs.
- Handlers are registered explicitly in your `IServerStartup`:
  `services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedHandler>();`. Several handlers of one event are several registrations; all run.
- **The handler contract: stage work, never manage it.** A handler adds or updates through injected repositories and returns.
  It must not create a DI scope, call `SaveChangesAsync` on anything, commit or dispose the ambient session. Because
  repositories write eagerly, the aggregate is already stored when a handler runs, and a handler that throws does not undo it:
  the exception reaches the caller of `SaveChangesAsync` after the data is written.

How aggregates and commands are written is in [application layer](../modules/application-layer.md) and
[module persistence](../persistence/module-persistence.md).

### Mediator notifications

`Stomme.Mediator.IMediator.Publish(notification)` calls every registered
`Stomme.Mediator.INotificationHandler<TNotification>` (`Task Handle(TNotification, CancellationToken)`) for a
`Stomme.Mediator.INotification` in the current scope. Register handlers explicitly:
`services.AddScoped<INotificationHandler<NoteCreatedNotification>, NoteCreatedNotificationHandler>();`. Use a notification to let another part of
the app react without the publisher knowing it; use a domain event when the reaction belongs to the aggregate's write.

### The message bus (integration events)

- **Off unless `Messaging:Enabled` is `true`.** Off, nothing is registered: there is **no** `IEventBus` in the container. A class
  that takes `IEventBus` in its constructor then fails to resolve. Take `IEnumerable<IEventBus>` and use `FirstOrDefault()`,
  or `IServiceProvider.GetService<IEventBus>()`, and do something sensible when it is absent.
- `Stomme.EventBus.IEventBus` (Rebus underneath): `PublishAsync` (to every subscriber), `SendAsync` (to a named queue or
  the configured route), `SendLocalAsync` (to this host's own input queue - point-to-point, no subscription needed) and
  `DeferAsync` (deliver later; support depends on the transport - not verified here).
- Handlers implement `Stomme.EventBus.IIntegrationEventHandler<TEvent>` (`Task HandleAsync(TEvent, CancellationToken)`).
  They are **discovered** in every `Stomme`-named assembly when the bus is enabled; do not register them yourself. An event
  is any class; `Stomme.EventBus.IntegrationEvent` is an optional base record with `EventId`, `OccurredOn`,
  `CorrelationId`.
- **A published event reaches this host's handlers only if the host subscribes to it.** Call
  `services.AddBusSubscription<TEvent>()` (namespace `Stomme.Infrastructure.EventBus`) in your `IServerStartup`. With
  `Messaging:Enabled` off the subscription does nothing (since 10.79.0; before, the host did not start without the bus).
  `SendLocalAsync` needs no subscription.
- **Delivery is at least once.** A handler can see the same message twice: make it idempotent. After
  `Messaging:MaxDeliveryAttempts` failed attempts the message moves to the error (dead-letter) queue.
- **Tenant.** The sender's tenant travels in a message header and the handler runs inside that tenant's scope. The **site**
  does not travel: put the framework tenant id and site id in the event and call
  `ITenantManager.SetAliasAsync(tenantId, siteId)` in the handler before using anything site-bound.
- **If the broker is unreachable**, sends and publishes are buffered in memory (`Messaging:BufferCapacity`, default 1000)
  and replayed when it returns; when the buffer is full the call throws `InvalidOperationException`. The buffer is lost on
  restart.
- **Transports** (`Messaging:Transport`): `inmemory` (default) - one process only, nothing survives a restart, no other
  instance sees it; `rabbitmq` and `azureservicebus` - a broker, connection string required; `ravendb` - durable queue
  documents in the installation's RavenDB store (RavenDB installs only), and the only transport with a dead-letter view.
  **Only a broker or `ravendb` carries messages between instances.** Instances that share one `Messaging:InputQueue`
  compete for messages (each message is handled once); give each instance its own input queue for every instance to
  receive a published event.
- **Dead letters.** With the `ravendb` transport, the Host role can list, retry and discard dead letters through
  `api/DeadLetter` (`GET`, `GET status`, `POST retry`, `POST discard`). The **Dead Letter Sweep Job** discards dead letters
  older than `Operations:DeadLetterSweep:RetentionDays`; its Job row starts disabled **and**
  `Operations:DeadLetterSweep:Enabled` must be true.
- **The transactional outbox is not available.** Stomme contains outbox types but the host does not activate them. Publish
  after `SaveChangesAsync` and accept that a crash between the write and the publish loses the message, or design the
  consumer to reconcile.

### Real-time push

- **`Stomme.Transport.Realtime.SiteSignal<TMessage>`** (in `Stomme.Shared`): an abstract in-process fan-out. Derive from
  it, expose `Subscribe` / `Unsubscribe` / `Raise(siteKey, message)`, and register it as a singleton. An Interactive Server
  page subscribes when it initialises and unsubscribes when disposed; each subscriber is called in the execution context it
  subscribed in (so its work runs as its own user, not as the user who raised the message), and one that throws does not
  affect the others or the raiser. A server circuit has no browser cookie to open a hub with, so this is how server-rendered
  pages are told.
- **SignalR hubs**, only while `Transport:SignalR:Enabled` is true. The framework maps its own hubs under
  `/hubs/v1/...` (`Stomme.Transport.Realtime.HubRoutes`). A module adds a hub by implementing
  `Stomme.Transport.IModuleEndpointContributor` (public parameterless constructor, discovered by scan) and mapping it in
  `MapEndpoints(endpoints, transports)` **only when `transports.SignalR.Enabled`**: SignalR services are not registered
  otherwise and an unguarded `MapHub` fails at startup. Anything that needs `IHubContext<THub>` must be resolved only when
  the transport is on (check the singleton `Stomme.Transport.TransportOptions`). Hub connections authenticate with the
  site's cookie; `Context.User.SiteKey()` (`Stomme.Extensions`) gives `"{tenantId}:{siteId}"` for grouping by site.
- A WebAssembly page connects with `Microsoft.AspNetCore.SignalR.Client`'s `HubConnectionBuilder` (the `Stomme.Client`
  package brings it) and should fall back to polling when the connection fails, because the transport may be off.
- **Map a hub under `/hubs/...` and build its URL with the site's path** (since 10.75.0):
  `NavigationManager.ToAbsoluteUri(Utilities.SiteUrl(PageState.Alias.Path, "/hubs/notes"))`. On a site in a folder
  (`example.com/shop`) that is `/shop/hubs/notes`; the host strips the folder from a `hubs/` URL, as from `api/`, `pages/`
  and `files/`, and resolves the folder's site, so the hub sees that site's user and tenant. A root site gets
  `/hubs/notes` as before. Do not use a relative URL: the page's `<base href>` is the host's root.
- **Push is per instance.** A hub or signal on instance A does not reach a browser connected to instance B. The framework's
  own live features (collaboration notices, dashboard refreshes, pipeline event triggers) publish an integration event when
  the bus is on and deliver in-process when it is off - and with the bus on they currently reach no handler (see Rules and
  pitfalls).

## Using it from an app

The [publish-and-handle-events skill](../../.claude/skills/stomme-publish-and-handle-events/SKILL.md) builds a domain
event, its handler, a mediator notification and an integration event end to end. The core of publishing safely:

```csharp
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.EventBus;

namespace Stomme.Application.Modules.Notes.Application
{
    public sealed record NoteCreatedIntegrationEvent(int TenantId, int SiteId, System.Guid NoteId) : IntegrationEvent;

    public sealed class NoteAnnouncer
    {
        private readonly IEventBus? _bus;

        // IEnumerable: with Messaging:Enabled off there is no IEventBus, and a direct dependency would not resolve.
        public NoteAnnouncer(IEnumerable<IEventBus> buses)
        {
            _bus = buses.FirstOrDefault();
        }

        public async Task AnnounceAsync(NoteCreatedIntegrationEvent created, CancellationToken ct)
        {
            if (_bus is null)
            {
                return; // the bus is off: nobody outside this process is listening
            }

            await _bus.PublishAsync(created, ct);
        }
    }
}
```

A SignalR hub of your own, mapped only when the transport is on:

```csharp
#nullable enable
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Stomme.Application.Modules.Notes.Realtime
{
    [Authorize]
    public sealed class NotesHub : Hub
    {
        public const string Route = "/hubs/v1/notes";

        public static string GroupFor(int tenantId, int siteId) => "notes:" + tenantId + ":" + siteId;

        public override async Task OnConnectedAsync()
        {
            var siteKey = Context.User?.SiteKey();
            if (!string.IsNullOrEmpty(siteKey))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "notes:" + siteKey);
            }

            await base.OnConnectedAsync();
        }
    }

    public sealed class NotesEndpoints : IModuleEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
        {
            if (transports.SignalR.Enabled)
            {
                endpoints.MapHub<NotesHub>(NotesHub.Route).RequireAuthorization();
            }
        }
    }
}
```

`RequireAuthorization()` with no policy requires a signed-in user. To require a permission key instead, register a policy in
your startup with `TransportServiceCollectionExtensions.ConfigureHubPolicy(policy, NotesPermissions.View)`
(namespace `Microsoft.Extensions.DependencyInjection`) and pass its name; see
[identity and permissions](../security/identity-and-permissions.md).

## Configuration

Read from the host's configuration (the override file included): the `Messaging:*` keys once at startup, the two
`Operations:DeadLetterSweep:*` keys on every run of the sweep, so a change to them needs no restart.

| Key | Default | Effect |
|---|---|---|
| `Messaging:Enabled` | `false` | Registers the bus, `IEventBus`, handler discovery. Off: none of it exists. |
| `Messaging:Transport` | `inmemory` | `inmemory`, `rabbitmq`, `azureservicebus` or `ravendb`. |
| `Messaging:ConnectionString` | empty | Broker connection. When empty, the connection string named after the transport (`ConnectionStrings:rabbitmq`, ...) is used. Unused by `ravendb`. |
| `Messaging:InputQueue` | `stomme` | This instance's queue. Instances sharing a name compete for messages. |
| `Messaging:ErrorQueue` | `{InputQueue}.error` | Where failed messages go. |
| `Messaging:MaxDeliveryAttempts` | 3 | Attempts before dead-lettering. |
| `Messaging:NumberOfWorkers` | processor count (at least 2) | Rebus workers. |
| `Messaging:MaxParallelism` | the worker count | Messages in flight. |
| `Messaging:BufferCapacity` | 1000 | Messages held in memory while the broker is unreachable. |
| `Operations:DeadLetterSweep:Enabled` | `false` | Lets the Dead Letter Sweep Job discard (its Job row must be enabled too). |
| `Operations:DeadLetterSweep:RetentionDays` | 30 | Age at which the sweep discards a dead letter. |
| `Transport:SignalR:Enabled` | `false` | Registers SignalR, maps the framework hubs and lets contributors map theirs. |
| `Transport:Grpc:Enabled` | `false` | Registers gRPC and gRPC-Web for contributors that map gRPC services. |

A module that cannot work without a transport can say so in its `ModuleDefinition.Transports` (`"SignalR"`, `"Grpc"`); the
host then logs an error naming the module and the missing switch at startup.

## Rules and pitfalls

- **Never take `IEventBus` as a required dependency.** With the bus off the container cannot build your class; with the host's
  container validation that can stop startup.
- **Publish needs a subscription; send-local does not.** A published event with no `AddBusSubscription<TEvent>()` in the
  receiving host reaches no handler and raises no error (measured on the `inmemory` transport: the publish returns, the
  handler never runs, nothing is logged).
- **The framework subscribes its own published events.** With `Messaging:Enabled` on, Collaboration's notices,
  Dashboards' dataset changes and Pipelines' event triggers travel on the bus, and Stomme subscribes their topics
  itself (since 10.73.2; before it, they reached no handler and those live features went silent with the bus on). Your
  own published events still need your `AddBusSubscription<TEvent>()`.
- **`inmemory` is not "between instances".** It is a single-process queue: messages disappear on restart and no other
  instance sees them.
- **Handlers must be idempotent** (at-least-once delivery) and must set the site themselves.
- **Domain event handlers do not get their own transaction.** The change is already stored when they run. Work that must
  survive independently goes on the bus.
- **No outbox.** Do not look for one or enable one; it is not supported.
- **Guard every `MapHub` with `transports.SignalR.Enabled`** and resolve `IHubContext<T>` only when the transport is on.
- **A hub outside `/hubs/` is root-only.** Mapped elsewhere, a site in a folder reaches it only at the host's root, with the
  ROOT site resolved - another site's users and tenant. Map under `/hubs/` and connect with `Utilities.SiteUrl`.
- **`hubs` is reserved** like `api`, `pages` and `files`: no site and no top-level page may be named `hubs`.
- **Push and signals are per instance.** Several instances need the bus (with a broker) to fan a change out to every instance.
- `ISyncManager` is not a message bus: it never leaves the process.

## Related

- [Caching and sync](caching-and-sync.md) - sync events and `IEventSubscriber`
- [Application layer](../modules/application-layer.md) - aggregates, commands, the mediator
- [Module persistence](../persistence/module-persistence.md) - repositories and the unit of work
- [Locks and collaboration](locks-and-collaboration.md) - the live editing features built on these
- [Notifications](notifications.md) - telling people (email and in-app) rather than code
- Skill: [publish and handle events](../../.claude/skills/stomme-publish-and-handle-events/SKILL.md)
