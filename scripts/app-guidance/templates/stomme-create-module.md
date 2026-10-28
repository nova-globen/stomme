---
name: stomme-create-module
description: Use when adding a new module (a feature placed on a page) to this Nova Globen Stomme app - "create a module", "add a feature page", "new module with its own data and API" - end to end across Shared, Client and Server, storage-blind, inside the app's own projects.
---

# Create a module inside the app

You will have built a module called **Notes** inside this app's three projects: a storage-blind, tenant-owned aggregate
(`Note`) with a repository, queries and commands behind three permission keys, an API at `api/Notes`, a service contract
with a server and an HTTP implementation, a list page and an add/edit page, and a page template that puts the module on
`/notes` on every site. This is the reference module every Stomme skill in this app uses; it was built, run and driven
on Stomme's packages exactly as written here. Replace "Notes"/"Note" with your module's name throughout. Read first:
[Module anatomy](../../../docs/modules/anatomy.md) (discovery, the three projects) and
[Module UI](../../../docs/modules/module-ui.md). For depth on each layer see
[Module persistence](../../../docs/persistence/module-persistence.md),
[Application layer](../../../docs/modules/application-layer.md),
[API and client services](../../../docs/modules/api-and-client-services.md) and the skills
[stomme-add-aggregate](../stomme-add-aggregate/SKILL.md),
[stomme-add-command-or-query](../stomme-add-command-or-query/SKILL.md),
[stomme-add-api-endpoint](../stomme-add-api-endpoint/SKILL.md),
[stomme-secure-a-module](../stomme-secure-a-module/SKILL.md) and
[stomme-build-module-ui](../stomme-build-module-ui/SKILL.md), which explain each file below.

**The convention this skill uses** (it is what discovery needs):

- One namespace root for the module in all three projects: `Stomme.Application.Modules.Notes`, with sub-namespaces
  (`.Models`, `.Services`, `.Domain`, `.Persistence`, `.Application`, `.Controllers`, `.Startup`).
- In `Client`, `ModuleInfo` and EVERY `.razor` component of the module are in exactly `Stomme.Application.Modules.Notes`
  (set with `@namespace`). That namespace plus the `Client` assembly name is the module's identity.
- Files go in a `Modules/Notes` folder in each project. The app's assembly names already end in `.Stomme`
  (discovery needs `Stomme` in the name); do not change them.
- The startups are named after the module (`NotesServerStartup`, `NotesClientStartup`): every module of the app shares one
  `Server` and one `Client` assembly, and distinct names keep logs and `using` lines unambiguous.

