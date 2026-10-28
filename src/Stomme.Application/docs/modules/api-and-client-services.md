# API and client services

A module's components never touch the server's data directly. They call a service interface; in the browser that
interface is implemented by an HTTP client calling the module's API controller, and on the server by a class that sends
the same commands and queries through the mediator. This page covers the chain - shared contract, controller, the two
service implementations, their registration - and the authorization and antiforgery rules every endpoint follows.

## How it works

The chain for one module (here, Notes):

| Project | Type | Role |
|---|---|---|
| `Shared` | `NoteModel`, `SaveNoteRequest` | The wire models: what crosses HTTP. Records or plain classes; never the aggregate. |
| `Shared` | `NotesPermissions` | The module's permission keys, as `const string`. |
| `Shared` | `INotesService` | The contract a component consumes. |
| `Server` | `NotesController` | `api/Notes`: authorizes, sends a command or query through `IMediator`, maps exceptions to status codes. |
| `Server` | `ServerNotesService : INotesService` | The server's implementation: sends the same commands and queries through `IMediator`. |
| `Client` | `NotesService : ServiceBase, INotesService` | The browser's implementation: HTTP calls to `api/Notes`. |
| `Client` | `NotesClientStartup : IClientStartup` | Registers `NotesService` when nothing else has registered `INotesService`. |

Which implementation a component gets:

- **WebAssembly site, in the browser:** the browser runs only the `Client` and `Shared` assemblies, so `INotesService` is
  `NotesService`, and every call goes over HTTP to the controller.
- **Static or Interactive Server site, on the server:** the host runs every assembly's `IServerStartup` and
  `IClientStartup` against one service collection. The module's server startup registers `ServerNotesService` with
  `AddTransient`; the client startup registers `NotesService` only if `INotesService` is not registered yet. Whichever
  startup runs first, the server implementation is the one resolved, and a component's call goes straight to the
  mediator without an HTTP round trip.

So a component's call reaches a handler by one of two roads, and the command's `[Requires]` is the check both roads
pass through. The controller's own authorization only guards the HTTP road.

### Controllers

- Derive from `ModuleControllerBase` (`Stomme.Controllers`, package `Stomme.Server`). Its constructor takes
  `ILogManager` (`Stomme.Infrastructure`) and `IHttpContextAccessor`.
- Route with `[Route(ControllerRoutes.ApiRoute)]` (`Stomme.Shared.ControllerRoutes.ApiRoute` is `"api/[controller]"`).
  A site served at a path (`example.com/shop`) is called at `/shop/api/Notes`; the host strips the alias path before
  routing, and `ServiceBase.CreateApiUrl` adds it on the client.
- The host discovers controllers in the app's assemblies; nothing to register.

### Authorization on an endpoint

Every action carries an authorization attribute; there is no default that protects an unmarked action.

- **A permission key**: `[RequiresPermissionKey("notes:edit")]` (`Stomme.Infrastructure.Security`). An ordinary
  ASP.NET Core authorization policy, decided by the site's permission rows for the key - the same rows the request's
  `[Requires]` reads, so the endpoint and the handler cannot disagree. A caller without the key - an anonymous one
  included - is refused with 403 before the action runs.
- **The permission on one placed module instance** (data that belongs to one placement of the module):
  `[Authorize(Policy = "Module:Edit:Administrators")]` - `EntityNames.Module`, `PermissionNames.Edit` or
  `PermissionNames.View`, and a fallback role - with the module id in the query string as `authmoduleid`. The client
  builds that URL with `CreateAuthorizationPolicyUrl(url, EntityNames.Module, moduleId)`, and the action must check
  that the id it acts on is the one authorized, with `IsAuthorizedEntityId(EntityNames.Module, moduleId)`.
- **Public data**: `[AllowAnonymous]` on that action only. On the controller class it switches off every
  `[RequiresPermissionKey]` in the class.

