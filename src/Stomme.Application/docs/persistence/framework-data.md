# Framework data

Nova Globen Stomme keeps its own data - tenants, aliases, sites, pages, modules, users, roles, permissions, settings, files,
folders, logs, notifications - in stores of its own. An app reads and changes that data through Stomme's services and
repositories, never through a database context. This page lists what there is and which surface to use from where.

## How it works

Two databases hold the framework's data:

- The **master** database is installation-wide. It holds the tenants, the aliases (host names and paths), the module
  definitions, the themes, the scheduled jobs and their logs, and the settings whose entity is `Host`, `Job`,
  `ModuleDefinition` or `Theme`.
- Each **tenant** database holds that tenant's sites, pages, page modules, modules, users, profiles, roles, user roles,
  permissions, languages, logs, notifications, site tasks and all other settings. Several tenants can share one
  database; each can also have its own.

A request resolves its alias from the host name and path, and the alias names the site and the tenant (see
[tenancy](../security/tenancy.md)). Every framework repository then reads the current tenant's database for the request.

The framework's tables are not EF Core `DbSet`s an app can query. They sit behind Stomme's own persistence layer, which
has an EF Core implementation and a RavenDB implementation, chosen by the installation. Caching, tenancy, audit fields
and the case-normalised columns that make lookups agree across databases all live in that layer. A `DbContext` of your
own over those tables would work on one database, return stale data against the cache, and fail on a RavenDB
installation.

There are two surfaces, and which one you use depends on where your code runs:

| Code | Use | Why |
|---|---|---|
| A Razor component (a module's page or control, in `Client`) | The client services, `I…Service` in `Stomme.Services` | The component runs in the browser on a WebAssembly site; the service calls the site's API over HTTP, and the API authorizes the call. |
| Server code: a mediator handler, a controller, a background job, a module startup | The repositories, `I…Repository` in `Stomme.Repository` | They read the store directly. They do NOT check permissions - the calling code must. |

The client services are also registered on the server, so a server-rendered component works the same way. Most of them
call the site's own API over HTTP even on the server, carrying the current request's cookies; inside a background job
there is no request, and such a call has nothing to authenticate with. Server code therefore uses the repositories.

## Using it from an app

### In a component

`ModuleBase` gives a module's component the current state, already loaded (see [module UI](../modules/module-ui.md)):

- `PageState.Alias`, `PageState.Site`, `PageState.Page`, `PageState.User` (null when anonymous), `PageState.Modules`
  (`Stomme.UI.PageState`, models in `Stomme.Models`).
- `PageState.Site.Settings` - the site's settings as a `Dictionary<string, string>`.
- `ModuleState` - the placed module instance (`Stomme.Models.Module`), with `ModuleId`.

For anything else, inject the client service. The main ones (all in `Stomme.Services`, package `Stomme.Client`):

| Service | Covers |
|---|---|
| `ISiteService` | `GetSitesAsync`, `GetSiteAsync(siteId)`, `AddSiteAsync`, `UpdateSiteAsync`, `DeleteSiteAsync` |
| `IPageService` | `GetPagesAsync(siteId)`, `GetPageAsync(pageId)`, `GetPageAsync(path, siteId)`, add, update, delete, copy, order |
| `IModuleService`, `IPageModuleService` | Module instances and their placement on pages |
| `IUserService` | `GetUserAsync(userId, siteId)`, `GetUserAsync(username, siteId)`, add, update, delete, sign-in flows |
| `IRoleService`, `IUserRoleService` | Roles of a site; `GetUserRolesAsync(siteId, userId)`, `GetUserRolesAsync(siteId, roleName)` |
| `ISettingService` | Settings per entity: `GetSiteSettingsAsync(siteId)`, `GetModuleSettingsAsync(moduleId)`, `GetPageModuleSettingsAsync(pageModuleId)`, `GetUserSettingsAsync(userId)`, `GetSettingsAsync(entityName, entityId)`, the matching `Update…Async`, and `GetSetting(settings, name, default)` |
| `IFileService`, `IFolderService` | Files and folders - see [files and folders](../services/files-and-folders.md) |
| `INotificationService` | Notifications - see [notifications](../services/notifications.md) |
| `ILogService` | The site's event log |
| `IAliasService`, `ITenantService`, `ILanguageService`, `IProfileService` | Aliases, tenants, languages, profile fields |

```razor
@namespace Stomme.Application.Modules.Notes
@inherits ModuleBase
@inject ISettingService SettingService

<p>@_greeting</p>

@code {
    private string _greeting = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        var settings = await SettingService.GetModuleSettingsAsync(ModuleState.ModuleId);
        _greeting = SettingService.GetSetting(settings, "Greeting", "Hello");
    }
}
```

(`ModuleBase`, `ISettingService` and `Task` resolve through the usings in `Client/_Imports.razor`.)

### In server code

The repositories are in `Stomme.Repository` (package `Stomme.Server`) and are registered for injection:

| Repository | Covers |
|---|---|
| `ISiteRepository` | `GetSitesAsync`, `GetSiteAsync(siteId)`, `UpdateSiteAsync` … |
| `IPageRepository` | `GetPagesAsync(siteId)`, `GetPageAsync(pageId)`, `GetPageAsync(path, siteId)` … |
| `IUserRepository` | `GetUserAsync(userId)`, `GetUserAsync(username)`, `GetUserAsync(username, email)` … |
| `IRoleRepository`, `IUserRoleRepository` | `GetUserRolesAsync(userId, siteId)`, `GetUserRolesAsync(roleName, siteId)` … |
| `ISettingRepository` | `GetSettingsAsync(entityName, entityId)`, `GetSettingValueAsync(entityName, entityId, settingName, defaultValue)`, `AddSettingAsync`, `UpdateSettingAsync`, `DeleteSettingAsync` |
| `IFileRepository`, `IFolderRepository` | Files and folders |

Entity names for settings are the constants of `Stomme.Shared.EntityNames` (`Site`, `Page`, `Module`, `User`,
`ModuleDefinition`, `Host`, …). Role names are the constants of `Stomme.Shared.RoleNames`: `Admin` ("Administrators"),
`Host` ("Host Users"), `Registered` ("Registered Users"), `Everyone` ("All Users"), `Unauthenticated`
("Unauthenticated Users").

Who is asking, and where:

- `Stomme.Shared.SiteState` (scoped): `Alias.SiteId`, `Alias.TenantId`, `Alias.Path`.
- `Stomme.Security.Abstractions.ICurrentUser` (scoped): `UserId` (null when anonymous), `UserName`, `Roles`,
  `IsAuthenticated`, `SiteId` (-1 when no site is resolved).
- `Stomme.Security.Abstractions.IAccessGuard`: `IsAllowedAsync(null, "notes:edit")` answers whether the current user
  holds a permission key on the current site (see [identity and permissions](../security/identity-and-permissions.md)).

A handler that tells a site's administrators about something:

```csharp
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Application
{
    public sealed class SiteOperators(IUserRoleRepository userRoles)
    {
        /// <summary>The site's Administrators plus the Host Users, who administer every site.</summary>
        public async Task<IReadOnlyList<int>> UserIdsAsync(int siteId) =>
            (await userRoles.GetUserRolesAsync(RoleNames.Admin, siteId))
                .Concat(await userRoles.GetUserRolesAsync(RoleNames.Host, siteId))
                .Select(role => role.UserId)
                .Distinct()
                .ToList();
    }
}
```

Register it in the module's startup (`services.AddScoped<SiteOperators>();`). Note why the Host Users are included: on a
new installation the site's only user is the host, whose only stored role is `Host Users`. (Signed in, the host's
`ICurrentUser.Roles` also lists `Administrators` and `Registered Users` - the sign-in adds them - but the user-role rows a
repository reads hold `Host Users` alone.)

