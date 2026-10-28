---
name: stomme-secure-a-module
description: Use when securing a Nova Globen Stomme module or reviewing one for security - declaring permission keys, authorizing API endpoints and commands, checking per-row access (site, owner), scoping data to the tenant and site, validating input, gating a feature by a switch, or keeping a module's secrets; also when an endpoint answers 403 to an administrator, 200 to an anonymous caller, or one site sees another site's data.
---

# Secure a module

You will have a module whose every endpoint and every command or query is authorized by a permission key, whose every
row is scoped to the request's tenant and site and checked per row where the data decides access, whose input is
validated in the domain, and whose UI shows only what the user may use. The example is the reference **Notes** module of
[stomme-create-module](../stomme-create-module/SKILL.md): everyone registered on a site reads the site's notes and
writes their own (`notes:view`, `notes:edit`); holders of `notes:manage` change anyone's. The code below is that module's,
unchanged; it was driven as the host, as a Registered User and anonymously.

Read first: [identity-and-permissions.md](../../../docs/security/identity-and-permissions.md),
[tenancy.md](../../../docs/security/tenancy.md), [security-posture.md](../../../docs/security/security-posture.md).
The module itself (projects, discovery, the client service chain) comes from the `stomme-create-module` skill; this
skill explains, and checks, the parts that make it secure. Use your module's names where these say `Notes`.

## Steps

1. **Shared: declare the keys.** `Shared/Modules/Notes/NotesPermissions.cs`. Keys are `resource:operation`, lower case,
   and the resource must not be a framework entity name (`page`, `module`, `user`, `site`, `file`, `folder`, ...).

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

   `Shared/Modules/Notes/Models/NoteModels.cs` - what crosses the wire. No tenant id, no site id, no author id the client
   could set:

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

2. **Client: declare the keys on the module definition.** In the module's `ModuleInfo` (Client project,
   `Client/Modules/Notes/ModuleInfo.cs`), set `PermissionNames` (`using Stomme.Security;`). Keep everything else your
   module already has:

   ```csharp
   PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations),
   ```

   Every module definition that uses these keys (a second component package, a settings module) declares them too. The
   host adds the declared keys' rows to each site when it starts.

3. **Server: a tenant-owned, site-scoped aggregate that validates itself.** `Server/Modules/Notes/Domain/Note.cs`
   implements `ITenantOwned`, carries `SiteId` and `AuthorUserId`, and refuses an invalid title or body in its own
   methods (`ArgumentException`, which the module's controller catches and answers with 400 - uncaught, the host
   would answer 500). The file is in
   [stomme-add-aggregate](../stomme-add-aggregate/SKILL.md); what matters here:

   ```csharp
   public static Note Create(Guid id, TenantId tenantId, int siteId, int authorUserId, string title, string body, DateTime nowUtc)
   {
       ArgumentNullException.ThrowIfNull(tenantId);
       if (siteId <= 0)
       {
           throw new ArgumentOutOfRangeException(nameof(siteId), "A note belongs to a site.");
       }

       if (authorUserId <= 0)
       {
           throw new ArgumentOutOfRangeException(nameof(authorUserId), "A note is written by a signed-in user.");
       }
       ...
   }
   ```

4. **Server: gate every request, scope every row, check per row.** `Server/Modules/Notes/Application/NoteRequests.cs`.
   Each request TYPE carries `[Requires]` - an attribute on a handler is never read. Another site's note is reported as
   not found; a note the caller may see but not change is refused with `AccessDeniedException` (403) by
   `NotesScope.EnsureMayChangeAsync`.

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

