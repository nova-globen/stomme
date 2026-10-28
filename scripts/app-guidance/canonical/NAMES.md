# The canonical Notes module (BL-239) - every name

Built, run and driven 2026-10-24 in a scaffolded app (Acme.Portal, Stomme 10.72.0 packages from the local feed, SQLite):
Interactive Server site - add, list, edit, delete in the browser as the host and as a Registered user; WebAssembly site
(fresh install, Runtime=WebAssembly, Prerender=false) - list, add, refused duplicate shown, through api/Notes. Over HTTP:
anonymous 403, member editing the host's note 403, empty title 400 (validator), duplicate title 409, unknown id 404.

The files here are written with the template namespace `Stomme.Application` (in the app they read `Acme.Portal`).
Paths are relative to the app root.

## Namespaces

| Namespace | Project | Holds |
|---|---|---|
| `Stomme.Application.Modules.Notes` | Shared | `NotesPermissions` |
| `Stomme.Application.Modules.Notes` | Client | `ModuleInfo`, `Index` (Index.razor), `Edit` (Edit.razor) - MUST be exactly this namespace |
| `Stomme.Application.Modules.Notes.Models` | Shared | `NoteModel`, `SaveNoteRequest` |
| `Stomme.Application.Modules.Notes.Services` | Shared | `INotesService` |
| `Stomme.Application.Modules.Notes.Services` | Server | `ServerNotesService` |
| `Stomme.Application.Modules.Notes.Services` | Client | `NotesService` |
| `Stomme.Application.Modules.Notes.Domain` | Server | `Note`, `NoteCreatedEvent` |
| `Stomme.Application.Modules.Notes.Persistence` | Server | `INoteRepository`, `NoteRepository` |
| `Stomme.Application.Modules.Notes.Application` | Server | requests, handlers, validators, `NoteConflictException`, `NoteCreatedHandler`, `NotesScope` (internal) |
| `Stomme.Application.Modules.Notes.Controllers` | Server | `NotesController` |
| `Stomme.Application.Modules.Notes.Startup` | Server | `NotesServerStartup` |
| `Stomme.Application.Modules.Notes.Startup` | Client | `NotesClientStartup` |

## Files

- `Shared/Modules/Notes/NotesPermissions.cs`
- `Shared/Modules/Notes/Models/NoteModels.cs`
- `Shared/Modules/Notes/Services/INotesService.cs`
- `Server/Modules/Notes/Domain/Note.cs`
- `Server/Modules/Notes/Persistence/NoteRepository.cs`
- `Server/Modules/Notes/Application/NoteRequests.cs`
- `Server/Modules/Notes/Application/NoteCreatedHandler.cs`
- `Server/Modules/Notes/Controllers/NotesController.cs`
- `Server/Modules/Notes/Services/ServerNotesService.cs`
- `Server/Modules/Notes/Startup/NotesServerStartup.cs`
- `Client/Modules/Notes/ModuleInfo.cs`
- `Client/Modules/Notes/_Imports.razor`
- `Client/Modules/Notes/Index.razor`
- `Client/Modules/Notes/Edit.razor`
- `Client/Modules/Notes/Services/NotesService.cs`
- `Client/Modules/Notes/Startup/NotesClientStartup.cs`

## Permission keys (`NotesPermissions`)

