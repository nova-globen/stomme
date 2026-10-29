# Configuration

Nova Globen Stomme reads its settings from a configuration root it builds itself: `appsettings.json`, the environment's
`appsettings.{Environment}.json`, `features.json`, environment variables and, last, the installation's
`appsettings.override.json`. A stock app is conservative: everything that reaches out, runs code, keeps extra data or
costs money is off until a switch turns it on. This page lists where settings come from, how code reads them, and every
switch.

## How it works

### Where settings come from

`Stomme.Server.Program.BuildAppAsync` builds its own `IConfigurationRoot` from these sources, later ones winning:

1. `appsettings.json` in the content root (required; the app fails to start without it). In this app it is
   `Server/appsettings.json`, the framework's defaults, committed.
2. `appsettings.{Environment}.json` (optional), where the environment is `ASPNETCORE_ENVIRONMENT`.
3. `features.json` (optional) - `Server/features.json`, the feature-flag file.
4. Environment variables (`Section__Key=value`).
5. `appsettings.override.json` in the directory named by the environment variable `STOMME_APPSETTINGS_PATH`
   (optional; used only when that directory and that file both exist).

All file sources reload when the file changes. Command-line arguments and .NET user secrets are **not** sources of this
root.

The override file is where the installation writes: the connection string (with its password), `Database:DefaultDBType`,
the `InstallationId`, and settings changed from the admin pages. `Server/Program.cs` makes sure it is used: when
`STOMME_APPSETTINGS_PATH` is not set it points it at `Data` under the current directory (`Server/Data` under
`dotnet run`) and creates `appsettings.override.json` there with `{}`. The app's `.gitignore` keeps `Server/Data` out of
source control, so secrets never reach a commit.

Writes go through `Stomme.Infrastructure.IConfigManager` (`AddOrUpdateSetting`, `RemoveSetting`). When the override
directory and file exist they write to the override file; otherwise to `appsettings.json` (in `Development`) or to
`appsettings.{Environment}.json` when that file exists. `IConfigManager.GetWritableSettingsPath()` tells you which.

### The two configuration objects

`AddStomme` registers that root as **`IConfigurationRoot`**. The container's **`IConfiguration`** is a different
object: ASP.NET Core's default configuration, which has `appsettings.json` and, since 10.73.2, the override file (so
logging levels set there apply), but **not** `features.json`. Before 10.73.2 it lacked the override file too, so every
setting the installer or an admin page writes was invisible through it. Always read through the root.

### Feature flags

Stomme has a feature-flag system per tenant (`Stomme.FeatureManagement`). A flag's state comes from, in order:

1. Configuration: the `FeatureManagement` section (in `features.json`, any appsettings file, the override file or an
   environment variable). A definite `true`/`false` here wins over the admin toggle. It does not bypass a flag's licence
   entitlement, its required flag, or a per-user "off" override.
2. The tenant's stored flag, toggled at **Admin > Feature Flags** (`/admin/features`; Administrators read, Host users
   change).
3. The flag's declared default.

Accepted forms for a key: a boolean (`"myapp.reports": false`), Microsoft.FeatureManagement's object form with an
explicit `enabled`/`Enabled` (`{ "enabled": false }`), or an empty `EnabledFor` array (off). An object form with filters
counts as "no opinion" and falls through to the stored flag.

The key `module.<id>.enabled` is special: `false` stops a module from being placed on the pages Stomme creates from
templates (a new site's pages, and the module's own page templates processed at start); `true` or absent places it as
usual. A module already on a page stays there. `<id>` is the module's name lower-cased with spaces removed
(`Knowledge Base` -> `module.knowledgebase.enabled`). The assembly is loaded either way.

`false` also turns a module's background work OFF on that host, so one image can carry a module that runs on one host
only: the host registers none of the module assembly's hosted services (`IHostedService`, jobs) and calls none of its
endpoint contributors (hubs, gRPC services), and logs once at start which assembly it switched off. A module's SERVER
assembly names its switch with `[assembly: Stomme.Modules.ModuleSwitch("<id>")]` (the module template does); several
assemblies may share one id. Without the attribute the ids are the names of the modules the assembly declares. Set it
per host, for example in a compose file: `FeatureManagement__module.lek.enabled=false`. Declaring and checking flags
of your own: the [stomme-configure-features](../../.claude/skills/stomme-configure-features/SKILL.md) skill.

