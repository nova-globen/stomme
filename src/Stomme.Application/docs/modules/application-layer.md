# The application layer

A module's server logic is a set of commands and queries sent through Nova Globen Stomme's mediator, each handled by one handler
class. The mediator runs every request through a pipeline that authorizes, logs, times and validates it before the
handler sees it. Aggregates raise domain events, which handlers in the same module react to; value objects keep a
rule about a value in one place.

## How it works

### The mediator

Namespace `Stomme.Mediator` (package `Stomme.Mediator`, part of `Stomme.Server`'s closure):

| Type | Role |
|---|---|
| `IMediator` | `Send(request, ct)`, `Publish(notification, ct)`, `CreateStream(request, ct)`. Scoped; inject it. |
| `ICommand<TResponse>`, `ICommand` | A request that changes something. `ICommand` returns `Unit`. |
| `IQuery<TResponse>` | A request that reads. |
| `ICommandHandler<TCommand, TResponse>`, `IQueryHandler<TQuery, TResponse>` | The handler: one method, `Task<TResponse> Handle(TRequest request, CancellationToken ct)`. Both derive from `IRequestHandler<TRequest, TResponse>`. |
| `INotification`, `INotificationHandler<T>` | In-process fan-out: every registered handler runs, in turn. |
| `IStreamRequest<T>`, `IStreamRequestHandler<TRequest, T>` | A request answered as `IAsyncEnumerable<T>`. |
| `IPipelineBehavior<TRequest, TResponse>` | A step wrapped around every handler. |
| `Unit` | The "no value" response: return `Unit.Value`. |

`Send` resolves `IRequestHandler<TRequest, TResponse>` from the current scope and calls it through every registered
`IPipelineBehavior`. The host registers the behaviors once; a module never registers them again. In order, outermost
first:

1. **Authorization** (`Stomme.Authorization.AuthorizationBehavior`) - checks every `[Requires]` on the request type
   and throws `Stomme.Security.Abstractions.AccessDeniedException` when the caller does not satisfy it.
2. **Exception** (`Stomme.Mediator.Behaviors.ExceptionBehavior`) - logs an unexpected exception with the request's
   name and rethrows it. It does not log a `FluentValidation.ValidationException` or a cancellation the caller asked for.
3. **Logging** - logs "[Mediator] Handling {RequestName}" and its completion.
4. **Performance** - logs a warning for a request that takes longer than 500 ms.
5. **Validation** (`Stomme.Mediator.Behaviors.ValidationBehavior`) - runs every `FluentValidation.IValidator<TRequest>`
   registered for the request and throws `FluentValidation.ValidationException` with all the failures.
6. The handler.

### Authorization on a request

`[Requires]` (`Stomme.Security.Abstractions.RequiresAttribute`) on a request type declares what the caller needs:
`[Requires("notes:edit")]` for a permission key, `[Requires(Feature = "notes.sharing")]` for a feature flag, or both
(`Feature` and `Permission` together are AND; several attributes are all required). The permission is decided by the
site's permission rows for the key - see [identity and permissions](../security/identity-and-permissions.md) for how a
module declares its keys and which roles get them.

The decision needs an HTTP request: with no `HttpContext` in scope (a background job), any `[Requires]` is denied. A job
calls domain code or a repository directly instead of sending a gated request - see [jobs](../services/jobs.md).

A controller turns `AccessDeniedException` into a 403 response (the host registers that filter for every controller).

### Domain events

An aggregate records what happened by calling `RaiseDomainEvent(...)` (protected, on `Entity<TId>`) with a record that
derives from `DomainEvent` (`Stomme.SharedKernel.Domain`; it stamps `OccurredOn`). Then:

1. The repository write (`AddAsync`, `UpdateAsync`, `DeleteAsync`) saves the aggregate and enrols it in the scope.
2. `IUnitOfWork.SaveChangesAsync` collects the events of every aggregate written in the scope, clears them from the
   aggregates, and calls every `IDomainEventHandler<TEvent>` registered for each event, in the caller's DI scope.
3. Each handler's `HandleAsync(TEvent domainEvent, CancellationToken ct)` runs before `SaveChangesAsync` returns.

The write is already saved when the handlers run; a handler that fails makes `SaveChangesAsync` throw, but does not undo
the write.

Domain events stay inside the app's process. To tell another part of the system, or another instance, use an
integration event on the message bus - see [events and messaging](../services/events-and-messaging.md).

### Value objects

`ValueObject` (`Stomme.SharedKernel.Domain`) is the base for an immutable value compared by its components
(`GetEqualityComponents`). To store one as a property of an aggregate, it must also implement `IStringValueObject`
(`string Value { get; }`) and expose `public static T Create(string value)`; every store keeps it as one string and
rebuilds it through `Create`. A `public const int MaxLength` on it sets the relational column's length. Apart from
`TenantId`, which the stores map themselves, a value object that is not an `IStringValueObject` cannot be an aggregate
property: the relational store refuses it when its model is built (see
[module persistence](../persistence/module-persistence.md)).

## Using it from an app

The examples belong to a module called Notes; the complete steps are in the
[stomme-add-command-or-query](../../.claude/skills/stomme-add-command-or-query/SKILL.md) skill.

### A command, its validator and its handler

From `Server/Modules/Notes/Application/NoteRequests.cs` of the reference Notes module (the whole file, with its queries,
the edit and delete commands and the `NotesScope` helper used below, is in
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md)):

