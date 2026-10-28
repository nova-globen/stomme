---
name: stomme-add-command-or-query
description: Use when adding business logic, a use case, an operation, a command, a query, a handler, a validator, a domain-event handler or a notification to a module in a Nova Globen Stomme app - anything that reads or changes a module's data through the mediator (IMediator, ICommand, IQuery, [Requires], FluentValidation).
---

# Add a command or a query

You will have a command (or query) with its authorization requirement, its validator and its handler, registered so
that the mediator runs it through Stomme's pipeline (authorization, logging, timing, validation), plus the server-side
service method that pages call. How it works: [the application layer](../../../docs/modules/application-layer.md).
Storage: [module persistence](../../../docs/persistence/module-persistence.md).

The steps continue the reference **Notes** module of [stomme-create-module](../stomme-create-module/SKILL.md) and
[stomme-add-aggregate](../stomme-add-aggregate/SKILL.md): the aggregate `Note` (title, body, author) and
`INoteRepository` exist. The code below is that module's, unchanged. Replace the names with yours.

## Steps

1. **Name the permission keys** the requests need, once, in the `Shared` project:
   `Shared/Modules/Notes/NotesPermissions.cs`.

   ```csharp
{{FILE:Shared/Modules/Notes/NotesPermissions.cs|3}}
   ```

   A key with no permission rows on a site refuses everyone, administrators included. The module's `ModuleInfo` must
   declare the keys with `PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations)` - see
   [identity and permissions](../../../docs/security/identity-and-permissions.md) and
   [stomme-secure-a-module](../stomme-secure-a-module/SKILL.md). The resource part must not be one of Stomme's
   entity names (`Site`, `Page`, `Module`, `User`, ...).

2. **Write the wire models** - what the handlers return and what the API receives - in
   `Shared/Modules/Notes/Models/NoteModels.cs`. Never return the aggregate itself.

   ```csharp
{{FILE:Shared/Modules/Notes/Models/NoteModels.cs|3}}
   ```

3. **Write the requests and handlers** in `Server/Modules/Notes/Application/NoteRequests.cs`. Every type the mediator
   touches is `public`. Every request carries `[Requires]`.

   ```csharp
{{FILE:Server/Modules/Notes/Application/NoteRequests.cs|3}}
   ```

   Exceptions are chosen for the API to map: `ArgumentException` (from the aggregate) for an invalid value, `KeyNotFoundException`
   for an unknown or other-site id, `NoteConflictException` for a clash, `FluentValidation.ValidationException` (from the
   validator) for invalid input, `Stomme.Security.Abstractions.AccessDeniedException` (from `[Requires]`, or thrown by
   the per-row check) for a caller who may not, `Stomme.Persistence.Abstractions.ConcurrencyConflictException` for a
   stale write.

4. **Register every handler and validator** in the module's `NotesServerStartup.ConfigureServices`
   (`Server/Modules/Notes/Startup/NotesServerStartup.cs`), with `#nullable enable` at the top of the file,
   `using System;`, `using System.Collections.Generic;`, `using Stomme.Mediator;`, `using FluentValidation;` and the
   module's `Application` and `Models` namespaces:

   ```csharp
   services.TryAddSingleton(TimeProvider.System);
   services.AddScoped<IRequestHandler<ListNotesQuery, IReadOnlyList<NoteModel>>, ListNotesQueryHandler>();
   services.AddScoped<IRequestHandler<GetNoteQuery, NoteModel?>, GetNoteQueryHandler>();
   services.AddScoped<IRequestHandler<CreateNoteCommand, NoteModel>, CreateNoteCommandHandler>();
   services.AddScoped<IRequestHandler<EditNoteCommand, NoteModel>, EditNoteCommandHandler>();
   services.AddScoped<IRequestHandler<DeleteNoteCommand, Unit>, DeleteNoteCommandHandler>();
   services.AddScoped<IValidator<CreateNoteCommand>, CreateNoteCommandValidator>();
   services.AddScoped<IValidator<EditNoteCommand>, EditNoteCommandValidator>();
   ```

   (`TryAddSingleton` needs `using Microsoft.Extensions.DependencyInjection.Extensions;`.) The service type is
   `IRequestHandler<TRequest, TResponse>`, with exactly the response type the request declares (`NoteModel?` for
   `GetNoteQuery`; `Unit` for a command declared as plain `ICommand`). Do not call `AddMediatorBehavior`, and do not call
   `services.AddMediator(...)` in a module: the host has composed the mediator already, every module of the app lives in
   the one `Server` assembly, and each call registers every handler in it again. The complete startup is in
   [stomme-create-module](../stomme-create-module/SKILL.md).