## Rules and pitfalls

- **Never query the framework's tables.** No `DbContext` over them, no SQL against them, no `TenantDBContext`: it maps
  only migration history now, and the framework data is not in it. Use the services and repositories above.
- **Repositories do not authorize.** A controller or handler that reads `IUserRepository` for a user id taken from the
  request hands any caller any user. Check the permission first (`[Requires]` on the request, `[RequiresPermissionKey]`
  on the action, or `IAccessGuard` for a row-level decision) - see
  [API and client services](../modules/api-and-client-services.md).
- **Stay on the request's site.** Take the site id from `SiteState.Alias.SiteId`, not from the request body or query
  string, unless you then check the caller's right to that site.
- **Do not call a client service from a background job.** It goes over HTTP with the request's cookies, and a job has
  no request. Use the repository; see [jobs](../services/jobs.md) for setting the site a job works on.
- **Keep your own data in your own aggregates.** Settings are for configuration values, not for records; a module's
  records are aggregates (see [module persistence](module-persistence.md)).

## Related

- [Module persistence](module-persistence.md) - storing a module's own data.
- [API and client services](../modules/api-and-client-services.md) - the controller and client-service chain.
- [Module UI](../modules/module-ui.md) - `ModuleBase`, `PageState`, `ModuleState`.
- [Settings and templates](../modules/settings-and-templates.md) - a module's settings.
- [Tenancy](../security/tenancy.md), [identity and permissions](../security/identity-and-permissions.md).
- [Files and folders](../services/files-and-folders.md), [notifications](../services/notifications.md),
  [jobs](../services/jobs.md).
