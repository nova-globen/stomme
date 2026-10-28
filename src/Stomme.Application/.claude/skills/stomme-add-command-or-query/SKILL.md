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
   using System.Collections.Generic;
   using Stomme.Shared;

   namespace Stomme.Application.Modules.Notes
   {
       /// <summary>The module's permission keys: "resource:operation", lower-case.</summary>
       public static class NotesPermissions
       {
           /// <summary>Read the site's notes.</summary>
           public const string View = "notes:view";

           /// <summary>Write notes, and change or delete your own.</summary>
           public const string Edit = "notes:edit";

           /// <summary>Change or delete anyone's note.</summary>
           public const string Manage = "notes:manage";

           /// <summary>Each key with the roles a site grants it to. PermissionKeys.Declare adds Administrators to every key.</summary>
           public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
           {
               (View, new[] { RoleNames.Registered }),
               (Edit, new[] { RoleNames.Registered }),
               (Manage, new[] { RoleNames.Admin }),
           };
       }
   }
   ```

   A key with no permission rows on a site refuses everyone, administrators included. The module's `ModuleInfo` must
   declare the keys with `PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations)` - see
   [identity and permissions](../../../docs/security/identity-and-permissions.md) and
   [stomme-secure-a-module](../stomme-secure-a-module/SKILL.md). The resource part must not be one of Stomme's
   entity names (`Site`, `Page`, `Module`, `User`, ...).

2. **Write the wire models** - what the handlers return and what the API receives - in
   `Shared/Modules/Notes/Models/NoteModels.cs`. Never return the aggregate itself.

   ```csharp
   using System;

   namespace Stomme.Application.Modules.Notes.Models
   {
       /// <summary>A note as the API and the pages see it. Never the aggregate itself.</summary>
       public sealed record NoteModel(Guid Id, string Title, string Body, int AuthorUserId, DateTime CreatedUtc, DateTime UpdatedUtc);

       /// <summary>What a caller sends to create or change a note. No tenant, site or author: the server decides those.</summary>
       public sealed class SaveNoteRequest
       {
           public string Title { get; set; } = string.Empty;

           public string Body { get; set; } = string.Empty;
       }
   }
   ```

3. **Write the requests and handlers** in `Server/Modules/Notes/Application/NoteRequests.cs`. Every type the mediator
   touches is `public`. Every request carries `[Requires]`.

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
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
   using Stomme.Tenancy.Domain;
   using FluentValidation;

   namespace Stomme.Application.Modules.Notes.Application
   {
       /// <summary>A change refused because it clashes with what is stored (the API answers 409). <see cref="IExpectedRefusal"/>: the
       /// mediator logs it as a refusal, not as an error.</summary>
       public sealed class NoteConflictException(string message) : Exception(message), IExpectedRefusal;

       // ---- Queries ---------------------------------------------------------------------------------------------------

       [Requires(NotesPermissions.View)]
       public sealed record ListNotesQuery : IQuery<IReadOnlyList<NoteModel>>;

       public sealed class ListNotesQueryHandler(INoteRepository notes, SiteState siteState)
           : IQueryHandler<ListNotesQuery, IReadOnlyList<NoteModel>>
       {
           public async Task<IReadOnlyList<NoteModel>> Handle(ListNotesQuery request, CancellationToken ct) =>
               (await notes.ListForSiteAsync(NotesScope.Site(siteState), ct)).Select(NotesScope.ToModel).ToList();
       }

       [Requires(NotesPermissions.View)]
       public sealed record GetNoteQuery(Guid NoteId) : IQuery<NoteModel?>;

       public sealed class GetNoteQueryHandler(INoteRepository notes, SiteState siteState)
           : IQueryHandler<GetNoteQuery, NoteModel?>
       {
           public async Task<NoteModel?> Handle(GetNoteQuery request, CancellationToken ct) =>
               await notes.GetForSiteAsync(NotesScope.Site(siteState), request.NoteId, ct) is { } note
                   ? NotesScope.ToModel(note)
                   : null;
       }

       // ---- Commands --------------------------------------------------------------------------------------------------

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

       [Requires(NotesPermissions.Edit)]
       public sealed record EditNoteCommand(Guid NoteId, string Title, string Body) : ICommand<NoteModel>;

       public sealed class EditNoteCommandValidator : AbstractValidator<EditNoteCommand>
       {
           public EditNoteCommandValidator()
           {
               RuleFor(command => command.NoteId).NotEmpty();
               RuleFor(command => command.Title)
                   .NotEmpty().WithMessage("Give the note a title.")
                   .MaximumLength(Note.MaxTitleLength);
               RuleFor(command => command.Body)
                   .MaximumLength(Note.MaxBodyLength);
           }
       }

       public sealed class EditNoteCommandHandler(
           INoteRepository notes,
           IUnitOfWork unitOfWork,
           SiteState siteState,
           ICurrentUser currentUser,
           IAccessGuard accessGuard,
           TimeProvider clock) : ICommandHandler<EditNoteCommand, NoteModel>
       {
           public async Task<NoteModel> Handle(EditNoteCommand request, CancellationToken ct)
           {
               var siteId = NotesScope.Site(siteState);
               var note = await notes.GetForSiteAsync(siteId, request.NoteId, ct)
                   ?? throw new KeyNotFoundException($"Note '{request.NoteId}' was not found.");
               await NotesScope.EnsureMayChangeAsync(note, currentUser, accessGuard, ct);
               if (await notes.FindByTitleAsync(siteId, request.Title, ct) is { } other && other.Id != note.Id)
               {
                   throw new NoteConflictException($"A note titled '{request.Title.Trim()}' already exists on this site.");
               }

               // Read, change, save THAT instance: the module store's repositories do not track reads.
               note.Edit(request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
               await notes.UpdateAsync(note, ct);
               await unitOfWork.SaveChangesAsync(ct);
               return NotesScope.ToModel(note);
           }
       }

       [Requires(NotesPermissions.Edit)]
       public sealed record DeleteNoteCommand(Guid NoteId) : ICommand;

       public sealed class DeleteNoteCommandHandler(
           INoteRepository notes,
           IUnitOfWork unitOfWork,
           SiteState siteState,
           ICurrentUser currentUser,
           IAccessGuard accessGuard) : ICommandHandler<DeleteNoteCommand, Unit>
       {
           public async Task<Unit> Handle(DeleteNoteCommand request, CancellationToken ct)
           {
               var note = await notes.GetForSiteAsync(NotesScope.Site(siteState), request.NoteId, ct)
                   ?? throw new KeyNotFoundException($"Note '{request.NoteId}' was not found.");
               await NotesScope.EnsureMayChangeAsync(note, currentUser, accessGuard, ct);
               await notes.DeleteAsync(note, ct);
               await unitOfWork.SaveChangesAsync(ct);
               return Unit.Value;
           }
       }

       /// <summary>The request's site and tenant - refused when missing, never guessed - the per-row check, and the wire mapping.</summary>
       internal static class NotesScope
       {
           public static int Site(SiteState siteState) =>
               siteState.Alias?.SiteId is int siteId && siteId > 0
                   ? siteId
                   : throw new InvalidOperationException("Notes belong to the request's site; none is resolved.");

           public static (TenantId TenantId, int SiteId) Resolve(ITenantContext tenantContext, SiteState siteState) =>
               (tenantContext.CurrentTenantId
                   ?? throw new InvalidOperationException("Notes belong to a tenant; none is resolved."),
                Site(siteState));

           // [Requires] decided the key; the ROW decides the rest: the author, or a holder of notes:manage.
           public static async Task EnsureMayChangeAsync(Note note, ICurrentUser currentUser, IAccessGuard accessGuard, CancellationToken ct)
           {
               var isAuthor = currentUser.UserId is int userId && note.AuthorUserId == userId;
               if (!isAuthor && !await accessGuard.IsAllowedAsync(null, NotesPermissions.Manage, ct))
               {
                   throw new AccessDeniedException("Only the note's author or a notes manager may change it.");
               }
           }

           public static NoteModel ToModel(Note note) =>
               new(note.Id, note.Title, note.Body, note.AuthorUserId, note.CreatedUtc, note.UpdatedUtc);
       }
   }
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
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Models;

   namespace Stomme.Application.Modules.Notes.Services
   {
       /// <summary>The contract the pages use: over HTTP in a browser, through the mediator on the server.</summary>
       public interface INotesService
       {
           Task<List<NoteModel>> ListAsync();

           Task<NoteModel?> GetAsync(Guid noteId);

           Task<NoteModel> CreateAsync(SaveNoteRequest request);

           Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request);

           Task DeleteAsync(Guid noteId);
       }
   }
   ```

   and its server implementation, `Server/Modules/Notes/Services/ServerNotesService.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Application;
   using Stomme.Application.Modules.Notes.Models;
   using Stomme.Mediator;

   namespace Stomme.Application.Modules.Notes.Services
   {
       /// <summary>The pages' service on the server: the same requests the API sends, so [Requires] applies alike.</summary>
       public sealed class ServerNotesService(IMediator mediator) : INotesService
       {
           public async Task<List<NoteModel>> ListAsync() => (await mediator.Send(new ListNotesQuery())).ToList();

           public Task<NoteModel?> GetAsync(Guid noteId) => mediator.Send(new GetNoteQuery(noteId));

           public Task<NoteModel> CreateAsync(SaveNoteRequest request) =>
               mediator.Send(new CreateNoteCommand(request.Title, request.Body));

           public Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request) =>
               mediator.Send(new EditNoteCommand(noteId, request.Title, request.Body));

           public async Task DeleteAsync(Guid noteId) => await mediator.Send(new DeleteNoteCommand(noteId));
       }
   }
   ```

   registered last in the same `ConfigureServices`: `services.AddTransient<INotesService, ServerNotesService>();`. On an
   Interactive Server site the page calls this service and the request never goes over HTTP; `[Requires]` still applies:
   the guard reads the signed-in user from the HTTP context of the circuit's connection (driven: the host and a
   Registered User get what their keys allow). The browser's HTTP implementation and the controller come from
   [stomme-add-api-endpoint](../stomme-add-api-endpoint/SKILL.md); a WebAssembly site needs them.

6. **React to a domain event** (optional). The aggregate raises `NoteCreatedEvent`; a handler in
   `Server/Modules/Notes/Application/NoteCreatedHandler.cs` runs inside `SaveChangesAsync`:

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
