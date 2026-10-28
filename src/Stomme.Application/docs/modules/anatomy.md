# Module anatomy

A module is a feature that an administrator places on a page: a list of notes, a knowledge base, a booking form. In code it
is a set of types spread over the app's three projects (`Client`, `Server`, `Shared`) that Nova Globen Stomme finds by convention at
startup - you never register a module with the framework by hand. This page explains what a module is made of, how the host
discovers it, and how it reaches a page.

## How it works

### Module type, module instance, placement, page, container

Stomme separates five things. Keep them apart when you read the API:

1. **Module definition** (`Stomme.Models.ModuleDefinition`) - the module TYPE, one per module in the installation. Your
   code declares it through a class that implements `Stomme.Modules.IModule` (conventionally called `ModuleInfo`). Its key
   is `ModuleDefinitionName`, which the host computes as `"{namespace}, {assembly name}"` (see Discovery below).
2. **Module instance** (`Stomme.Models.Module`, `ModuleId`) - one occurrence of the module on a site, with its own
   settings (`Module.Settings`) and its own permission rows (`Module.PermissionList`). Your data is normally keyed by site,
   and by module instance when two placements must hold separate data.
3. **Placement** (`Stomme.Models.PageModule`, `PageModuleId`) - an instance shown on a page: the page, the pane, the order,
   the title and the container. One instance can be placed on several pages.
4. **Page** (`Stomme.Models.Page`) - a URL path on a site with a theme, panes and its own View/Edit permissions.
5. **Container** - the chrome a theme draws around each placement (title, actions menu). Containers belong to themes; see
   [Themes](../ui/themes.md).

Inside a component that inherits `Stomme.Modules.ModuleBase`, the placed instance is `ModuleState` (a
`Stomme.Models.Module` that also carries the placement's `PageModuleId`, `Title`, `Pane`, `Order` and the
`ModuleDefinition`). See [Module UI](module-ui.md).

### The three projects of a module

A module's code lives in three assemblies because a Blazor WebAssembly browser can only load assemblies that contain no
server code.

| Project | Holds | Runs |
| --- | --- | --- |
| `Shared` | Wire models (records sent over HTTP), the permission keys, the service contract (`INotesService`) | Server and browser |
| `Client` | `ModuleInfo` (the `IModule`), the Razor components (`ModuleBase`), the HTTP implementation of the service, an `IClientStartup` | Server and browser |
| `Server` | The aggregate and its persistence registration, commands, queries and their handlers, the API controller, the server implementation of the service, the server manager, an `IServerStartup` | Server only |

- `Client` references only `Shared` and the `Stomme.Client` package. Anything that needs ASP.NET Core server types, EF Core,
  the mediator handlers or the database goes in `Server`.
- `Shared` references only the `Stomme.Shared` package.
- `Server` references both, plus the `Stomme.Server` package.

In the app the template made, these are the app's own projects:
`Client/Stomme.Application.Client.csproj`, `Server/Stomme.Application.Server.csproj` and
`Shared/Stomme.Application.Shared.csproj`. Every module of the app lives inside them, in a folder of its own
(`Client/Modules/Notes`, `Server/Modules/Notes`, `Shared/Modules/Notes`). A module can instead be a separate set of three
projects packed as a NuGet package; see "In the app or as a package" below.

### Discovery

At startup the host does this, in order:

1. Unpacks every `*.nupkg` in the content root's `Packages` folder into the bin folder (`lib/net*/` files go to bin,
   `wwwroot/` to the web root, `staticwebassets/` to `wwwroot/_content/{package id}`), then deletes the package. A package
   whose nuspec declares a `Stomme.Framework` package type with a version newer than the running framework is skipped.
2. Loads every DLL in the bin folder whose FILE NAME contains `Stomme` (case-insensitive). This is why every assembly of a
   module, and every project of the app, has an assembly name ending in `.Stomme`:
   `Stomme.Application.Server.Stomme`, `Stomme.Application.Client.Stomme`, `Stomme.Application.Shared.Stomme`.
   An assembly without `Stomme` in its name is never scanned: its startups, services, controllers and modules do not exist
   for the host.
3. For every loaded assembly whose full name contains `Stomme` (except framework libraries that opt out):
   - registers each class implementing `Stomme.Modules.IService` as scoped, and each `Stomme.Modules.ITransientService`
     as transient - under the interface named `I{ClassName}` ONLY when that interface is in the same namespace and the same
     assembly; otherwise under the class itself;
   - registers each `Microsoft.Extensions.Hosting.IHostedService` as a singleton;
   - creates each `Stomme.Infrastructure.IServerStartup` and `Stomme.Services.IClientStartup` with its parameterless
     constructor and calls `ConfigureServices(services)`.
4. Adds every loaded `Stomme` assembly that contains a controller as an MVC application part, so controllers in your
   `Server` project are routed.