Make it a separate package instead only when other apps will use it; see the table in
[Module anatomy](../../../docs/modules/anatomy.md#in-the-app-or-as-a-package).

## Steps

### 1. Shared: keys, wire models, service contract

`Shared/Modules/Notes/NotesPermissions.cs`:

```csharp
{{FILE:Shared/Modules/Notes/NotesPermissions.cs}}
```

The key's resource (`notes`) must not be one of the framework's entity names (`page`, `module`, `user`, `file`, `folder`,
`site`, `role`, `notification`, ...).

`Shared/Modules/Notes/Models/NoteModels.cs`:

```csharp
{{FILE:Shared/Modules/Notes/Models/NoteModels.cs}}
```

`Shared/Modules/Notes/Services/INotesService.cs`:

```csharp
{{FILE:Shared/Modules/Notes/Services/INotesService.cs}}
```

### 2. Server: the aggregate

`Server/Modules/Notes/Domain/Note.cs`:

```csharp
{{FILE:Server/Modules/Notes/Domain/Note.cs}}
```

Give the aggregate a class name that no other module of the app uses: it becomes the table name in a store all modules
share.

### 3. Server: the repository

`Server/Modules/Notes/Persistence/NoteRepository.cs`:

```csharp
{{FILE:Server/Modules/Notes/Persistence/NoteRepository.cs}}
```

### 4. Server: the queries and the commands

`Server/Modules/Notes/Application/NoteRequests.cs`:

```csharp
{{FILE:Server/Modules/Notes/Application/NoteRequests.cs}}
```

`[Requires]` makes the host's mediator refuse a caller without the key before the handler runs - from the API, a page or
a job alike. Handlers and request types must be `public`: the mediator invokes them across assemblies.

`Server/Modules/Notes/Application/NoteCreatedHandler.cs` (optional - it shows where a reaction to a domain event goes):

```csharp
{{FILE:Server/Modules/Notes/Application/NoteCreatedHandler.cs}}
```

### 5. Server: the API

`Server/Modules/Notes/Controllers/NotesController.cs`:

```csharp
{{FILE:Server/Modules/Notes/Controllers/NotesController.cs}}
```

### 6. Server: the service for server-side rendering

`Server/Modules/Notes/Services/ServerNotesService.cs`:

```csharp
{{FILE:Server/Modules/Notes/Services/ServerNotesService.cs}}
```

### 7. Server: the startup

`Server/Modules/Notes/Startup/NotesServerStartup.cs`:

```csharp
{{FILE:Server/Modules/Notes/Startup/NotesServerStartup.cs}}
```

### 8. Client: the HTTP service and its registration

`Client/Modules/Notes/Services/NotesService.cs`:

```csharp
{{FILE:Client/Modules/Notes/Services/NotesService.cs}}
```

`Client/Modules/Notes/Startup/NotesClientStartup.cs`:

```csharp
{{FILE:Client/Modules/Notes/Startup/NotesClientStartup.cs}}
```

### 9. Client: the module definition

`Client/Modules/Notes/ModuleInfo.cs`:

```csharp
{{FILE:Client/Modules/Notes/ModuleInfo.cs}}
```

`AliasName = "*"` is required: the default empty alias name matches no site and the template is skipped without a message.
Do not set `Dependencies` for a module inside the app: the app's `Shared` assembly is already part of its WebAssembly client.

### 10. Client: the pages

`Client/Modules/Notes/_Imports.razor`:

```razor
{{FILE:Client/Modules/Notes/_Imports.razor}}
```

The app's `Client/_Imports.razor` already brings `Microsoft.AspNetCore.Components.Web`, `Stomme.Modules`,
`Stomme.Modules.Controls`, `Stomme.Shared` and the rest to every folder below it - keep it that way.

`Client/Modules/Notes/Index.razor` - the list, the module's default view:

```razor
{{FILE:Client/Modules/Notes/Index.razor}}
```

`Client/Modules/Notes/Edit.razor` - one form for the `Add` and `Edit` actions:

```razor
{{FILE:Client/Modules/Notes/Edit.razor}}
```

Nothing else is registered anywhere: the host finds `NotesServerStartup`, `NotesClientStartup`, `NotesController` and
`ModuleInfo` by convention.

## Verify

1. `dotnet build` at the app root: no errors.
2. `cd Server` and `dotnet run`; install if asked (see `stomme-run-and-install`). The console must show no `fail:` line
   about the Notes module. Page templates are applied when the host starts: on a fresh installation `/notes` exists after
   the install; on an installation that existed before the module, it exists after the next start.
3. Anonymous: `GET /api/Notes` answers **403** (401 is also a refusal), never 404 (404 means the controller was not
   discovered). Opening `/notes` signed out sends you to the login page: the page itself is for Registered Users.
4. Sign in as the host. The navigation shows **Notes**; `/notes` shows "No notes yet" and **Add note**. Add a note: the
   dialog headed "Note" opens; save it and the list shows it with **Edit** and **Delete**. Edit it, then delete it through
   the confirmation; the list follows each time. If nothing happens on a click, events are dead: see Common mistakes.
5. Reload the page: the notes are still there (they were stored). Adding a second note whose title differs only in case is
   refused with "A note titled '...' already exists on this site."
6. In **Admin > Module Management** the module is listed as Notes.
7. Sign in as a user who is only a Registered User: the list shows, **Add note** shows, and **Edit**/**Delete** show only on
   that user's own notes. Over HTTP, that user's `PUT` or `DELETE` of someone else's note answers 403.
8. Check the browser console: no errors. Check the page narrow and in dark mode, and run `stomme-ux-review`.
9. If any of your sites uses the WebAssembly runtime, repeat step 4 on such a site with the console open. It works: the
   page calls `api/Notes` through `NotesService` instead of the mediator.

## Common mistakes

- **The page shows nothing and Admin > Module Management lists a module named after the folder.** A component's
  namespace differs from `ModuleInfo`'s. Every `.razor` file needs `@namespace Stomme.Application.Modules.Notes`.
- **`/notes` never appears.** `AliasName = "*"` is missing from the page template, or the app has not been restarted since the
  module was added, or `module.notes.enabled` is `false` in `Server/features.json`.
- **Buttons do nothing, no console error.** `Microsoft.AspNetCore.Components.Web` is not imported for the component. It comes
  from `Client/_Imports.razor`; do not remove it, and a component in a folder outside `Client` does not get it.
- **`GET /api/Notes` answers 404.** The controller is not discovered (an assembly name without `Stomme`) or the route is wrong:
  keep `[Route(ControllerRoutes.ApiRoute)]` and a class name ending in `Controller`.
- **"No service for type INotesService".** `NotesClientStartup` or `NotesServerStartup` is missing, not `public`, or has a
  constructor with parameters (the host creates startups with a parameterless constructor).
- **"No handler registered for ..." (a 500).** A handler was not registered in `NotesServerStartup`, is registered under a
  different response type than the request declares, or is not `public`.
- **Every request is refused (403) even for the host.** The key in `[Requires]`/`[RequiresPermissionKey]` is not declared in
  `ModuleInfo.PermissionNames` (letter case does not matter); a key no module declares denies everyone.
- **A Registered User sees no Add note although they hold `notes:edit`.** An `ActionLink` or the `Edit` component uses
  `SecurityAccessLevel.Edit`, which means "may edit this page", not "holds the module's key". Keep `Security="SecurityAccessLevel.View"`
  on the links and `SecurityAccessLevel.View` on `Edit.razor`, and decide with `IsAuthorizedPermission(...)` and on the server.
- **Notes of one site show on another.** A query forgot `note.SiteId == siteId`. The store separates tenants, not sites.
- **The add fails with "Notes belong to a tenant; none is resolved".** The command ran outside a request (a job); set the
  tenant and site first, see [Jobs](../../../docs/services/jobs.md).
- **The app does not start after adding a constructor dependency.** The host validates its container at startup; register
  every service your handlers and services take.