## Using it from an app

### Reading configuration in code

At composition time, inside `IServerStartup.ConfigureServices`, there is no container yet. Use
`Stomme.Infrastructure.HostConfiguration.Resolve(IServiceCollection)`, which returns the root:

```csharp
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Server.Startup
{
    public class ReportsStartup : IServerStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            var configuration = HostConfiguration.Resolve(services);
            if (bool.TryParse(configuration?["Reports:Enabled"], out var enabled) && enabled)
            {
                services.AddScoped<ReportBuilder>();
            }
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }
    }

    public class ReportBuilder
    {
    }
}
```

At run time, inject `Microsoft.Extensions.Configuration.IConfigurationRoot`, or call
`HostConfiguration.Resolve(IServiceProvider)`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Stomme.Application.Server.Services
{
    public class ReportSettings
    {
        private readonly IConfigurationRoot _configuration;

        public ReportSettings(IConfigurationRoot configuration)
        {
            _configuration = configuration;
        }

        public int PageSize => _configuration.GetValue("Reports:PageSize", 50);
    }
}
```

A value read at composition time is fixed until the app restarts. A value read per call follows the file when it
changes.

### Writing a setting

```csharp
using Stomme.Infrastructure;

namespace Stomme.Application.Server.Services
{
    public class ReportSwitch
    {
        private readonly IConfigManager _config;

        public ReportSwitch(IConfigManager config)
        {
            _config = config;
        }

        // Writes {"Reports": {"Enabled": true}} into the override file and reloads the configuration.
        public void TurnOn()
        {
            _config.AddOrUpdateSetting("Reports:Enabled", true, true);
        }
    }
}
```

`AddOrUpdateSetting` logs and swallows a write failure; it does not throw.

## Configuration

`true`/`false` unless noted. "Restart" means the value is read when the services are composed, so a change takes effect
at the next start. Restart after changing any switch unless you know its reader is per call.

### Installation and database

| Key | Default | Effect |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | empty | The master database. Empty means not installed: the first request opens the installer. Written by the installer. |
| `Database:DefaultDBType` | empty, read as SQL Server (`Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server`) | The database provider type. Written by the installer. |
| `AvailableDatabases` | six entries in `appsettings.json` | The databases the installer offers: LocalDB, SQL Server, SQLite, PostgreSQL, Azure SQL, RavenDB. Each has `Name`, `ControlType`, `DBType`. |
| `Installation:DefaultAlias`, `HostEmail`, `HostPassword` (or `HostPasswordFile`, a file holding it), `HostUsername` (default `host`), `HostName` (default `host`), `SiteTemplate`, `DefaultTheme`, `DefaultContainer` | empty | The unattended install. See [Install, run and upgrade](../operations/install-run-upgrade.md#unattended-install). |
| `Installation:Sites` | empty | The sites beyond the first, each `Name`, `Aliases` and optionally `Tenant` + `ConnectionString` (a new tenant with its own database), created on any start that finds one missing; an invalid list stops the start. See [More sites from configuration](../operations/install-run-upgrade.md#more-sites-from-configuration-installationsites). |
| `Installation:UseHttpsRedirection` | `false` | Adds HTTPS redirection to the pipeline. |
| `Installation:InternalUrl` | empty | Where the host sends calls to its own API (made by client services running on the server). Empty: its first plain-HTTP listener (`http://+:8080` becomes `127.0.0.1:8080`). An http or https URL: its scheme, host and port (a path is dropped; a URL carrying a user name is refused, logged, and the listener used). `public`: the site's public address. |
| `RenderMode` | `Static` | The render mode of the first site, applied at install only: `Static`, `Interactive` or `Headless`. Later changes: **Admin > Site Settings**. |
| `Runtime` | `Server` | The interactive runtime of the first site, applied at install only: `Server`, `WebAssembly` or `Auto`. |
| `Prerender` | absent: on when `RenderMode` is `Interactive` | Whether the first site prerenders interactive components, applied at install only. |
| `InstallationId` | empty | The installation's identity, written at install. Also the cache key prefix. Do not change it. |
| `ConnectionStrings:ModuleAggregates` | empty | Where storage-blind module data lives on a relational install. Empty: a SQLite file `Stomme.ModuleAggregates.db` in the data directory (`<content root>/Data`), whatever database the installer chose. Set: that connection, with the installation's provider. On RavenDB module data always lives in the installation's store. Restart. |
| `ConnectionStrings:FeatureManagement` | empty | Where feature flags live on a relational install. Empty: `Stomme.Features.db` in the data directory. Restart. |
| `Deployment:Mode` | `Hosted` | `Hosted` or `OnPrem` (case insensitive). `OnPrem` turns on offline licence checks and the **Licensing** admin page. Any other value stops the app at start. |