The model behind these - keys, roles, the declaration in `ModuleInfo` - is in
[identity and permissions](../security/identity-and-permissions.md).

A `[Requires]`-denied mediator request that escapes an action becomes a 403 `ProblemDetails` response through a filter
the host registers for every controller.

### Antiforgery

The host validates an antiforgery token on every POST, PUT, DELETE and other non-GET/HEAD/OPTIONS/TRACE request to a
controller. It accepts the token in the header `X-XSRF-TOKEN-HEADER` (`Stomme.Shared.Constants.AntiForgeryTokenHeaderName`)
or, for a form post, in the form field `__RequestVerificationToken`. The value is `SiteState.AntiForgeryToken`, which
the host puts into every page it renders. A request that fails the check gets 400 and the host logs
"AutoValidateAntiforgeryTokenFilter Failure For {url}".

- A client service deriving from `ServiceBase` adds the header to every call; nothing to do.
- A request carrying an `Authorization` header (a bearer token) is not checked for antiforgery - unless its sign-in cookie
  also signs it in, in which case it is checked like any other (since 10.73.2).
- Code that calls the API by other means - JavaScript `fetch`, a hand-made `HttpClient` - must send the header itself.

### ServiceBase

`ServiceBase` (`Stomme.Services`, package `Stomme.Client`) is the base class of a client service:

| Member | Use |
|---|---|
| constructor `(HttpClient http, SiteState siteState)` | The form to use. |
| `CreateApiUrl("Notes")` | `/api/Notes`, or `/{alias path}/api/Notes` on a site served at a path. |
| `CreateAuthorizationPolicyUrl(url, entityName, entityId)` | Adds `auth{entity}id=` to a URL for an instance policy. |
| `GetJsonAsync<T>(url)`, `PostJsonAsync<TValue, TResult>(url, value)`, `PutJsonAsync<TValue, TResult>(url, value)`, `DeleteAsync(url)`, `PostAsync(url)`, `PutAsync(url)` | The calls. Protected. |
| `ThrowOnRefusal` | Protected virtual, `false` by default. |

By default a refused call (any status that is not success) is logged to the site's log - except a 404 - and the call
returns the type's default - `null` for an object. Override `ThrowOnRefusal` to `true` and a refused call throws
`Stomme.Services.ServiceRefusalException` instead, whose `Message` is the server's reason (an `{"error": "..."}` body,
the first validation error, or a problem's detail or title; with none of those, "The request was refused (403
Forbidden)." and the like) and whose `StatusCode` is the status. A GET answered 404 still
returns the default. A component can then show the same message on every render mode, since the server implementation
throws too.

A successful response must be `application/json`; anything else is treated as no result.

## Using it from an app

The complete steps, with every file, are in the
[stomme-add-api-endpoint](../../.claude/skills/stomme-add-api-endpoint/SKILL.md) skill. The essentials:

The controller of the reference Notes module (`Server/Modules/Notes/Controllers/NotesController.cs`):

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

The client service (`Client/Modules/Notes/Services/NotesService.cs`):

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Services;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Services
{
    /// <summary>INotesService over HTTP, for the browser. The server decides every permission again.</summary>
    public class NotesService : ServiceBase, INotesService
    {
        public NotesService(HttpClient http, SiteState siteState) : base(http, siteState)
        {
        }

        // A refused call throws ServiceRefusalException with the server's reason, as the server implementation throws.
        protected override bool ThrowOnRefusal => true;

        private string ApiUrl => CreateApiUrl("Notes");

        public async Task<List<NoteModel>> ListAsync() =>
            await GetJsonAsync<List<NoteModel>>(ApiUrl) ?? new List<NoteModel>();

        public async Task<NoteModel?> GetAsync(Guid noteId) =>
            await GetJsonAsync<NoteModel?>($"{ApiUrl}/{noteId}");

        public async Task<NoteModel> CreateAsync(SaveNoteRequest request) =>
            await PostJsonAsync<SaveNoteRequest, NoteModel>(ApiUrl, request);

        public async Task<NoteModel> EditAsync(Guid noteId, SaveNoteRequest request) =>
            await PutJsonAsync<SaveNoteRequest, NoteModel>($"{ApiUrl}/{noteId}", request);

        public async Task DeleteAsync(Guid noteId) =>
            await DeleteAsync($"{ApiUrl}/{noteId}");
    }
}
```

The two registrations:

```csharp
// Server: the module's IServerStartup.ConfigureServices (NotesServerStartup), as its last line
services.AddTransient<INotesService, ServerNotesService>();

