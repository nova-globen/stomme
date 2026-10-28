# Architecture overview

Nova Globen Stomme is a multi-tenant CMS and application framework for .NET 10 and Blazor. This app was made from the
`stomme-app` template and uses Stomme only as NuGet packages: there is no framework source in this repository.
Most of what an application needs - tenants and sites, sign-in, pages, modules, themes, storage on the database the
installer chose, jobs, notifications, backups, AI, MCP, pipelines and connectors - is already in those packages. Build
on it; do not rebuild it.

## How it works

### The app's layout

| Folder | Project | What belongs here |
|---|---|---|
| `Server/` | `Stomme.Application.Server.csproj` (`Microsoft.NET.Sdk.Web`, assembly `Stomme.Application.Server.Stomme`) | The host you run. `Program.cs` calls the framework's host. Your server-side code: `IServerStartup` classes, controllers, server services, aggregates, handlers, jobs. |
| `Client/` | `Stomme.Application.Client.csproj` (`Microsoft.NET.Sdk.BlazorWebAssembly`, assembly `Stomme.Application.Client.Stomme`) | Your Razor components (module UI, themes) and client services that call the server over HTTP. It is also the WebAssembly runtime a WebAssembly site's browser downloads. |
| `Shared/` | `Stomme.Application.Shared.csproj` (`Microsoft.NET.Sdk`, assembly `Stomme.Application.Shared.Stomme`) | Models and interfaces both sides use. No server-only dependencies. |

The solution file is `Stomme.Application.slnx`; `Server` is the startup project. `Server` references `Client` and
`Shared`; `Client` references `Shared`.

Every assembly name ends in `.Stomme`. That is not decoration: the host loads and scans only assemblies whose file
name contains `Stomme`. See [Startup and discovery](startup-and-discovery.md).

Other files the template gives you:

- `Directory.Packages.props` - central package versions. Every `Stomme.*` entry (`Stomme.Server`, `Stomme.Client`,
  `Stomme.Shared`, and `Stomme.Sdk.Slim`, which the app's own projects do not reference) is pinned to the framework version the template was packed from; the other
  packages are pinned to the versions that framework was built with.
- `NuGet.config` - restores every package, `Stomme.*` included, from nuget.org.
- `Server/appsettings.json` - the framework's default settings. Committed. See [Configuration](configuration.md).
- `Server/features.json` - feature-flag overrides (the `FeatureManagement` section).
- `Server/wwwroot/Modules/Templates` and `Server/wwwroot/Themes/Templates` - the source templates the host's
  **Create Module** and **Create Theme** admin pages generate from. They are excluded from compilation.

### The packages

The app references three packages directly: `Stomme.Server` (in `Server`), `Stomme.Client` (in `Client`) and
`Stomme.Shared` (in `Shared`). `Stomme.Server` brings the rest of the framework with it, among them:

- `Stomme.Shared` - models, enums, the `Stomme.Shared.Constants` and `Stomme.Shared.SettingKeys` classes, the
  marker interfaces `Stomme.Modules.IService` and `Stomme.Modules.ITransientService`, and
  `Stomme.Services.IClientStartup`.
- `Stomme.Client` - the Blazor UI: admin pages, the installer, themes, controls, `ModuleBase`, the client services.
- `Stomme.Server` - the host: controllers, the server-side services and repositories, the database providers, the
  built-in modules, `Stomme.Infrastructure.IServerStartup`, `Stomme.Infrastructure.HostConfiguration`,
  `Stomme.Infrastructure.IConfigManager`.
- The persistence libraries (`Stomme.Persistence`, `Stomme.Persistence.Abstractions`, `Stomme.Persistence.EfCore`,
  `Stomme.Persistence.RavenDb`, `Stomme.Persistence.File`), the tenancy libraries, `Stomme.SharedKernel`,
  `Stomme.Mediator`, `Stomme.FeatureManagement`, `Stomme.Security.Abstractions`, `Stomme.Authorization`, the
  event bus and the pipeline engine (`Stomme.Engine.*`).

You never edit these. You add your own code to the three projects of the app, and the host discovers it.

### Tenants, sites and aliases

One running app is one **installation**. An installation holds **tenants** (a tenant is a database, or a share of
one), each tenant holds **sites**, and each site answers on one or more **aliases** (a host name, optionally with a
port and a folder: `localhost:44358`, `example.com`, `example.com/shop`). Every request is resolved by its host name to
an alias, the alias to a site, the site to its tenant. Details: [Tenancy](../security/tenancy.md).

### The request path

The framework's middleware runs in this order (from `Stomme.Extensions.ApplicationBuilderExtensions.UseStommeAsync`):

1. Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host`), in every environment (in
   `Development`, just after WebAssembly debugging).
2. Outside `Development`: the exception handler (`/Error`) and HSTS.
3. Request localization.
4. HTTPS redirection, only when `Installation:UseHttpsRedirection` is `true`.
   Then every `IServerPipelineStartup` of stage `BeforeStaticFiles`.
5. Static files (the web root and the packages' static web assets) - except the sites' uploaded files under
   `/Content/Tenants`, which are never served this way.
6. The framework's exception middleware, then every `IServerPipelineStartup` of stage `BeforeTenantResolution`.
7. **Tenant resolution**: the alias for the request's host and path, which sets the site and tenant for the rest of
   the request. Right after it, a request for a file under `/Content/Tenants` is redirected to the same file on the
   site's `/files/` route, where its folder's permissions apply (another site's path answers `404`).
8. The OpenID Connect provider's store provisioning, every `IServerPipelineStartup` of stage `BeforeJwt`, the JWT
   middleware, then every `IServerPipelineStartup` of stage `BeforeRouting` (see
   [Startup and discovery](startup-and-discovery.md#middleware-earlier-in-the-pipeline)).
9. Routing, CORS, output cache, authentication, the OpenID Connect provider's bearer tokens for `api/...` (when switched
   on), authorization, the rate limiter, the optional transports, the 404 page handling, antiforgery.
10. `Configure(...)` of every `IServerStartup` in a discovered assembly - this is where an app adds middleware.
11. Endpoints: the OpenAPI document first, when `UseSwagger` is `true`; controllers and Razor Pages; a catch-all that answers `404` for any unmatched `api/...` route; the
    OpenID Connect endpoints (when switched on); `/healthz` and `/readyz`; the optional SignalR and gRPC-Web endpoints;
    the Razor components (`App`, which renders pages through the site router); and a fallback so every other path
    reaches the site router.

A page request ends in the site router, which finds the page for the path, the theme and containers for the site, and
the modules placed on the page. Interactive components run in a Blazor Server circuit or in WebAssembly, depending on
the site's render mode and runtime (set per site; see [Render modes](../ui/render-modes.md)).

### The client-server contract

UI code never touches a repository or a `DbContext`. The chain for any data type is: model (`Shared`) -> repository or
aggregate and controller (`Server`) -> a client service in `Client` that calls the controller over HTTP -> components
that use the client service. On the server, the same service interface can be backed by a server-side implementation
so Blazor Server and prerendering do not make an HTTP round trip. See
[API and client services](../modules/api-and-client-services.md).

## Using it from an app

- A new feature is almost always a **module**: a set of types across `Server`, `Client` and `Shared` that the host
  discovers. Start with the [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) skill.
- Services of your own are registered in an `IServerStartup` (server) or `IClientStartup` (client). There is no
  `Startup.cs` and you do not edit `Program.cs` to add services.
- Configuration is read from the host's own configuration root, not from the container's `IConfiguration`. See
  [Configuration](configuration.md).

## Rules and pitfalls

- **Do not put `Stomme` or `Module` in the app's name.** The template's own notes warn that these produce namespace
  conflicts with the framework's `Stomme.*` and `Stomme.Modules.*` namespaces.
- **Do not rename the assemblies away from the `.Stomme` suffix.** An assembly whose file name does not contain
  `Stomme` is never loaded or scanned: its startups, services, controllers and modules silently do not exist.
- **Do not copy framework code into the app** to change it. Your copy will not be discovered the way you expect, and it
  stops receiving fixes when you upgrade. Use the extension points: `IServerStartup`, `IClientStartup`, modules,
  themes, feature flags, configuration.
- **UI code must run in both Blazor Server and WebAssembly.** A component in `Client` must not assume it runs on the
  server. Data access goes through a client service.

## Related

- [Startup and discovery](startup-and-discovery.md)
- [Configuration](configuration.md)
- [Install, run and upgrade](../operations/install-run-upgrade.md)
- [Deploy](../operations/deploy.md)
- [Module anatomy](../modules/anatomy.md)
- [Module persistence](../persistence/module-persistence.md)
- [Tenancy](../security/tenancy.md)
- Skills: [stomme-run-and-install](../../.claude/skills/stomme-run-and-install/SKILL.md),
  [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md)
