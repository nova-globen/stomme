---
name: stomme-add-api-endpoint
description: Use when a module needs an HTTP API, a REST endpoint, a Web API controller, a route under api/, or a client service that calls the server from a Blazor component or the browser (WebAssembly) in a Nova Globen Stomme app - including authorizing the endpoint ([RequiresPermissionKey]), antiforgery on POST/PUT/DELETE, and the Shared contract, controller, server service and HTTP client service chain.
---

# Add an API endpoint and its client service

You will have the module's API controller under `api/{Name}`, authorized per action, and the browser's HTTP
implementation of the module's service interface, registered so that a WebAssembly site calls the API and a server
rendered site calls the mediator directly. How it works:
[API and client services](../../../docs/modules/api-and-client-services.md).

The steps continue the reference **Notes** module of [stomme-create-module](../stomme-create-module/SKILL.md) and
[stomme-add-command-or-query](../stomme-add-command-or-query/SKILL.md): the keys `NotesPermissions.View` / `Edit` /
`Manage`, the models `NoteModel` and `SaveNoteRequest`, the requests `ListNotesQuery`, `GetNoteQuery`,
`CreateNoteCommand`, `EditNoteCommand`, `DeleteNoteCommand`, the exception `NoteConflictException`, the interface
`INotesService` (in `Shared`) and `ServerNotesService` (in `Server`) exist. The code below is that module's, unchanged.

## Steps

1. **Check the permission keys are declared.** The module's `ModuleInfo` (in `Client`) must declare `notes:view`,
   `notes:edit` and `notes:manage` with the roles a site grants them to, through `PermissionKeys.Declare`
   (`Stomme.Security`) in `ModuleDefinition.PermissionNames`. Without it every caller is refused. See
   [identity and permissions](../../../docs/security/identity-and-permissions.md) and
   [stomme-secure-a-module](../stomme-secure-a-module/SKILL.md).

2. **Write the controller** in `Server/Modules/Notes/Controllers/NotesController.cs`. Every action carries
   `[RequiresPermissionKey]` and sends one request through the mediator; one place maps exceptions to status codes.

   ```csharp
{{FILE:Server/Modules/Notes/Controllers/NotesController.cs|3}}
   ```

   Nothing to register: the host finds controllers in the app's assemblies. An `AccessDeniedException` from `[Requires]`
   or from a handler's per-row check needs no catch either - the host turns it into 403 `application/problem+json`
   carrying the exception's message as `detail`. Do not put `[AllowAnonymous]` on the class; for a truly public read,
   put it on that one action and leave its request without `[Requires]`.

3. **Write the browser's service** in `Client/Modules/Notes/Services/NotesService.cs`. `ServiceBase` builds the URL
   (with the site's path when the site is served at one) and sends the antiforgery header on every call.

   ```csharp
{{FILE:Client/Modules/Notes/Services/NotesService.cs|3}}
   ```

   The name passed to `CreateApiUrl` is the controller's name without `Controller`. With `ThrowOnRefusal`, a GET that
   answers 404 still returns null (not found); every other refusal throws `ServiceRefusalException` whose `Message` is
   the server's reason (the `error`, the first validation error, or the problem's `detail`).