### Switched capabilities

| Capability | Switch | Default | Notes |
|---|---|---|---|
| Health probing job and the health page's data | `Health:Enabled` | `true` (also when absent) | Off: the probe job does nothing, and `/readyz` answers 503 for ever. `/healthz` is always mapped. Paths: `Health:LivenessPath` (`/healthz`), `Health:ReadinessPath` (`/readyz`). |
| Health alerts to the site's operators | `Health:Alerting` | `false` | |
| How often the health job probes each site's mail server | `Health:MailServerProbeIntervalMinutes` | `1` (every tick) | `0` never; N at most once per N minutes. Each probe is one connection to the relay. |
| AI assistant (chat, agents, the landing designer) | `AiAssistant:Enabled` | `false` | Also needs the site's own settings (endpoint, model, key). `AiAssistant:RateLimitPerMinute` (30), `AiAssistant:RequestTimeoutSeconds` (120). See [AI assistant](../ai/ai-assistant.md). |
| MCP server (the site's modules as tools and resources) | `Mcp:Server:Enabled` | `false` | Restart. `Mcp:Server:Path` (`/mcp/v1`, must start with `/`), `Mcp:Server:RequestsPerMinute` (60). Signed-in callers only (the session cookie; no antiforgery token is checked on this route); an anonymous `POST` gets `401`. See [MCP server](../ai/mcp-server.md). |
| MCP client (register external MCP servers and discover their tools; nothing calls those tools yet) | `Mcp:Enabled` | `false` | Restart. `Mcp:Client:HostAllowlist`. See [MCP client](../ai/mcp-client.md). |
| Metering and quotas jobs | `Metering:Enabled` | `false` | Gates the jobs only; the pages are placed anyway. |
| OpenID Connect provider, per tenant host name | `OpenIddict:Enabled` | `false` | `OpenIddict:ServiceAccounts:Enabled` (`false`) as well for client-credentials service accounts. See [Authentication options](../security/authentication-options.md). |
| SAML2 login | `Saml2:Enabled` | `false` | Registers nothing unless `Saml2:IdpEntityId` and `Saml2:IdpMetadataLocation` are also set. |
| Windows login (Negotiate) | `WindowsAuth:Enabled` | `false` | Registered on Windows only, unless `WindowsAuth:AllowNonWindows` is `true`. |
| Data export (tabular exports to private files) | `DataExport:Enabled` | `false` | |
| A user's own data package | `Portability:Enabled` | `false` | |
| Backup and restore | `Backup:Enabled` | `false` | `Backup:Archive:Provider`: `Local` (default; `<data directory>/backups`) or `S3` with `Backup:Archive:S3:*`. Scheduled runs need `Backup:Schedule:Passphrase` or `Backup:Schedule:PassphraseFile`; `Backup:Schedule:IntervalHours` (with `RetentionDays`, `KeepLast`) seeds the first schedule at start-up for an unattended host. See [Backup and restore](../services/backup-restore.md). |
| Pipelines (the workflow engine) | `Pipelines:Enabled` | `false` | Restart. See [Pipelines](../pipelines/overview.md). |
| - schedule triggers | `Pipelines:Schedules:Enabled` | `true` | Only matters when pipelines are on. |
| - public webhook triggers | `Pipelines:Webhooks:Enabled` | `false` | Answers nothing without a secret: `Pipelines:Webhooks:Secret` or `Pipelines:Webhooks:SecretFile`, or a webhook's own. |
| - script steps | `Pipelines:Scripts:Enabled` | `false` | |
| - running an approved program | `Pipelines:Processes:Enabled` | `false` | Nothing launches until `Pipelines:Processes:AllowedRoots` lists directories. |
| Collaboration (locks, editing sessions, presence) | `Collaboration:Enabled` | `false` | |
| Real-time push to browsers (SignalR hubs) | `Transport:SignalR:Enabled` | `false` | Restart. |
| gRPC-Web module services | `Transport:Grpc:Enabled` | `false` | Restart. |
| Message bus between instances | `Messaging:Enabled` | `false` | Restart. `Messaging:Transport` (`inmemory`, `rabbitmq`, `azureservicebus`, `ravendb`), `Messaging:InputQueue` (`stomme`), `Messaging:ConnectionString`, `Messaging:MaxDeliveryAttempts`. See [Events and messaging](../services/events-and-messaging.md). |
| - the dead-letter sweep job | `Operations:DeadLetterSweep:Enabled` | `false` | |
| Help topics | `Help:Enabled` | `false` | |
| Connection health probes (dial each connection every minute) | `Connectivity:HealthProbes:Enabled` | `false` | |
| Cloud cost alerts | `CloudUsage:Alerts:Enabled` | `false` | `CloudUsage:Alerts:AnomalyLookbackDays` (7), `AnomalyThresholdPercent` (20). |
| Temporary-file cleanup job | `CostOptimization:Cleanup:TempFiles:Enabled` | `false` | Sweeps only `CostOptimization:Cleanup:TempFiles:Directories` (none by default). `CostOptimization:Cleanup:DryRun` is `true` by default: it reports and deletes nothing. `CostOptimization:Cleanup:SafetyWindowDays` (7). |
| Demo connections, datasets and boards on every site | `DemoContent:Enabled` | `false` | Restart. |
| OpenAPI document (`/openapi/v1.json`) | `UseSwagger` | absent | On only when the value is exactly the text `true`; write it as the JSON string `"true"`. Restart. |

A module's MCP surface has its own feature flag as well (for example `datasets.mcp`, `dashboards.mcp`), off by default.

When `Messaging:Enabled`, `OpenIddict:Enabled`, `DataExport:Enabled`, `Portability:Enabled`, `Pipelines:Enabled`,
`Backup:Enabled`, `Mcp:Enabled` or `Mcp:Server:Enabled`, `Help:Enabled`, or `Deployment:Mode=OnPrem` is set, the
matching admin page is added to every site at the next start (for example **Pipelines** at `/admin/pipelines`, **MCP**
at `/admin/mcp`). Turning the switch off again does not remove the page.

### Limits and other settings

| Key | Default | Effect |
|---|---|---|
| `DataProtection:KeysPath` | empty: an existing ring in ASP.NET Core's default folder stays there, else `DataProtection-Keys` beside the override file | The folder the Data Protection key ring (sign-in cookies, antiforgery, stored secrets) is kept in. See [Deploy](../operations/deploy.md#data-protection-keys). |
| `DataProtection:ApplicationName` | empty: ASP.NET Core's own, per content root | A fixed name, so the ring still fits when the app runs from another folder. Set it before the first install: changing it later signs everyone out and leaves stored secrets unreadable. |
| `ForwardedHeaders:KnownProxies`, `ForwardedHeaders:KnownNetworks` | empty: loopback and the private networks | Whose `X-Forwarded-For`/`-Proto`/`-Host` are believed - addresses, and networks as `address/prefix`, each a JSON array or one comma-separated value. Naming any replaces the default. A value that does not parse stops the app at start. See [Deploy](../operations/deploy.md). |
| `Files:MaxUploadBytes` | 104857600 (100 MiB) | Largest upload. A value of 0 or less, or at or above `int.MaxValue`, is refused: the app starts, but every upload and file operation fails with an error naming the key. |
| `Files:Unzip:MaxEntries`, `Files:Unzip:MaxTotalBytes`, `Files:Unzip:MaxEntryRatio`, `Files:Unzip:RatioFloorBytes` | 1000; 1 GiB; 100; 64 KiB | Limits on unzipping an uploaded archive. |
| `Files:Chunks:OrphanMinutes` | 60 | Age after which an incomplete chunked upload is swept. |
| `Files:UploadByUrl:AllowedHosts` | empty | Host names "upload from URL" may reach even when they resolve to an internal address. |
| `RateLimiting:Account:SignIn:PermitLimit`, `...:WindowSeconds` | 10 per 60 s | Sign-in attempts per site and client address. |
| `RateLimiting:Account:Email:PermitLimit`, `...:WindowSeconds` | 5 per 300 s | "Forgot password", "forgot user name", the sign-in link by mail, and anonymous registration. |
| `Caching:Duration`, `Caching:FailSafe`, `Caching:Distributed`, `Caching:ScaleOut`, `ConnectionStrings:DistributedCache` | 10 minutes in memory, fail-safe on (each key defaults on its own) | The cache. See [Caching and sync](../services/caching-and-sync.md). |
| `Caching:Cache-Control` | absent | A `Cache-Control` header for static files other than the Blazor framework files. |
| `Logging:FileLogger:LogLevel:Default` | `Error` | The file log, `<content root>/Content/Log`. |
| `Logging:LogLevel:*` | as in `appsettings.json` | Standard ASP.NET Core logging. |

## Rules and pitfalls

- **Never read Stomme settings through `IConfiguration`.** It lacks the override file and `features.json`. The symptom
  is a switch that is on in the override file while your code (or the job it gates) behaves as if it were off, and
  every test that set the key on the builder passes.
- **The override file wins over environment variables.** A value an admin page or the installer wrote there beats the
  same key set in a container's environment. To force a value from the environment, remove it from the override file.
- **`STOMME_APPSETTINGS_PATH` must name an existing directory holding an existing `appsettings.override.json`.**
  Otherwise the override source is skipped and the installer writes its connection string, with the password, into
  `appsettings.json`. The app's `Program.cs` creates both; keep that code.
- **Do not add top-level sections named `Password`, `Lockout`, `User`, `SignIn`, `Tokens`, `Stores` or
  `ClaimsIdentity`.** The host binds the whole configuration root onto ASP.NET Core Identity's `IdentityOptions`, so such
  a section changes the sign-in rules.
- **Adding a key under `Caching` keeps the other cache defaults** since 10.73.2. Before, a `Caching` section with no
  `Duration` made the memory cache duration zero; on an older Stomme set `Caching:Duration` whenever you add a key.
- **`RenderMode`, `Runtime` and `Prerender` apply at install only.** Editing them later changes nothing for existing
  sites; use **Admin > Site Settings**.
- **Feature-flag keys in environment variables contain dots** (`FeatureManagement__module.blogs.enabled=false`). Some
  shells cannot export such a name; container environments and `.env` files can.
- **Never commit secrets.** Connection strings with passwords, API keys and passphrases belong in the override file
  (ignored), in environment variables, or in a file named by a `*File` setting (`Backup:Schedule:PassphraseFile`,
  `Backup:Archive:S3:SecretAccessKeyFile`, `Pipelines:Webhooks:SecretFile`). Per-site secrets (an AI key, a connection's
  credentials) are stored by Stomme in its encrypted per-site secret store, not in configuration.

## Related

- [Startup and discovery](startup-and-discovery.md)
- [Install, run and upgrade](../operations/install-run-upgrade.md)
- [Deploy](../operations/deploy.md)
- [Security posture](../security/security-posture.md)
- Skills: [stomme-configure-features](../../.claude/skills/stomme-configure-features/SKILL.md),
  [stomme-run-and-install](../../.claude/skills/stomme-run-and-install/SKILL.md)