```csharp
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using FluentValidation;

namespace Stomme.Application.Modules.Notes.Application
{
    [Requires(NotesPermissions.Edit)]
    public sealed record CreateNoteCommand(string Title, string Body) : ICommand<NoteModel>;

    public sealed class CreateNoteCommandValidator : AbstractValidator<CreateNoteCommand>
    {
        public CreateNoteCommandValidator()
        {
            RuleFor(command => command.Title)
                .NotEmpty().WithMessage("Give the note a title.")
                .MaximumLength(Note.MaxTitleLength);
            RuleFor(command => command.Body)
                .MaximumLength(Note.MaxBodyLength);
        }
    }

    public sealed class CreateNoteCommandHandler(
        INoteRepository notes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        SiteState siteState,
        ICurrentUser currentUser,
        TimeProvider clock) : ICommandHandler<CreateNoteCommand, NoteModel>
    {
        public async Task<NoteModel> Handle(CreateNoteCommand request, CancellationToken ct)
        {
            var (tenantId, siteId) = NotesScope.Resolve(tenantContext, siteState);
            var author = currentUser.UserId ?? throw new AccessDeniedException("A note is written by a signed-in user.");
            if (await notes.FindByTitleAsync(siteId, request.Title, ct) is not null)
            {
                throw new NoteConflictException($"A note titled '{request.Title.Trim()}' already exists on this site.");
            }

            var note = Note.Create(Guid.NewGuid(), tenantId, siteId, author, request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
            await notes.AddAsync(note, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return NotesScope.ToModel(note);
        }
    }
}
```

`NoteModel` and `NotesPermissions` live in the module's `Shared` project (see
[API and client services](api-and-client-services.md)); `Note` is the aggregate from
[module persistence](../persistence/module-persistence.md) and `INoteRepository` its repository.

### Registration

In the module's `IServerStartup.ConfigureServices` (the reference module's is `NotesServerStartup`), one line per handler
and validator:

```csharp
services.TryAddSingleton(TimeProvider.System);
services.AddScoped<IRequestHandler<CreateNoteCommand, NoteModel>, CreateNoteCommandHandler>();
services.AddScoped<IValidator<CreateNoteCommand>, CreateNoteCommandValidator>();
```

Register the handler under `IRequestHandler<,>` - that is the type the mediator resolves. `MediatorServiceExtensions`
also offers `services.AddMediator<TMarker>()`, which scans `TMarker`'s assembly and registers every public handler it
finds. It is additive: every call registers every handler again. All of an app's modules share the app's one `Server`
assembly, so do not call it from a module's startup; the skills register every handler explicitly. Validators are never
registered by the scan.

### Sending

```csharp
var created = await mediator.Send(new CreateNoteCommand(title, body), ct);
```

### A domain-event handler

```csharp
#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>Runs inside IUnitOfWork.SaveChangesAsync after a note is created. Stages work; never saves.</summary>
    public sealed class NoteCreatedHandler(ILogger<NoteCreatedHandler> logger) : IDomainEventHandler<NoteCreatedEvent>
    {
        public Task HandleAsync(NoteCreatedEvent domainEvent, CancellationToken ct = default)
        {
            logger.LogInformation("Note {NoteId} created on site {SiteId}.", domainEvent.NoteId, domainEvent.SiteId);
            return Task.CompletedTask;
        }
    }
}
```