5. **Server: authorize every action.** `Server/Modules/Notes/Controllers/NotesController.cs`. `[RequiresPermissionKey]` on
   each action, the same key as the request behind it. No `[AllowAnonymous]` on the class. `AccessDeniedException` is not
   caught: the host answers 403.

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Application;
   using Stomme.Application.Modules.Notes.Models;
   using Stomme.Controllers;
   using Stomme.Infrastructure;
   using Stomme.Infrastructure.Security;
   using Stomme.Mediator;
   using Stomme.Persistence.Abstractions;
   using Stomme.Shared;
   using FluentValidation;
   using Microsoft.AspNetCore.Http;
   using Microsoft.AspNetCore.Mvc;

   namespace Stomme.Application.Modules.Notes.Controllers
   {
       /// <summary>
       /// api/Notes. Each action is gated by a permission key before it runs, and the request it sends is gated again by its
       /// [Requires]. 400: invalid input; 403: not allowed; 404: unknown or another site's note; 409: a clash or a stale write.
       /// </summary>
       [Route(ControllerRoutes.ApiRoute)]
       public sealed class NotesController : ModuleControllerBase
       {
           private readonly IMediator _mediator;

           public NotesController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
               : base(logger, accessor)
           {
               _mediator = mediator;
           }

           // GET api/Notes
           [HttpGet]
           [RequiresPermissionKey(NotesPermissions.View)]
           public async Task<IActionResult> List(CancellationToken ct) =>
               Ok(await _mediator.Send(new ListNotesQuery(), ct));

           // GET api/Notes/{id}
           [HttpGet("{id:guid}")]
           [RequiresPermissionKey(NotesPermissions.View)]
           public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
               await _mediator.Send(new GetNoteQuery(id), ct) is { } note ? Ok(note) : NotFound();

           // POST api/Notes
           [HttpPost]
           [RequiresPermissionKey(NotesPermissions.Edit)]
           public Task<IActionResult> Create([FromBody] SaveNoteRequest request, CancellationToken ct) =>
               Answer(async () => Ok(await _mediator.Send(new CreateNoteCommand(request.Title, request.Body), ct)));

           // PUT api/Notes/{id}
           [HttpPut("{id:guid}")]
           [RequiresPermissionKey(NotesPermissions.Edit)]
           public Task<IActionResult> Edit(Guid id, [FromBody] SaveNoteRequest request, CancellationToken ct) =>
               Answer(async () => Ok(await _mediator.Send(new EditNoteCommand(id, request.Title, request.Body), ct)));

           // DELETE api/Notes/{id}
           [HttpDelete("{id:guid}")]
           [RequiresPermissionKey(NotesPermissions.Edit)]
           public Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
               Answer(async () =>
               {
                   await _mediator.Send(new DeleteNoteCommand(id), ct);
                   return NoContent();
               });

           // AccessDeniedException is not caught: the host turns it into 403. These are the other answers.
           private async Task<IActionResult> Answer(Func<Task<IActionResult>> action)
           {
               try
               {
                   return await action();
               }
               catch (ValidationException ex)
               {
                   foreach (var failure in ex.Errors)
                   {
                       ModelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
                   }

                   return ValidationProblem(ModelState);
               }
               catch (KeyNotFoundException)
               {
                   return NotFound();
               }
               catch (NoteConflictException ex)
               {
                   return Conflict(new { error = ex.Message });
               }
               catch (ConcurrencyConflictException)
               {
                   return Conflict(new { error = "Someone else changed this note in the meantime. Reload it and try again." });
               }
               catch (ArgumentException ex)
               {
                   return BadRequest(new { error = ex.Message });
               }
           }
       }
   }
   ```

6. **Server: register.** In `NotesServerStartup.ConfigureServices` the aggregate, the repository, and each handler and
   validator explicitly - the full list is in [stomme-create-module](../stomme-create-module/SKILL.md):

   ```csharp
   services.TryAddSingleton(TimeProvider.System);
   services.AddModuleAggregatePersistence<Note, Guid>();
   services.AddScoped<INoteRepository, NoteRepository>();
   services.AddScoped<IRequestHandler<EditNoteCommand, NoteModel>, EditNoteCommandHandler>();
   // ... one line per handler and validator
   ```

   Do not call `AddMediator` in a module: the host's mediator is composed already, and a call scans the app's whole
   `Server` assembly and registers every handler of every module again. `IAccessGuard`, `ICurrentUser`,
   `ITenantContext` and `SiteState` are the host's; do not register them.

7. **Client: hide what the user cannot use.** In each component of the module (from `Client/Modules/Notes/Index.razor`):

   ```razor
   @namespace Stomme.Application.Modules.Notes
   @inherits ModuleBase

   @if (!IsAuthorizedPermission(NotesPermissions.View))
   {
       <p id="notes-denied">You do not have access to the notes on this site.</p>
   }
   else if (IsAuthorizedPermission(NotesPermissions.Edit))
   {
       <ActionLink Action="Add" Text="Add note" Security="SecurityAccessLevel.View" />
   }

   @code {
       // A convenience for the UI only: the server decides again on every change.
       private bool MayChange(NoteModel note) =>
           IsAuthorizedPermission(NotesPermissions.Edit)
           && (note.AuthorUserId == PageState.User?.UserId || IsAuthorizedPermission(NotesPermissions.Manage));
   }
   ```

   Show an edit control on a note only when `MayChange(note)`. `Security="SecurityAccessLevel.View"` is deliberate:
   `SecurityAccessLevel.Edit` means "may edit this page" and would hide the link from a Registered User who holds
   `notes:edit`. This is a convenience; the server decided in step 4.

8. **Secrets and outbound calls, if the module has them.** A token or key goes in `ISiteSecretStore`, never in a
   setting or the aggregate; a URL a user typed is checked with `OutboundConnectGuard.CheckDestinationAsync` when saved
   and called through `OutboundConnectGuard.CreateHandler()`. Samples: [security-posture.md](../../../docs/security/security-posture.md).

9. **A switch, if the feature is optional.** Put an `ActionMethodSelectorAttribute` on the controller that reads the
   switch through `HostConfiguration.Resolve(...)` (sample in security-posture.md), so a switched-off API answers 404
   for every caller.

## Verify

Build, then run the app (`cd Server`, `dotnet run`) on a fresh install and drive it in a browser and over HTTP:

1. Sign in as the Host. The module's page lists notes; **Add note** works. In **Admin > Module Management**, open
   Notes, **Permissions** tab: the three keys appear with the roles you declared plus Administrators.
2. Create a second user (**Admin > User Management**, or `POST /api/User` as the host). As that user: reading and adding
   work, and **Edit**/**Delete** show on that user's own notes only. Changing the Host's note over HTTP
   (`PUT /api/Notes/{id}`) answers 403 `application/problem+json` with "Only the note's author or a notes manager may
   change it."; as the Host, changing that user's note works.
3. Sign out. Opening the page sends you to the login page; `GET /api/Notes` answers 403 (401 is also a refusal), never
   200 with data.
4. Over HTTP as the second user, `POST /api/Notes` with an empty title answers 400 with "Give the note a title."; a
   `POST` without the `X-XSRF-TOKEN-HEADER` header (from a session that never sent it) answers 400 (antiforgery).
5. Add a second site (**Admin > Site Management**) in the same tenant. `GET /api/Notes/{id of a site-1 note}` on site 2,
   as its administrator, answers **404**.
6. Clear `notes:edit` for Registered Users on the Permissions tab: the second user's **Add note** disappears and
   `POST /api/Notes` answers 403 on the very next request.
7. The browser console shows no errors. **Admin > Event Log** shows each denial: an Error entry "User member1 Was Denied
   Access: ..." for a refused change, and "User ... Does Not Hold Permission Key ..." for a refused key - an Error for a
   signed-in caller (step 6's `notes:edit`), a Warning for an anonymous one (step 3's `notes:view`).

It must NOT happen: a 200 to an anonymous caller, an administrator getting 403 on a key you declared, another site's
note returned, a 500 for a denial or bad input.

## Common mistakes

- **Administrators get 403 on every action.** The key in `[RequiresPermissionKey]` or `[Requires]` is not the one
  declared (typo, upper case, a second definition that does not declare it), or the site has no rows yet. Use the
  constants from `NotesPermissions` everywhere; keys with no rows deny everyone.
- **An anonymous caller gets 200.** The action has no authorization attribute, or the controller class carries
  `[AllowAnonymous]`, which switches off `[RequiresPermissionKey]` on every action.
- **The API denies but an Interactive Server page does not**, or the reverse. The page calls a server-side service that
  calls the mediator; only `[Requires]` on the request type protects that path. Put it on every request type.
- **`[Requires]` seems ignored.** It is on the handler; move it to the command or query type.
- **Site 2 sees site 1's rows.** A read or write does not compare `SiteId` with `SiteState.Alias.SiteId`, or the site id
  came from the request body.
- **Every tenant sees a row.** The aggregate does not implement `ITenantOwned`; the host logs its name at startup.
- **A Registered User who holds the key cannot open the form.** The component or its `ActionLink` uses
  `SecurityAccessLevel.Edit` (page editors only). Use `SecurityAccessLevel.View` and the key checks above.
- **A switched-off API answers 403 instead of 404.** The switch is checked in the action body, after the policy. Use an
  `ActionMethodSelectorAttribute`.
- **A job is denied.** Jobs have no principal: `[Requires]` and `IAccessGuard` deny there. A job acting for a user enters
  that user's site and checks with `IStoredRowPermissionEvaluator` (see
  [identity-and-permissions.md](../../../docs/security/identity-and-permissions.md)).
- **A denial is a 500.** The handler threw something other than `AccessDeniedException` for "not allowed", or a
  controller swallowed `AccessDeniedException` into its own catch-all. Let it propagate.