| Constant | Key | Granted by `Declarations` to (Declare adds Administrators to every key) |
|---|---|---|
| `View` | `notes:view` | `RoleNames.Registered` |
| `Edit` | `notes:edit` | `RoleNames.Registered` (write notes; change/delete your own) |
| `Manage` | `notes:manage` | `RoleNames.Admin` (change/delete anyone's) |
| `Declarations` | `IReadOnlyList<(string Key, string[] Roles)>` | used by `PermissionKeys.Declare(NotesPermissions.Declarations)` in `ModuleInfo.PermissionNames` |

## Shared types

- `NoteModel(Guid Id, string Title, string Body, int AuthorUserId, DateTime CreatedUtc, DateTime UpdatedUtc)` - sealed record.
- `SaveNoteRequest { string Title; string Body; }` - one request class for create and edit (no tenant/site/author).
- `INotesService`: `Task<List<NoteModel>> ListAsync()`, `Task<NoteModel?> GetAsync(Guid noteId)`,
  `Task<NoteModel> CreateAsync(SaveNoteRequest request)`, `Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request)`,
  `Task DeleteAsync(Guid noteId)`.

## Aggregate `Note : AggregateRoot<Guid>, ITenantOwned`

- Constants `MaxTitleLength = 120`, `MaxBodyLength = 4000`.
- Properties (all `{ get; private set; }`): `TenantId TenantId`, `int SiteId`, `string Title`, `string TitleKey`, `string Body`,
  `int AuthorUserId`, `DateTime CreatedUtc`, `DateTime UpdatedUtc`.
- `static string KeyOf(string title)` - trim + `ToUpperInvariant`.
- `static Note Create(Guid id, TenantId tenantId, int siteId, int authorUserId, string title, string body, DateTime nowUtc)` - raises `NoteCreatedEvent`.
- `void Edit(string title, string body, DateTime nowUtc)`.
- `NoteCreatedEvent(Guid NoteId, int SiteId, int AuthorUserId) : DomainEvent` - sealed record.

## Repository

- `INoteRepository : IRepository<Note, Guid>` with `ListForSiteAsync(int siteId, ct)`, `FindByTitleAsync(int siteId, string title, ct)`,
  `GetForSiteAsync(int siteId, Guid id, ct)`.
- `NoteRepository(IRepository<Note, Guid> inner)` - forwards all six `IRepository` members incl. `FindAsync`.

## Requests (all `public`, `[Requires]` on the request type)

| Request | Response | Key | Handler | Validator |
|---|---|---|---|---|
| `ListNotesQuery` | `IReadOnlyList<NoteModel>` | View | `ListNotesQueryHandler` | - |
| `GetNoteQuery(Guid NoteId)` | `NoteModel?` | View | `GetNoteQueryHandler` | - |
| `CreateNoteCommand(string Title, string Body)` | `NoteModel` | Edit | `CreateNoteCommandHandler` | `CreateNoteCommandValidator` |
| `EditNoteCommand(Guid NoteId, string Title, string Body)` | `NoteModel` | Edit (+ author or Manage, per row) | `EditNoteCommandHandler` | `EditNoteCommandValidator` |
| `DeleteNoteCommand(Guid NoteId)` : `ICommand` | `Unit` | Edit (+ author or Manage, per row) | `DeleteNoteCommandHandler` | - |

- `NoteConflictException(string message) : Exception, Stomme.Mediator.IExpectedRefusal` - duplicate title (409); the marker makes the mediator log it at Information, not Error.
- `NoteCreatedHandler : IDomainEventHandler<NoteCreatedEvent>` - logs "Note {NoteId} created on site {SiteId}."
- `NotesScope` (internal static): `Site(SiteState)`, `Resolve(ITenantContext, SiteState)`, `EnsureMayChangeAsync(Note, ICurrentUser, IAccessGuard, ct)`, `ToModel(Note)`.
- Validation message for an empty title: "Give the note a title."; per-row denial: "Only the note's author or a notes manager may change it."

## API (`NotesController : ModuleControllerBase`, `[Route(ControllerRoutes.ApiRoute)]`)

| Route | Action | Key | Answers |
|---|---|---|---|
| `GET api/Notes` | `List` | View | 200 list |
| `GET api/Notes/{id:guid}` | `Get` | View | 200 / 404 |
| `POST api/Notes` | `Create` | Edit | 200 / 400 / 409 / 403 |
| `PUT api/Notes/{id:guid}` | `Edit` | Edit | 200 / 400 / 404 / 409 / 403 |
| `DELETE api/Notes/{id:guid}` | `Delete` | Edit | 204 / 404 / 403 |

Private `Answer(Func<Task<IActionResult>>)` maps ValidationException 400 (ValidationProblem), KeyNotFoundException 404,
NoteConflictException 409, ConcurrencyConflictException 409, ArgumentException 400; AccessDeniedException left to the host (403).

## Registrations (`NotesServerStartup.ConfigureServices`, in this order)

```
services.TryAddSingleton(TimeProvider.System);
services.AddModuleAggregatePersistence<Note, Guid>();
services.AddScoped<INoteRepository, NoteRepository>();
services.AddScoped<IRequestHandler<ListNotesQuery, IReadOnlyList<NoteModel>>, ListNotesQueryHandler>();
services.AddScoped<IRequestHandler<GetNoteQuery, NoteModel?>, GetNoteQueryHandler>();
services.AddScoped<IRequestHandler<CreateNoteCommand, NoteModel>, CreateNoteCommandHandler>();
services.AddScoped<IRequestHandler<EditNoteCommand, NoteModel>, EditNoteCommandHandler>();
services.AddScoped<IRequestHandler<DeleteNoteCommand, Unit>, DeleteNoteCommandHandler>();
services.AddScoped<IValidator<CreateNoteCommand>, CreateNoteCommandValidator>();
services.AddScoped<IValidator<EditNoteCommand>, EditNoteCommandValidator>();
services.AddScoped<IDomainEventHandler<NoteCreatedEvent>, NoteCreatedHandler>();
services.AddTransient<INotesService, ServerNotesService>();   // last
```

No `AddMediator`. Client: `NotesClientStartup` adds `INotesService -> NotesService` (scoped) only when no registration exists.
`NotesService : ServiceBase` overrides `ThrowOnRefusal => true`; `CreateApiUrl("Notes")`.

## Client UI

- `ModuleInfo : IModule` - Name "Notes", Description "Short notes kept per site.", Version "1.0.0",
  `PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations)`, one `PageTemplate` Name "Notes", Path "notes",
  `AliasName = "*"`, Version "*", Order 10, IsNavigation true, page/module permissions View: Registered + Administrators, Edit: Administrators.
- `Index.razor` - default action; `Pager` of notes (Title, Last changed via `UtcToLocal`), `ActionLink Action="Add"` (Security View, shown
  when `IsAuthorizedPermission(NotesPermissions.Edit)`), per row `ActionLink Action="Edit"` + `ActionDialog` delete
  (`Id="delete-{id}"`) shown when author or Manage; empty state `db-empty` "No notes yet".
- `Edit.razor` - `Actions => "Add,Edit"`, `Title => "Note"`, `SecurityAccessLevel => SecurityAccessLevel.View`; fields
  `#note-title`, `#note-body`; Save / Cancel.
- Page URL `/notes`; actions `/notes/*/{moduleId}/Add`, `/notes/*/{moduleId}/Edit?id={guid}`.