5. **Expose the requests to pages** through the module's service. In `Shared/Modules/Notes/Services/INotesService.cs`:

   ```csharp
{{FILE:Shared/Modules/Notes/Services/INotesService.cs|3}}
   ```

   and its server implementation, `Server/Modules/Notes/Services/ServerNotesService.cs`:

   ```csharp
{{FILE:Server/Modules/Notes/Services/ServerNotesService.cs|3}}
   ```

   registered last in the same `ConfigureServices`: `services.AddTransient<INotesService, ServerNotesService>();`. On an
   Interactive Server site the page calls this service and the request never goes over HTTP; `[Requires]` still applies:
   the guard reads the signed-in user from the HTTP context of the circuit's connection (driven: the host and a
   Registered User get what their keys allow). The browser's HTTP implementation and the controller come from
   [stomme-add-api-endpoint](../stomme-add-api-endpoint/SKILL.md); a WebAssembly site needs them.

6. **React to a domain event** (optional). The aggregate raises `NoteCreatedEvent`; a handler in
   `Server/Modules/Notes/Application/NoteCreatedHandler.cs` runs inside `SaveChangesAsync`:

   ```csharp
{{FILE:Server/Modules/Notes/Application/NoteCreatedHandler.cs|3}}
   ```

   registered with `services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedHandler>();` (`using
   Stomme.SharedKernel.Domain;`). Nothing scans for these: an unregistered handler never runs. A handler stages work
   through injected repositories; it never calls `SaveChangesAsync`, never creates a scope and never disposes what it was
   given.

## Verify

1. `dotnet build`: no errors, no new warnings.
2. `cd Server`, `dotnet run`. In Development the host validates its container at start: if a handler's dependency is
   missing, it stops with an error naming it.
3. Drive each request through the module's page (or the API from
   [stomme-add-api-endpoint](../stomme-add-api-endpoint/SKILL.md)), signed in as a user who holds the keys:
   - Create a note: it appears in the list; the console shows "[Mediator] Handling CreateNoteCommand" and, with step 6,
     "Note ... created on site 1."
   - Create one with an empty title over the API: refused with 400 and "Give the note a title.", nothing stored.
   - Create a second note with the same title in another letter case: refused as a conflict (409).
   - Edit and delete: the list follows.
4. Signed out: the list and every change are refused (403), and nothing changes. As a Registered User, changing another
   user's note is refused (403) with "Only the note's author or a notes manager may change it."
5. On a second site of the installation, the first site's notes do not exist: the list is empty and an id copied from the
   first site answers not found.
6. Check that no request is slower than half a second: the pipeline logs a warning for one that is.

Expect one `fail:` line from `Stomme.Mediator.Behaviors.ExceptionBehavior` in the console for each refused change in
steps 3 and 4 (a conflict, a per-row denial): the mediator logs every exception a handler throws, including the ones that
are a correct answer. A validation failure is not logged there.

## Common mistakes

- **"No handler registered for CreateNoteCommand. Register IRequestHandler<CreateNoteCommand, NoteModel> in DI."** The
  handler is not registered, or is registered under a different response type (`IReadOnlyList<T>` against `List<T>`).
  Register exactly the request's declared response type.
- **The mediator fails at run time on a request that compiles** (a `RuntimeBinderException`). A handler or request type
  is not `public`: the mediator calls handlers through `dynamic`.
- **Everyone, administrators included, is refused.** The key is not declared in `ModuleInfo`, so no site has a
  permission row for it. Declare it and restart the app; the host adds the declared keys' rows to each site on start.
- **A request works from a page but is open over HTTP, or the reverse.** A check was put only on the controller or only
  in a page. `[Requires]` on the request covers every road.
- **A job sending the command is always refused.** `[Requires]` needs an HTTP request; a background job has none. Call
  the repository or a domain service from the job instead (see [jobs](../../../docs/services/jobs.md)).
- **A refusal fills the log with errors.** A refusal type of your own (a conflict, a duplicate) is logged by the mediator
  at Error unless it implements `Stomme.Mediator.IExpectedRefusal`, as `NoteConflictException` does (since 10.73.2).
  An uncaught `FluentValidation.ValidationException` is a 400 since 10.73.2 (a 500 before); see the endpoint skill.
- **Domain-event handlers run twice.** Its `AddScoped` line appears twice (`AddMediator` does not register domain-event
  handlers). Calling `AddMediator<T>()` from two startups over one assembly is harmless since 10.73.2 (it registers each
  handler once); before, it made every notification handler run twice.
- **A handler's work happens later, at an unrelated save.** `IUnitOfWork.SaveChangesAsync` was not called after the
  write; the events waited in the scope for the next one.
- **Saving inside a domain-event handler corrupts the caller's operation.** Never call `SaveChangesAsync` or create a
  scope in a domain-event handler.
