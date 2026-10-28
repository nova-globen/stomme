# Startup and discovery

The app runs the framework's own host. At startup the host loads every assembly whose file name contains `Stomme`,
registers the services those assemblies declare by convention, and runs their startup classes. This is how your code
plugs in: you never register a module, a service or a controller anywhere central, and you never edit `Program.cs` to
add one.

## How it works

### `Server/Program.cs`

The app's `Main` does four things:

1. Makes sure the settings override file exists (see [Configuration](configuration.md#where-settings-come-from)):
   when the environment variable `STOMME_APPSETTINGS_PATH` is not set, it sets it to `Data` under the current
   directory, creates that directory and writes `appsettings.override.json` there with `{}` if the file is missing.
2. Calls `Stomme.Server.Program.BuildAppAsync(args)`, which builds the configuration, composes the services, builds
   the `WebApplication` and its middleware pipeline. It does not run the app or touch the database.
3. Calls `Stomme.Server.Program.InstallAsync(app)`, which installs or upgrades the database (see
   [Install, run and upgrade](../operations/install-run-upgrade.md)) and applies a staged backup restore. When the
   returned `Stomme.Models.Installation.Message` is not empty, the app logs it and exits without serving.
4. Runs the app.

`BuildAppAsync` also accepts an optional `Action<WebApplicationBuilder>` hook that runs right after the builder is
created and before the framework composes its services. It exists for tests; prefer an `IServerStartup`.

### What `BuildAppAsync` composes, in order

`AddStomme` (an extension in the `Microsoft.Extensions.DependencyInjection` namespace) is the composition root.
The order matters for anything you register:

1. The framework's scoped and singleton core services, the configuration root (registered as `IConfigurationRoot`),
   the HTTP clients and the file-upload guard.
2. **Packages are unpacked**: every `*.nupkg` in the content root's `Packages` folder is extracted into place (`lib/`
   into the application's directory, `wwwroot/` and `staticwebassets/` into the web root, `content/` into the content
   root) and deleted. This is how a module installed as a package arrives, before any assembly is loaded.
3. The rest of the framework's own registrations: repositories, tenancy, the connector catalog, the framework store,
   the authorization bridge, the secret store.
4. **Assemblies are loaded**: every `*.dll` in the application's directory whose file name contains `Stomme` (case
   insensitive) and is not already loaded is loaded into the default load context.
5. **Convention registration**, assembly by assembly, for every loaded assembly whose name contains `Stomme`
   (framework building-block libraries opt out):
   - each concrete class implementing `Stomme.Modules.IService` is registered **scoped**;
   - each concrete class implementing `Stomme.Modules.ITransientService` is registered **transient**;
   - each concrete class implementing `Microsoft.Extensions.Hosting.IHostedService` is registered as a singleton
     hosted service;
   - each `Stomme.Infrastructure.IServerStartup` is created and its `ConfigureServices(services)` runs;
   - each `Stomme.Services.IClientStartup` is created and its `ConfigureServices(services)` runs (on the server,
     these registrations serve Blazor Server and prerendering).
6. The database contexts, the message bus (when switched on), identity, authentication, the OpenID Connect provider
   (when switched on), CORS, caching, MVC, Razor Pages and Razor components.
7. MVC: every discovered assembly that contains a class deriving from `Microsoft.AspNetCore.Mvc.Controller` or
   `Microsoft.AspNetCore.Mvc.RazorPages.Page` is added as an application part, and each `IServerStartup.ConfigureMvc`
   runs.