5. Builds the module definitions. For each assembly that is a file in the bin folder, it finds every type implementing
   `Stomme.Modules.IModuleControl` (every component that inherits `ModuleBase` does). The **namespace** of a control plus
   the assembly name is the module's `ModuleDefinitionName`. The `IModule` class in the **same namespace and assembly**
   supplies the definition's properties. So:
   - all the components of one module share one namespace with its `ModuleInfo`;
   - a second namespace in the same `Client` assembly is a second module;
   - a component whose namespace has no `IModule` still becomes a module, named after the last namespace segment (or the
     `Title` of its `Index` component);
   - an `IModule` with no components becomes a "Headless" module that only Host users can place.
6. Syncs the definitions to the installation's database and, per site, places modules that declare page templates (see
   [Settings and templates](settings-and-templates.md)).

The host then also fills these properties itself - do not set them: `ModuleDefinitionName`, `ControlTypeTemplate`
(`"{namespace}.{Action}, {assembly}"`), `ControlTypeRoutes` (from each component's `Actions`), `AssemblyName` (the
assembly the components are in), `IsPortable` (true when `ServerManagerType` names a type implementing
`Stomme.Modules.IPortable`) and `PermissionList`.

### The definition: `IModule` and `ModuleDefinition`

`Stomme.Modules.IModule` (in the `Stomme.Client` package) has one member: `ModuleDefinition ModuleDefinition { get; }`.
The properties a module sets on `Stomme.Models.ModuleDefinition`:

| Property | Meaning |
| --- | --- |
| `Name` | The display name in the admin UI. Also the source of the feature key `module.<id>.enabled` (lower-cased, spaces removed). Once declared, a component's `Title` never overrides it. |
| `Description` | Shown in the admin dialogs. |
| `Version` | The module's version, for example `"1.0.0"`. |
| `ReleaseVersions` | Comma-separated versions, for example `"1.0.0,1.1.0"`, that drive upgrade hooks. See [Portable, searchable, migratable](portable-searchable-migratable.md). |
| `Categories` | The group the module is listed under when an administrator adds one to a page. Empty means `Common`. |
| `Owner`, `Url`, `Contact`, `License` | Shown in Admin > Module Management. |
| `PermissionNames` | The permission names and KEYS the module uses, built with `Stomme.Security.PermissionKeys.Declare(...)`. See below. |
| `ServerManagerType` | Assembly-qualified NAME (a string, never a `typeof`) of the server class the host asks for search content, sitemap entries, content export/import and upgrades. |
| `SettingsType` | Assembly-qualified name of the per-placement settings component. |
| `ConfigSettingsType` | Assembly-qualified name of the module-wide (installation) configuration component. |
| `PageTemplates` | Pages the host creates on every site and places this module on. |
| `Dependencies` | Comma-separated assembly names (without `.dll`) a WebAssembly browser must load besides the component assembly - a packaged module names its `Shared` assembly here. |
| `PackageName` | The NuGet package id, for a module shipped as a package. Static assets of a packaged module are served under `_content/{PackageName}/`. |
| `Resources` | Stylesheets and scripts (`Stomme.Models.Stylesheet`, `Stomme.Models.Script`). A URL starting with `~` resolves to `/Modules/{namespace}/`. |
| `Runtimes` | Comma-separated runtimes the module supports (`Server`, `WebAssembly`). Empty means all. On a site whose runtime is not listed, the module's components are not rendered. |
| `Transports` | Comma-separated opt-in transports the module needs (`Grpc`, `SignalR`). When one is not enabled, the host logs an error at startup naming the module. |
| `IsAutoEnabled` | Default `true`. |

### Permissions a module carries

Two vocabularies exist side by side:

- **Plain permission names** on the instance: `View` and `Edit` (`Stomme.Shared.PermissionNames`). The page and the
  module instance (`Module.PermissionList`) each carry View/Edit rows per role. A user without `View` on the
  instance does not see the placement at all.
- **Permission keys** of the form `resource:operation`, lower-case, at most 50 characters per segment, for example
  `notes:view`. The resource must not be one of the framework's entity names (`Page`, `Module`, `User`, `File`, `Folder`,
  `Site`, `Role`, `Notification` and the rest of `Stomme.Shared.EntityNames`, compared case-insensitively). Declare them
  with the roles a new site grants them to:

  ```csharp
  PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations)
  ```

  `Declare` always keeps `View` and `Edit` and adds `Administrators` to every key's roles. The host materialises the key
  rows per site; a key added in a later version is added to existing sites without touching keys an operator already
  changed. A key that no module declares denies everyone.

  The same key is then checked in three places: `[Requires]` on the command or query (the mediator refuses the call),
  `[RequiresPermissionKey]` on the controller action (the HTTP request is refused before the action runs) and
  `IsAuthorizedPermission(...)` in the component (to hide controls). See
  [Identity and permissions](../security/identity-and-permissions.md).