4. **Register it for the browser only when nothing else has**, in `Client/Modules/Notes/Startup/NotesClientStartup.cs`
   (or in the module's existing `IClientStartup`):

   ```csharp
{{FILE:Client/Modules/Notes/Startup/NotesClientStartup.cs|3}}
   ```

   And in the module's `NotesServerStartup.ConfigureServices`, as its last line if it is not there yet:
   `services.AddTransient<INotesService, ServerNotesService>();`.

5. **Call it from the component** through `@inject INotesService NotesService` - never the controller URL, the
   repository or the mediator. Catch the exception of a refused call and show its `Message`, HTML-encoded, with
   `AddModuleMessage(..., MessageType.Error)`. The pages themselves are
   [stomme-build-module-ui](../stomme-build-module-ui/SKILL.md).

6. **Calling the API from anything else** (JavaScript, another program): send the antiforgery token
   (`SiteState.AntiForgeryToken`, also the `__RequestVerificationToken` hidden field of a rendered page) in the
   `X-XSRF-TOKEN-HEADER` header on every POST, PUT and DELETE, with the cookies of the signed-in session, or a bearer
   token in `Authorization`, which skips the antiforgery check (see
   [authentication options](../../../docs/security/authentication-options.md)).

## Verify

1. `dotnet build`: no errors, no new warnings. Then `cd Server`, `dotnet run`.
2. Signed out, open `/api/Notes` in the browser: refused (403), unless `notes:view` is granted to all users - then an
   empty or public list.
3. Signed in as the host, open `/api/Notes`: a JSON array (`[]` at first).
4. On the module's page, add, edit and delete a note. On a WebAssembly site, the browser's developer tools (Network) show
   the POST/PUT/DELETE requests to `/api/Notes` carrying `X-XSRF-TOKEN-HEADER`; on an Interactive Server site no request
   to `/api/Notes` appears at all, because the page calls the mediator directly. Both must work: test on a site of each
   runtime (Admin > Site Settings > UI Component Settings (a section only the Host sees)).
5. Over HTTP as the host: `POST /api/Notes` with an empty title answers 400 with "Give the note a title."; the same title
   twice answers 409; `GET /api/Notes/{a new Guid}` answers 404; a POST without `X-XSRF-TOKEN-HEADER` answers 400.
6. Open `/api/Notes/{id}` with an id from another site of the installation, as that site's administrator: 404.
7. The browser console shows no error, and the host's console no "AutoValidateAntiforgeryTokenFilter Failure" line for
   the page's own calls. On a WebAssembly site a refusal you provoke on purpose (a duplicate title) adds one
   "Failed to load resource: ... 409" line for that request; that one is expected.

When you script the HTTP checks with PowerShell's `Invoke-WebRequest -WebSession`, note that headers you pass are kept in
the session and sent again on later calls: a "request without the antiforgery header" sent through the same session
still carries it and answers 200. Use a fresh session for that check.

## Common mistakes

- **403 for every caller, administrators included.** The key is not declared in `ModuleInfo` (no site has rows for it),
  or the key in `[RequiresPermissionKey]` differs from the declared one. Use the `NotesPermissions` constants on both.
- **400 on every POST, the log says "AutoValidateAntiforgeryTokenFilter Failure".** The call did not send the
  antiforgery header: it does not go through `ServiceBase`, or JavaScript built it by hand.
- **The page shows nothing and the log shows a refused request.** `ThrowOnRefusal` is off, so the call returned null.
  Turn it on and show the message.
- **A validation error comes back as a bare 400 your UI does not show.** Since 10.73.2 the host answers an uncaught
  `FluentValidation.ValidationException` with a 400 `ValidationProblemDetails` (and a stale write with 409); catch it in the
  controller as above when the client should read the messages in your own shape. On an older Stomme it is a 500.
- **A server-rendered page works and the WebAssembly site does not (or the reverse).** One implementation is missing or
  misregistered: `ServerNotesService` with `AddTransient` in the server startup, `NotesService` behind the guard in the
  client startup, `INotesService` in `Shared`.
- **The WebAssembly site fails to load the module** after adding server types to `Client` or `Shared`. Keep controllers,
  handlers and anything from `Stomme.Server` in the `Server` project.
- **`404` from `/api/Notes` on a site served at a path.** The URL was built by hand without the path. Use
  `CreateApiUrl`.
- **An action that should be hidden while a switch is off answers 403 instead of 404.** Authorization runs before the
  action body. Decide it in routing (an `ActionMethodSelectorAttribute`), not with `return NotFound()` in the action.
