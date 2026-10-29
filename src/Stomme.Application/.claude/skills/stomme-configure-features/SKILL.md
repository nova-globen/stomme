---
name: stomme-configure-features
description: Use when asked to turn a Nova Globen Stomme capability on or off (AI assistant, MCP, pipelines, backup, data export, SignalR, the message bus, any ":Enabled" switch), to add a setting or feature flag of the app's own, to check a flag in code, or to stop a bundled module from being placed on pages (module.<id>.enabled).
---

# Configure switches and feature flags

You will have turned a Stomme switch on or off in the right file, or declared a feature flag of the app's own,
checked it in server code and overridden it from `features.json`. How configuration and flags work, and the full list
of switches with their defaults: [Configuration](../../../docs/architecture/configuration.md).

## Steps

### A. Turn a Stomme switch on or off

1. Find the switch in the tables of [Configuration](../../../docs/architecture/configuration.md#switched-capabilities). Note its
   companion settings (a webhook secret, an archive provider, an allowlist): a switch without them often does nothing.
2. Choose the file:
   - a default every installation of this app should have: `Server/appsettings.json` (committed);
   - a value for this installation only, or anything secret: `Server/Data/appsettings.override.json` (git-ignored; in a
     deployment, the override file on the persistent `Data` volume);
   - a container: an environment variable, `Section__Key` (for example `Pipelines__Enabled=true`). The override file
     wins over environment variables, so remove the key from the override file if it is there.
3. Edit the JSON, nesting by section:
   ```json
   {
     "Pipelines": {
       "Enabled": true,
       "Webhooks": {
         "Enabled": false
       }
     }
   }
   ```
4. Restart the app. Many switches are read only when the services are composed.

### B. Declare a feature flag of the app's own

1. `Server/Features/AppFeatures.cs`:
   ```csharp
   using System.Collections.Generic;
   using Stomme.FeatureManagement.Application;

   namespace Stomme.Application.Server.Features
   {
       public static class AppFeatures
       {
           public const string Reports = "myapp.reports";
       }

       // Found by the host's scan: a public class with a public parameterless constructor in a *.Stomme assembly.
       public sealed class AppFeatureContributor : IFeatureContributor
       {
           public IReadOnlyCollection<FeatureDescriptor> Features { get; } = new[]
           {
               new FeatureDescriptor(AppFeatures.Reports, "Show the reports page.", Category: "My app", DefaultEnabled: false)
           };
       }
   }
   ```
   `FeatureDescriptor` and `IFeatureContributor` are in the namespace `Stomme.FeatureManagement.Application` (they come
   with the `Stomme.Server` package). Prefix keys with something of your own; the framework uses `notifications.*`,
   `visitors.*`, `mcp.*`, `module.*` and module names.
2. Check the flag where the capability runs, through `Stomme.Security.Abstractions.IFeatureEvaluator` (scoped; it
   answers `false` when the flag system is not available). `Server/Features/ReportGate.cs`:
   ```csharp
   using System.Threading;
   using System.Threading.Tasks;
   using Stomme.Security.Abstractions;

   namespace Stomme.Application.Server.Features
   {
       public class ReportGate
       {
           private readonly IFeatureEvaluator _features;

           public ReportGate(IFeatureEvaluator features)
           {
               _features = features;
           }

           public Task<bool> IsOnAsync(CancellationToken cancellationToken = default)
           {
               return _features.IsEnabledAsync(AppFeatures.Reports, cancellationToken);
           }
       }
   }
   ```
3. Register it scoped in an `IServerStartup` (`services.AddScoped<ReportGate>();`), see
   [Startup and discovery](../../../docs/architecture/startup-and-discovery.md#a-server-startup).
4. Gate the capability on the server: an API action answers `NotFound()` when `IsOnAsync` is `false`; a job does nothing.
   Hiding a button in the UI is not a gate. `Server/Features/ReportsController.cs`:
   ```csharp
   using System.Threading.Tasks;
   using Microsoft.AspNetCore.Authorization;
   using Microsoft.AspNetCore.Mvc;

   namespace Stomme.Application.Server.Features
   {
       [Authorize]
       [Route("api/[controller]")]
       public class ReportsController : Controller
       {
           private readonly ReportGate _gate;

           public ReportsController(ReportGate gate)
           {
               _gate = gate;
           }

           [HttpGet]
           public async Task<IActionResult> Get()
           {
               if (!await _gate.IsOnAsync(HttpContext.RequestAborted))
               {
                   return NotFound();
               }

               return Ok(new[] { "report" });
           }
       }
   }
   ```
   A real module's controller derives from Stomme's module controller base and checks a permission as well; see
   [API and client services](../../../docs/modules/api-and-client-services.md). A mediator request can carry `[Requires(Feature = AppFeatures.Reports)]`
   (`Stomme.Security.Abstractions.RequiresAttribute`) instead; see
   [Application layer](../../../docs/modules/application-layer.md).
5. Restart. The flag is created for every tenant at start with its default.

### C. Override a flag from configuration

In `Server/features.json` (or any configuration source), under `FeatureManagement`:
```json
{
  "FeatureManagement": {
    "myapp.reports": true
  }
}
```
A definite `true`/`false` here wins over the admin toggle, and takes effect without a restart (the file is reloaded when
it changes). Remove the key to give control back to **Admin > Feature Flags**.

### D. Keep a bundled module off the pages

In `Server/features.json`, under `FeatureManagement` (a key outside that section is not read): `"module.<id>.enabled": false`, where `<id>` is the module's name lower-cased with spaces
removed. Stomme then does not place the module on any page it creates from templates (a new site's pages, and the
module's own page templates processed at start). A module already on a page stays there, and its assembly still loads.

The same `false` keeps the module's background work off on that host: none of its server assembly's hosted services
start and none of its endpoint contributors (hubs, gRPC) are mapped, and the log says so once at start. The server
assembly names its switch with `[assembly: Stomme.Modules.ModuleSwitch("<id>")]` (the module template has it); without
it, the ids are the module names that assembly declares. Per host, in its environment:
`FeatureManagement__module.<id>.enabled=false`. Verify: the start-up log line "Module assembly ... is switched off on this
host", and the module's hub route answering 404.

## Verify

- Start the app and read the log: no configuration error at start.
- For a switch with an admin page (pipelines, backup, data export, portability, MCP, help, message queue, identity
  provider): the page appears under `/admin` after the restart. Other switches: use the capability (for example the
  health page, or the AI assistant's chat) and see it work, or see it refuse when off. Two worked examples, both
  checked before and after the switch:
  - `Pipelines:Enabled`: off, `GET /api/Pipelines` and `/admin/pipelines` answer `404`; on, **Admin > Pipelines**
    (`/admin/pipelines`) lists the starting templates and `GET /api/Pipelines` answers `403` anonymous and `200` as the
    host.
  - `Mcp:Server:Enabled`: on, **Admin > MCP** (`/admin/mcp`) appears (with only this switch it says the client half,
    `Mcp:Enabled`, is off - that is expected), and an MCP `initialize` posted to `/mcp/v1` as the signed-in host (the
    session cookie is enough; this route checks no antiforgery token) answers `200`; anonymous it answers `401`.
- For your flag: **Admin > Feature Flags** (`/admin/features`) lists `myapp.reports` with its description and category
  (`GET api/Feature` returns the same list to an Administrator). Toggle it as the host (in the page, or
  `POST api/Feature/myapp.reports` with `{"enabled":true}` and the antiforgery header) and call the gated endpoint:
  `GET api/Reports` answers `404` when off and `200` when on. Set the key in
  `features.json` and check that the toggle no longer changes the result.
- Check with code that the value you set is the one the app reads: read it from `IConfigurationRoot`, not
  `IConfiguration`.

## Common mistakes

- **The switch is on in the override file or `features.json` but the feature stays off.** Cause: code reads
  `IConfiguration`, which lacks `features.json` (and, before 10.73.2, the override file). Fix: read through `IConfigurationRoot` or
  `Stomme.Infrastructure.HostConfiguration.Resolve(...)`.
- **The switch was changed but nothing happened.** Cause: the switch is read at composition. Fix: restart.
- **An environment variable is ignored.** Cause: the same key is in the override file, which wins. Fix: remove it there.
- **`UseSwagger: true` does not publish the OpenAPI document.** Cause: the host compares the value with the text `true`.
  Fix: write `"UseSwagger": "true"`.
- **Changing `DefaultEnabled` in the descriptor changes nothing.** Cause: the flag row was created at the first start
  with the old default; later starts do not overwrite it. Fix: toggle it in **Admin > Feature Flags**, or set it in
  `features.json`.
- **The flag check throws "Unable to resolve service" or "Cannot consume scoped service".** Cause: `IFeatureEvaluator`
  is scoped and was injected into a singleton. Fix: register the consumer scoped.
- **`module.<id>.enabled: false` did not remove a module from an existing site.** Cause: it only stops new placements from templates. Fix: remove the module from the page in the page's admin UI.
- **A new top-level section changed the password rules.** Cause: the host binds the configuration root onto ASP.NET
  Core Identity's options; sections named `Password`, `Lockout`, `User`, `SignIn`, `Tokens`, `Stores` or
  `ClaimsIdentity` are read by it. Fix: put the app's settings under a section of its own name.