- The definition's own `PermissionList` holds the `Utilize` rows: who may add the module to a page. The host sets
  Administrators and Registered Users for a normal module, Administrators alone for an `Admin` module (Host Users for
  Stomme's own host-level admin modules), Host Users for a headless module.

### WebAssembly: what reaches the browser

On a site whose runtime is WebAssembly, the browser downloads, per site, the assembly named by each enabled module's
`AssemblyName` and its `Dependencies`, from the server's bin folder, and loads them into the browser's runtime. A `Server`
assembly must never be listed: it cannot load in the browser. The app's own `Client` and `Shared` assemblies are also part of
the app's WebAssembly runtime itself (the `Client` project is the WebAssembly app); a module inside the app therefore
reaches the browser twice, which works (see Rules and pitfalls).

## Using it from an app

### In the app or as a package

| Put the module inside the app (default) | Make it a separate package |
| --- | --- |
| The feature belongs to this app only. | The same module is used by several apps or installations. |
| It is built, versioned and deployed with the app. | It has its own version and release cycle. |
| One build, one solution. | It is installed at runtime (dropped into the `Packages` folder) or bundled by `PackageReference`. |

Both are discovered the same way. For a module inside the app follow
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md). For a separate package use the host's
**Admin > Module Management > Create Module** (Host users only): it generates a three-project module with a package project
from the template in `Server/wwwroot/Modules/Templates/External`, in a folder named `{Owner}.Module.{Name}` next to the
app's folder. To ship a packaged module with the app, follow
[stomme-bundle-external-module](../../.claude/skills/stomme-bundle-external-module/SKILL.md).

### A minimal definition

```csharp
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;

namespace Stomme.Application.Modules.Notes
{
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Notes",
            Description = "Short notes kept per site.",
            Version = "1.0.0",
            PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations),
        };
    }
}
```

`NotesPermissions` is a static class in the `Shared` project; the full module is in
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md).

## Rules and pitfalls

- **The assembly name must contain `Stomme`.** A new project whose assembly name lacks it compiles and runs, and nothing in
  it is ever discovered: no startup runs, no controller is routed, no module appears. Keep the `.Stomme` suffix on every
  project of the app and of a packaged module.
- **The namespace is the module.** A component in a different namespace from its `ModuleInfo` becomes a separate,
  nameless module, and the real module has no `Index` to render. Use `@namespace` in every `.razor` file of the module and
  give `ModuleInfo` the same namespace. Do not rename the namespace of a module that is already placed on sites: its
  `ModuleDefinitionName` changes and existing placements point at a definition that no longer exists.
- **No server code in `Client` or `Shared`.** These assemblies run in the browser on a WebAssembly site. A server-only type in
  them makes the assembly fail to load there. A packaged module whose browser-bound projects reference a package that
  brings `Microsoft.AspNetCore.OpenApi` must also remove its source generator from those projects (the generator emits types
  that reference server-only assemblies); the app's own `Client` and `Shared` projects do not reference it.
- **`ServerManagerType` is a string.** A `typeof(...)` in `Client` would need a reference to `Server`, which the browser cannot
  load. Write the assembly-qualified name, and check it after renaming a class: a wrong name is silently ignored (no search,
  no export, no upgrade hook).
- **Service contracts in `Shared` are not registered by convention.** The `IService` convention pairs a class only with an
  `I{Name}` interface in its own assembly and namespace. A contract in `Shared` with an implementation in `Client` must be
  registered by an `IClientStartup` (see [Module UI](module-ui.md)).
- **A module inside the app works on a WebAssembly site.** The host streams the assembly named by the module's
  `AssemblyName` - for a module inside the app, the app's own `Client` assembly, which is also in the WebAssembly app's
  own download - and the module still renders and calls its API with no console error (driven: list, add, refused duplicate, on a fresh WebAssembly site without prerendering). The cost is
  that assembly downloaded twice. Do not set `Dependencies` for a module inside the app.
- **Avoid `Module` and `Stomme` as a segment of the app's own name.** The type `Stomme.Models.Module` and the
  `Stomme` namespaces then collide with your namespaces.

## Related

- [Module UI](module-ui.md) - components, routing inside a module, state, the service-over-HTTP rule
- [Settings and templates](settings-and-templates.md) - placement settings, module-wide configuration, page templates, `module.<id>.enabled`
- [Portable, searchable, migratable](portable-searchable-migratable.md) - export/import, search, sitemap, upgrade hooks
- [Module persistence](../persistence/module-persistence.md) - the storage-blind aggregate behind a module
- [API and client services](api-and-client-services.md) - controllers and the HTTP client service
- [Application layer](application-layer.md) - commands, queries, handlers
- [Startup and discovery](../architecture/startup-and-discovery.md) - host startup in full
- Skills: [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md),
  [stomme-bundle-external-module](../../.claude/skills/stomme-bundle-external-module/SKILL.md)
