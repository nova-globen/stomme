# Identity and permissions

Users sign in with ASP.NET Identity, per tenant. What a signed-in user, or an anonymous visitor, may do on a site is
decided by **roles** and **permission rows** stored for that site. A module declares the permissions it enforces as
**permission keys** (`resource:operation`), and Nova Globen Stomme checks them on the server for every request, again in the
mediator, and in the UI as a pre-check.

## How it works

### Users

- A user is a `Stomme.Models.User` (`UserId`, `Username`, `DisplayName`, `Email`, `Roles`, `IsDeleted`, ...) backed by
  an ASP.NET Identity user in the tenant's database. A user account belongs to a tenant; it signs in to that tenant's
  sites.
- Sign-in issues the Identity cookie. Its principal carries the user id (`ClaimTypes.NameIdentifier`), the user name,
  the site key it was issued for, a security stamp and one `ClaimTypes.Role` claim per role the user holds on that
  site. On each request the cookie is re-validated against the stored user for the request's site: a deleted user, a
  changed security stamp or an ended server-side session signs the user out, and a cookie issued for a different site
  of the tenant has its roles re-read for this one.
- Site sign-in options (password rules, lockout, two-factor by e-mailed code, passkeys, login links, registration,
  cookie name and lifetime) are set by the Host in **Admin > User Management > Settings**.

### Roles

`Stomme.Shared.RoleNames`:

| Constant | Role name | Meaning |
|---|---|---|
| `RoleNames.Everyone` | `All Users` | Everyone, signed in or not. A pseudo-role: nobody is a member; permission rows name it. |
| `RoleNames.Unauthenticated` | `Unauthenticated Users` | Anonymous visitors only. A pseudo-role. |
| `RoleNames.Registered` | `Registered Users` | Assigned automatically to a user on a site. |
| `RoleNames.Admin` | `Administrators` | The site's administrators. |
| `RoleNames.Host` | `Host Users` | The installation's operators. A Host user also receives the `Administrators` and `Registered Users` claims on every site. |

Sites can add their own roles (**Admin > Role Management**). Role names are compared exactly (ordinal).

### Permission rows

A permission is a `Stomme.Models.Permission` row:
`(SiteId, EntityName, EntityId, PermissionName, RoleName or UserId, IsAuthorized)`. The decision
(`Stomme.Security.UserSecurity.IsAuthorized`) is **deny first**: if any row for the user (by user id or by one of
their roles, including `All Users` and, when anonymous, `Unauthenticated Users`) has `IsAuthorized = false`, the answer
is no; otherwise the answer is yes if any matching row grants. **No rows means no.**

Two kinds of permission use these rows:

1. **Framework entities** - pages and module placements (`EntityNames.Page`, `EntityNames.Module`) carry `View` and
   `Edit` rows (`Stomme.Shared.PermissionNames`) for one instance (`EntityId` = the page or module id). They are what
   the page's and a module's **Permissions** tab edit.
2. **Permission keys** - a module's own capabilities, written `resource:operation` in lower case
   (`notes:view`, `notes:manage`). A key is stored as rows with `EntityName = resource`, `EntityId = -1`,
   `PermissionName = operation`, per site. A key names a capability across the site, not one instance.

### Permission keys: declared once, seeded per site

A module declares its keys in its `ModuleDefinition.PermissionNames`, built with
`Stomme.Security.PermissionKeys.Declare(...)` (in the `Stomme.Shared` package):

- `Declare(IEnumerable<(string Key, string[] Roles)>)` - each key with the roles a new site grants it to.
- `Declare(IEnumerable<string> keys, params string[] defaultRoles)` - every key granted to the same roles.

`Declare` lower-cases each key, rejects a malformed one (not exactly two non-empty segments, a segment over 50
characters, whitespace or a separator, or a resource that is one of the `Stomme.Shared.EntityNames` constants,
compared case-insensitively) with `ArgumentException`, always adds `Administrators` to every key's roles, and puts
`View,Edit` first in the string. Write the declaration only through `Declare`: a two-segment key typed directly into
`PermissionNames` is ignored.

At startup Stomme seeds the declared keys' rows on every site that has none, granted to the declared roles. A site
created at runtime receives them the first time its module definitions are read. After that the site owns its rows: an
Administrator changes who holds a key in **Admin > Module Management**, on the module's **Permissions** tab. Clearing a
key there writes an `Administrators` deny row (so the key is held by nobody and is not re-seeded).

### Where a key is checked