Registered explicitly (nothing scans for domain-event handlers in an app):

```csharp
services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedHandler>();
```

### A notification through the mediator

```csharp
public sealed record NoteSharedNotification(Guid NoteId) : INotification;

// after the write:
await mediator.Publish(new NoteSharedNotification(note.Id), ct);

// registration:
services.AddScoped<INotificationHandler<NoteSharedNotification>, NoteSharedNotificationHandler>();
```

Use a domain event for a reaction to a change in an aggregate; use a mediator notification when there is no aggregate
involved. Both run in-process, in the caller's scope.

### A string value object

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes.Domain
{
    public sealed class NoteTitle : ValueObject, IStringValueObject
    {
        public const int MaxLength = 120;

        private NoteTitle(string value) => Value = value;

        public string Value { get; }

        public static NoteTitle Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"A title is 1 to {MaxLength} characters.", nameof(value));
            }

            return new NoteTitle(trimmed);
        }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }
}
```

The reference Notes module keeps its title a plain `string` with a normalised `TitleKey`. With a value object instead,
the aggregate declares `public NoteTitle Title { get; private set; }` and the wire model carries `Title.Value`, a plain
string. (This variant was not driven.)

## Rules and pitfalls

- **Handlers and requests must be `public`.** The mediator calls the handler through `dynamic`; an `internal` handler
  or request type fails at run time, not at build time.
- **Put `[Requires]` on every request that is not public to everyone.** It is the one check that applies whether the
  request comes from the API, from a server-rendered page (which calls the mediator directly, not the controller), from
  an MCP tool or from another handler. A request with no `[Requires]` is open to anyone who can reach it.
- **Never register the pipeline behaviors yourself.** The host has registered them in the order above; a second
  registration runs each behavior twice and can put a step outside authorization.
- **One handler per request type.** If two are registered, the mediator uses the last registration; the other is dead
  code.
- **A validator throws, it does not return.** `ValidationBehavior` throws `FluentValidation.ValidationException`.
  Nothing in the host turns it into a 400: a controller must catch it (see
  [API and client services](api-and-client-services.md)), otherwise the caller gets a 500.
- **Validate input in the validator, enforce invariants in the aggregate.** The validator gives the user a readable
  message per field; the aggregate's factory and methods still refuse an invalid state, because not every caller goes
  through the mediator.
- **A domain-event handler stages work; it never manages the unit of work.** It runs inside the caller's operation: do
  not call `IUnitOfWork.SaveChangesAsync` from it, do not create a DI scope (`CreateScope`) - the new scope would have a
  different tenant context and session - and do not dispose anything injected. Add or update aggregates through the
  injected repository and return.
- **Call `IUnitOfWork.SaveChangesAsync` after the writes of one operation.** Without it, no domain-event handler runs
  for that operation.
- **Stamp the tenant and the site in the handler, from the scope.** `ITenantContext.CurrentTenantId` and
  `SiteState.Alias.SiteId`; refuse when either is missing. Never take them from the request body.
- **Throw meaningful exception types and map them at the API.** A convention that maps cleanly to HTTP: `ArgumentException` for an
  invalid value (400), `KeyNotFoundException` for an unknown or other-site id (404), `InvalidOperationException` for a
  rule that refuses the change (400 or 409), `ConcurrencyConflictException` for a stale write (409).
- **Async all the way.** No `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` in a handler; it deadlocks under real
  databases.

## Related

- [Module persistence](../persistence/module-persistence.md) - aggregates, repositories, the unit of work.
- [API and client services](api-and-client-services.md) - exposing a command or query over HTTP.
- [Identity and permissions](../security/identity-and-permissions.md) - permission keys and `[Requires]`.
- [Events and messaging](../services/events-and-messaging.md) - integration events between contexts and instances.
- [Jobs](../services/jobs.md) - running domain code without a request.
- Skills: [stomme-add-command-or-query](../../.claude/skills/stomme-add-command-or-query/SKILL.md),
  [stomme-add-aggregate](../../.claude/skills/stomme-add-aggregate/SKILL.md),
  [stomme-add-api-endpoint](../../.claude/skills/stomme-add-api-endpoint/SKILL.md).