Then the app is built and `UseStommeAsync` builds the middleware pipeline. Each `IServerStartup.Configure(app, env)`
runs after antiforgery and before the endpoints are mapped (see the request path in the
[overview](overview.md#the-request-path)); an `IServerPipelineStartup` runs earlier, at the stage it names (see
[Middleware earlier in the pipeline](#middleware-earlier-in-the-pipeline)).

### How a service is paired with its interface

For `IService` and `ITransientService` the host looks for an interface named `I` + the class name, in the **same
namespace and the same assembly** as the class. It finds it by taking the class's assembly-qualified name and
replacing the class name with `I` + the class name. When that interface exists the class is registered under it;
otherwise under the class itself.

In the browser (WebAssembly), Nova Globen Stomme's client does the same for `IService` and also for
`Stomme.Modules.IClientService`, and it additionally accepts an interface named `I` + the class name anywhere in the
class's interfaces. The server does not. A client service whose interface lives in `Shared` therefore needs an explicit
registration in an `IClientStartup` to work on the server too (see below).

### The WebAssembly client

`Client/Program.cs` hands the start to `Stomme.Client.Program.Main`. In the browser it asks the server which
assemblies the site needs (`api/Installation/list` and `api/Installation/load`), loads them (caching them in IndexedDB),
registers each assembly's `IService`/`IClientService` classes and runs each `IClientStartup`. The server streams only
assemblies for a site whose runtime is WebAssembly (or that is a Hybrid site).

## Using it from an app

### A server startup

Put one `IServerStartup` per module (or one for the app) in `Server`. It needs a public parameterless constructor:
the host creates it with `Activator.CreateInstance`, not from the container.

```csharp
using System;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Application.Server.Startup
{
    public class AppServerStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<IGreeter, Greeter>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }

    public interface IGreeter
    {
        string Greet(string name);
    }

    public class Greeter : IGreeter
    {
        public string Greet(string name)
        {
            return $"Hello, {name}.";
        }
    }
}
```

### Registration by marker interface

The same `Greeter` can register itself instead. The interface must be named `I` + the class name and live in the same
namespace and assembly:

```csharp
using Stomme.Modules;

namespace Stomme.Application.Server.Services
{
    public interface IClockText
    {
        string Now();
    }

    // Registered transient as IClockText by the host's scan; no startup code needed.
    public class ClockText : IClockText, ITransientService
    {
        public string Now()
        {
            return System.DateTime.UtcNow.ToString("O");
        }
    }
}
```

Use the marker interfaces for simple services. Use an `IServerStartup` when the interface lives in another assembly
(for example in `Shared`), when the lifetime is not scoped or transient, or when registration depends on configuration.

### A client service used on both sides

The common shape: the contract in `Shared`, the HTTP implementation in `Client`, a server implementation in `Server`.
Register the HTTP one in an `IClientStartup` only when nothing registered the contract yet, and register the server one
in the `IServerStartup`, where it is added after. In the browser only the client startup runs; on the server both run,
and either order leaves the server implementation as the one resolved (Microsoft's container resolves the last
registration, and the client startup skips when one exists).

The example is the Notes module the
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) skill builds. Its models (`NoteModel`,
`SaveNoteRequest`) are in `Shared/Modules/Notes/Models/NoteModels.cs`.

`Shared/Modules/Notes/Services/INotesService.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;

namespace Stomme.Application.Modules.Notes.Services
{
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

`Client/Modules/Notes/Services/NotesService.cs` - the HTTP implementation, on Stomme's `Stomme.Services.ServiceBase`,
which builds the site-aware API URL and carries the antiforgery header:

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

`Client/Modules/Notes/Startup/NotesClientStartup.cs`:

```csharp
using System.Linq;
using Stomme.Application.Modules.Notes.Services;
using Stomme.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Modules.Notes.Startup
{
    public class NotesClientStartup : IClientStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotesService)))
            {
                services.AddScoped<INotesService, NotesService>();
            }
        }
    }
}
```

In the `Server` project, the module's `NotesServerStartup.ConfigureServices` registers the server implementation as
its last line: `services.AddTransient<INotesService, ServerNotesService>();` (`ServerNotesService` in
`Server/Modules/Notes/Services/` sends the module's mediator requests instead of calling HTTP). The `api/Notes` controller
the HTTP client calls is described in [API and client services](../modules/api-and-client-services.md).

### Middleware

Add middleware in `IServerStartup.Configure`. It runs after authentication, authorization and antiforgery, and before
the endpoints:

```csharp
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Server.Startup
{
    public class HeadersStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next();
            });
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }
}
```

Several startups may exist; their order follows the order the assemblies were loaded and is not something to rely on.

### Middleware earlier in the pipeline

When middleware must run before the site is resolved, before a token is read or before routing - a `301` for a legacy
URL, security headers that must be on static files too, a token that arrives in the query string - implement
`Stomme.Infrastructure.IServerPipelineStartup` instead. It is opt-in, found the same way as an `IServerStartup` (a
class with a public parameterless constructor in an assembly whose file name contains `Stomme`), and its
`Configure(app, env)` runs once, while the pipeline is built, at its `Stage`. `IServerStartup.Configure` does not move.

| `ServerPipelineStage` | Runs | What is known |
|---|---|---|
| `BeforeStaticFiles` | before the static files | nothing about the site; sees every request, static files included |
| `BeforeTenantResolution` | after the static files and the exception middleware, before tenant resolution | no alias, site or tenant yet |
| `BeforeJwt` | after tenant resolution, before the JWT middleware and authentication | the alias and site settings (`Stomme.Extensions.HttpContextExtensions.GetAlias`) |
| `BeforeRouting` | after the JWT middleware, before routing | as above, plus a user a site JWT signed in |

Within a stage, hooks run by `Order` (lower first, default `0`), then by the class's full name - never by the order the
assemblies were loaded. A class may implement both `IServerStartup` and `IServerPipelineStartup`; the host creates a
separate instance for each.

```csharp
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Stomme.Application.Server.Startup
{
    // Legacy URLs answer 301 before the host resolves a site for them.
    public class LegacyRedirects : IServerPipelineStartup
    {
        public ServerPipelineStage Stage => ServerPipelineStage.BeforeTenantResolution;

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Equals("/old-products", System.StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                    context.Response.Headers.Location = "/products";
                    return;
                }
                await next();
            });
        }
    }

    // A <video> element cannot send headers: copy ?access_token= into Authorization before the JWT middleware reads it.
    public class QueryTokenToHeader : IServerPipelineStartup
    {
        public ServerPipelineStage Stage => ServerPipelineStage.BeforeJwt;

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.Use(async (context, next) =>
            {
                var token = context.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(token) && !context.Request.Headers.ContainsKey("Authorization"))
                {
                    context.Request.Headers["Authorization"] = "Bearer " + token;
                }
                await next();
            });
        }
    }
}
```

`BeforeJwt` is also before authentication, so a token moved there reaches the OpenID Connect provider's bearer
validation for `api/...` too (when that is switched on). The same move at `BeforeRouting` is too late for a site JWT:
the request stays anonymous.

## Configuration

There is no switch for discovery itself. Two settings change what is discovered:

- A module bundled by package reference is always loaded; the `FeatureManagement` key `module.<id>.enabled` only decides
  whether Stomme places it on the pages it creates from templates. See [Configuration](configuration.md#feature-flags).
- Configuration-gated features (MCP, pipelines, the message bus and others) decide at composition time whether to
  register their services. Changing one needs a restart. See [Configuration](configuration.md#switched-capabilities).

## Rules and pitfalls

- **An assembly without `Stomme` in its file name is invisible.** Its startups never run, its services are never
  registered, its controllers answer 404, its modules never appear. Keep the template's `<AssemblyName>` values
  (`*.Stomme`). A class library you add for your own code needs the same suffix if it carries startups, services,
  controllers or components.
- **Middleware that must run before routing, tenant resolution or the JWT middleware does not belong in
  `IServerStartup.Configure`** - that runs after all of them. Use an `IServerPipelineStartup`, not an
  `IStartupFilter`: a startup filter's middleware runs before the framework's whole pipeline, including the forwarded
  headers and the exception handling.
- **A startup class needs a public parameterless constructor**, and it is not created from the container: you cannot
  inject into it. Read configuration with `Stomme.Infrastructure.HostConfiguration.Resolve(services)` inside
  `ConfigureServices` (see [Configuration](configuration.md#reading-configuration-in-code)).
- **Every `IHostedService` in your assemblies is started**, whether you register it or not. A hosted service that must
  only run when a switch is on has to check the switch itself; registering it conditionally does not stop the scan.
  Prefer a Stomme scheduled job for periodic work: [Jobs](../services/jobs.md).
- **Interface pairing replaces every occurrence of the class name** in the assembly-qualified name. A class named
  `Report` in a namespace containing `Report` (for example `MyApp.Report.Services.Report`) looks for
  `MyApp.IReport.Services.IReport`, finds nothing, and is registered as the class only. Injecting the interface then
  fails with "Unable to resolve service". Register it in a startup instead.
- **On the server, an `IService` class whose interface is in another assembly is registered as the class only.** The
  browser finds the interface; the server does not. The symptom is a page that works in WebAssembly and fails in
  Blazor Server or prerendering. Register the pair explicitly.
- **In `Development` the container is validated when the host is built** (ASP.NET Core's default there; Stomme does
  not change it). Registering a service whose constructor needs something nothing registers then stops the whole host
  from starting, not only your module, and so does a scoped service captured by a singleton. Outside `Development`
  nothing checks this at start: the same mistake fails later, when the service is first resolved (a hosted service at
  start, anything else on the first request that needs it). Register a fail-closed default rather than leaving a
  dependency for later, and run the app in `Development` at least once after changing registrations.
- **Do not register framework services again.** The framework registers its own with both `Add` and `TryAdd`; a second
  registration of a framework contract from your startup replaces or duplicates it for every module.
- **Controllers must derive from `Controller`** (for example Stomme's module controller base classes) for their
  assembly to be added as an MVC part. An assembly that contains only `ControllerBase`-derived controllers is not
  added by this scan. The app's `Server` assembly is the entry assembly, which ASP.NET Core adds by itself, so this
  matters for an additional class library.
- **A package dropped in `Packages` is unpacked at the next start**, and the `.nupkg` is deleted. A package built for a
  newer framework version than the running one is skipped and deleted.

## Related

- [Overview](overview.md)
- [Configuration](configuration.md)
- [Module anatomy](../modules/anatomy.md)
- [API and client services](../modules/api-and-client-services.md)
- [Jobs](../services/jobs.md)
- Skills: [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md),
  [stomme-bundle-external-module](../../.claude/skills/stomme-bundle-external-module/SKILL.md)