| Where | How | Namespace / package |
|---|---|---|
| A controller action or a whole controller | `[RequiresPermissionKey("notes:manage")]` - an ASP.NET authorization policy; runs before the action | `Stomme.Infrastructure.Security` (`Stomme.Server`) |
| A command or query | `[Requires("notes:manage")]` on the request TYPE; the mediator's authorization behaviour checks it before any handler runs and throws `AccessDeniedException`, which a controller turns into **403** `application/problem+json` | `Stomme.Security.Abstractions` |
| Inside a handler or service, for a decision that depends on data | `await accessGuard.IsAllowedAsync(feature: null, permission: "notes:manage", ct)` on `IAccessGuard` | `Stomme.Security.Abstractions` |
| A Razor component (UI pre-check only) | `IsAuthorizedPermission("notes:manage")` on `ModuleBase` | `Stomme.Modules` (`Stomme.Client`) |
| A job deciding for a user who is not present | `IStoredRowPermissionEvaluator.IsAuthorizedAsync(siteId, userId, key, ct)` - from stored roles only | `Stomme.Infrastructure.Security` (`Stomme.Server`) |

All of them read the same rows of the request's site (the job evaluator: the site you pass). The server-side decision:

- reads the site from the request's alias; with **no `HttpContext`** (a hosted job, a headless caller) every
  requirement is **denied** - there is nobody to decide for;
- denies a malformed key and a key with no rows on the site, administrators included - there is no Host or
  Administrator short-circuit; the Administrators row seeded by `Declare` is what admits them;
- treats `system:admin` as the one key that is not rows: it means membership of `Host Users`
  (`PermissionRowAccessGuard.SystemAdminKey`);
- with `[Requires(Feature = "...")]`, also requires the feature flag to be on (and denies when no feature evaluator is
  registered).

`[Requires]` with a `Feature` and a `Permission` requires both. Several `[Requires]` on one type are all required, and a
class-level and a method-level requirement are combined.

### Who is calling

`Stomme.Security.Abstractions.ICurrentUser` (scoped) gives `UserId` (`int?`), `UserName`, `IsAuthenticated`, `Roles`
(a case-insensitive set) and `SiteId` (`-1` without a resolved site). It answers WHO; it has no permission check on
purpose - ask `IAccessGuard`. Outside a request it is anonymous.

### Module placements: the instance-scoped policies

Older modules, and the framework's own HtmlText module, scope access to ONE placement of the module: the controller
carries `[Authorize(Policy = PolicyNames.ViewModule)]` or `[Authorize(Policy = PolicyNames.EditModule)]`
(`Stomme.Shared.PolicyNames`, marked deprecated in favour of the dynamic form
`EntityNames.Module + ":" + PermissionNames.Edit + ":" + RoleNames.Admin`), the client adds `?authmoduleid={moduleId}`
to the URL (`ServiceBase.CreateAuthorizationPolicyUrl(url, EntityNames.Module, moduleId)`), and the policy checks the
user's `View` or `Edit` on THAT module id. Because the caller chooses `authmoduleid`, the action body must check that
the row it touches belongs to that module: `ModuleControllerBase.IsAuthorizedEntityId(EntityNames.Module, row.ModuleId)`.
For a new module prefer permission keys; use the instance policies when a module's data really belongs to one
placement.

## Using it from an app

### Declare the keys (Shared)

The reference Notes module's keys (`Shared/Modules/Notes/NotesPermissions.cs`):

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

In the module's `ModuleInfo` (Client project), set
`PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations)` (`using Stomme.Security;`). If the module
package has a second module definition that uses the same keys, it must declare them too: a placement only carries the
key rows its own definition declares, and `IsAuthorizedPermission` on that placement answers no otherwise. The keys then
appear as columns (operation and resource) beside "Utilize Module" on the module's **Permissions** tab in
**Admin > Module Management**.

### Gate the endpoint and the request (Server)

From `Server/Modules/Notes/Controllers/NotesController.cs` (the whole controller is in
[stomme-add-api-endpoint](../../.claude/skills/stomme-add-api-endpoint/SKILL.md)):

```csharp
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Application;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Mediator;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Application.Modules.Notes.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class NotesController : ModuleControllerBase
    {
        private readonly IMediator _mediator;

        public NotesController(IMediator mediator, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _mediator = mediator;
        }

        // GET api/Notes/{id}
        [HttpGet("{id:guid}")]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
            await _mediator.Send(new GetNoteQuery(id), ct) is { } note ? Ok(note) : NotFound();
    }
}
```

The `GetNoteQuery` type carries `[Requires(NotesPermissions.View)]`, and its handler returns null for an unknown id or
another site's note, which the action answers with 404. The full module, every file, is in the
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) skill; what makes it secure is explained in
[stomme-secure-a-module](../../.claude/skills/stomme-secure-a-module/SKILL.md).

### A decision that depends on the row

From `NotesScope` in `Server/Modules/Notes/Application/NoteRequests.cs`, called by the edit and delete handlers after
they loaded the site's note:

```csharp
// [Requires] decided the key; the ROW decides the rest: the author, or a holder of notes:manage.
public static async Task EnsureMayChangeAsync(Note note, ICurrentUser currentUser, IAccessGuard accessGuard, CancellationToken ct)
{
    var isAuthor = currentUser.UserId is int userId && note.AuthorUserId == userId;
    if (!isAuthor && !await accessGuard.IsAllowedAsync(null, NotesPermissions.Manage, ct))
    {
        throw new AccessDeniedException("Only the note's author or a notes manager may change it.");
    }
}
```

`ICurrentUser`, `IAccessGuard` and `AccessDeniedException` are in `Stomme.Security.Abstractions`. A Registered User
who calls `PUT api/Notes/{id}` on someone else's note gets 403 `application/problem+json` with that message as `detail`.

### Check in the UI

From `Client/Modules/Notes/Index.razor`:

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
```

`IsAuthorizedPermission(key)` decides from the placement's own permission list in the browser: no server call. It hides
what the user cannot use; the server decides again on every call. `IsAuthorizedRole(RoleNames.Admin)` checks a role.

### Roles in API attributes

`[Authorize(Roles = RoleNames.Admin)]` and `[Authorize(Roles = RoleNames.Host)]` work on any controller: roles are
claims on the principal. Prefer a permission key for a module's own capabilities, so an Administrator can grant it to
another role without a code change.

## Configuration

Sign-in options are site settings the Host sets in **Admin > User Management > Settings** - among them
`LoginOptions:TwoFactor` (`false`, `true` = optional, `required`; the code is sent by e-mail, so configure mail first),
`LoginOptions:Passkeys`, `LoginOptions:AllowSiteLogin`, `IdentityOptions:Password:*` and
`IdentityOptions:Lockout:MaxFailedAccessAttempts` / `DefaultLockoutTimeSpan`. These are per-site settings, not
`appsettings.json` keys.

Account endpoints are rate limited by default, per site and client address:

| Key | Default | Effect |
|---|---|---|
| `RateLimiting:Account:SignIn:PermitLimit` | `10` | Sign-in attempts per window |
| `RateLimiting:Account:SignIn:WindowSeconds` | `60` | The sign-in window |
| `RateLimiting:Account:Email:PermitLimit` | `5` | E-mail-sending account requests (and anonymous registration) per window |
| `RateLimiting:Account:Email:WindowSeconds` | `300` | That window |

## Rules and pitfalls

- **Gate every action twice.** `[RequiresPermissionKey]` on the action and `[Requires]` on the command or query. The
  endpoint gate protects the HTTP surface; the request gate protects every other caller (a server-side service on an
  Interactive Server page, an MCP tool, a pipeline step).
- **`[Requires]` on a handler class or method is never enforced.** Only attributes on the request type are read.
- **A class-level `[AllowAnonymous]` switches off `[RequiresPermissionKey]`** on every action of that controller - the
  authorization middleware stops at it. The mediator's `[Requires]` still denies the commands behind it, so testing a
  command-backed action "proves" a gate that is missing from the others.
- **A config-switched controller cannot answer 404 from its action body.** `[RequiresPermissionKey]` runs before the
  body, so a caller without the key gets 403 and never reaches `if (!enabled) return NotFound();`. Use an
  `ActionMethodSelectorAttribute` so the router itself finds no action (see
  [security-posture.md](security-posture.md)).
- **No rows means denied, for everyone.** A key nobody declared, a key misspelled in one place, or a second module
  definition that uses keys it does not declare, denies administrators too. Use the same constants everywhere.
- **The UI check is not security.** `IsAuthorizedPermission` only hides controls; every endpoint and request must
  check on the server.
- **Jobs have no principal.** `IAccessGuard`, `[Requires]` and `[RequiresPermissionKey]` deny in a hosted job. A job
  acting for a user re-checks with `IStoredRowPermissionEvaluator` (registered by the framework since 10.79.0) - after
  entering that user's site (the role cache is keyed by alias).
- **Never reuse a key's resource as the `EntityName` of per-instance permission rows.** Saving instance rows for an
  `(EntityName, PermissionName)` pair deletes the key rows of that pair.
- **Keys are lower case.** Equality is case-sensitive on some databases and not on others; `Declare` and the guard both
  lower-case, so keep constants lower case and do not build keys from user input.

## Related

- [tenancy.md](tenancy.md) - which site a request belongs to
- [authentication-options.md](authentication-options.md) - external logins and the OpenID Connect provider
- [security-posture.md](security-posture.md) - fail closed, secrets, egress, antiforgery
- [../modules/api-and-client-services.md](../modules/api-and-client-services.md) - controllers and client services
- [../modules/application-layer.md](../modules/application-layer.md) - commands, queries and the mediator
- [../modules/module-ui.md](../modules/module-ui.md) - `ModuleBase` and module actions
- [../../.claude/skills/stomme-secure-a-module/SKILL.md](../../.claude/skills/stomme-secure-a-module/SKILL.md)