// Client: the module's IClientStartup.ConfigureServices (NotesClientStartup; needs using System.Linq;)
if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotesService)))
{
    services.AddScoped<INotesService, NotesService>();
}
```

## Rules and pitfalls

- **Components use the service interface, never a repository or the mediator.** The same component runs in the
  browser on a WebAssembly site, where there is no repository and no mediator.
- **Keep `Client` and `Shared` free of server types.** They are downloaded and loaded by the browser. The interface and
  the wire models go in `Shared`; the server implementation goes in `Server`.
- **Guard the client registration, and register the server one with `AddTransient`.** Without the guard the order in
  which the startups run decides which implementation a server-rendered page gets, and a page that calls over HTTP from
  the server breaks wherever there is no request to borrow cookies from.
- **Authorize twice.** `[RequiresPermissionKey]` on the action and `[Requires]` on the request. The action's attribute
  stops an unauthorized HTTP caller before any work; the request's attribute is the only check on the server road.
- **Never put `[AllowAnonymous]` on a controller class** that has gated actions; it disables every
  `[RequiresPermissionKey]` in it, while a test of a dispatching action still sees 403 from the mediator and looks fine.
- **Do not check a "config switch" inside the action.** An authorization policy runs before the action body, so a
  caller without the key gets 403 and never reaches `if (!enabled) return NotFound();`. To hide an endpoint when a
  switch is off, decide it in routing: an `ActionMethodSelectorAttribute` (ASP.NET Core) that returns false makes the
  router answer 404. A feature flag in the request's `[Requires(Feature = ...)]` refuses with 403 instead.
- **Catch `FluentValidation.ValidationException` in the controller.** Nothing in the host maps it; uncaught, it is a
  500 with a generic error page.
- **Return JSON for every result a client service reads.** `Ok(model)` does. A string or an empty 200 is read as no
  result.
- **With `ThrowOnRefusal` off, a refused call returns null.** A page that does not check for it shows nothing, or fails
  later with a `NullReferenceException` far from the cause. Turn it on in new services.
- **Take the site from the server, not from the client.** The controller and the handlers use `SiteState.Alias.SiteId`;
  a site or tenant id in a request body is ignored or checked, never trusted.
- **A 400 from a POST that "should work" is usually antiforgery.** Look for the `AutoValidateAntiforgeryTokenFilter
  Failure` line in the log, and check that the call goes through `ServiceBase` or sends `X-XSRF-TOKEN-HEADER`.

## Related

- [The application layer](application-layer.md) - the commands and queries the controller sends.
- [Module persistence](../persistence/module-persistence.md) - where the handlers keep data.
- [Module UI](module-ui.md) - the component that consumes the service.
- [Module anatomy](anatomy.md) - the Client/Server/Shared split and `ModuleInfo`.
- [Identity and permissions](../security/identity-and-permissions.md) - permission keys and policies.
- [Security posture](../security/security-posture.md) - fail closed, antiforgery.
- [Framework data](../persistence/framework-data.md) - the framework's own services.
- Skills: [stomme-add-api-endpoint](../../.claude/skills/stomme-add-api-endpoint/SKILL.md),
  [stomme-add-command-or-query](../../.claude/skills/stomme-add-command-or-query/SKILL.md),
  [stomme-secure-a-module](../../.claude/skills/stomme-secure-a-module/SKILL.md).
