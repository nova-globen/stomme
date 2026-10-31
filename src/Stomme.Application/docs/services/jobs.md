# Background jobs and hosted services

Nova Globen Stomme runs scheduled work as **jobs**: classes derived from `Stomme.Infrastructure.HostedServiceBase` that the host
finds by assembly scan, records in its Job table, and runs on a schedule a Host user can change under
**Admin > Scheduled Jobs** (`admin/jobs`). A job runs once per tenant on every tick; per-site work is a loop the job writes
itself. Plain `IHostedService` / `BackgroundService` classes are also discovered and started, but get none of the
scheduling, logging or tenant handling.

## How it works

1. **Discovery.** At startup the host scans every loaded assembly whose name contains `Stomme` (the app's own assemblies
   are named `*.Stomme`, so they qualify) and registers every non-abstract class that implements
   `Microsoft.Extensions.Hosting.IHostedService` as a singleton hosted service. A `HostedServiceBase` subclass is such a
   class. You do not call `AddHostedService` for it.
2. **Registration in the Job table.** Every minute each job checks whether the installation is installed (a
   `ConnectionStrings:DefaultConnection` exists). If it is, and the Job table has no row for the job's type
   (`"{Namespace}.{Class}, {Assembly}"`), the job adds one from the values its constructor set: `Name`, `Frequency`,
   `Interval`, `StartDate`, `EndDate`, `RetentionHistory`, `IsEnabled`, `MaximumDuration`. **After that the row wins**: changing
   the constructor does not change an existing installation's schedule; a Host user changes it on the Scheduled Jobs page.
3. **The schedule.** The loop wakes every minute (after a random start-up delay of up to ten seconds). A job runs when its row
   is enabled, it is not already executing, its next execution time has passed and its end date (if any) has not. The row
   is marked `IsExecuting` while it runs; a run that has been executing longer than `MaximumDuration` minutes is treated as
   dead and reset.
4. **A run.** The job writes a Job Log row, then for **each tenant** of the installation it:
   - enters that tenant's ambient scope (`Stomme.Persistence.Abstractions.ITenantScopeFactory.BeginScope`),
   - creates a new DI scope and points its `Stomme.Infrastructure.ITenantManager` at the tenant (`SetTenant`),
   - calls `ExecuteJob(IServiceProvider)` and then `ExecuteJobAsync(IServiceProvider)` with that scope's provider.

   A tenant that throws is contained: its error is appended to the notes and the next tenant still runs. The returned
   strings are concatenated into the Job Log's notes (HTML: end lines with `<br />`). Only the last `RetentionHistory` log
   rows are kept.
5. **The tenant, not the site.** Inside `ExecuteJobAsync` the scope knows the tenant but **no site**: `SetTenant` installs a
   placeholder alias with `SiteId = -1`. Anything that needs a site - most repositories that take a site id do not, but
   caching, permissions, metering, notifications and the event log attribute work to the current alias - needs the site
   set explicitly, per site, in its own DI scope (see "Per-site work" below).

### Frequency codes

| `Frequency` | Meaning | Next run after a run |
|---|---|---|
| `m` | minutes | previous due time + `Interval` minutes (never in the past) |
| `H` | hours | previous due time + `Interval` hours (never in the past) |
| `d` | days | today at the same time of day + `Interval` days |
| `w` | weeks | today at the same time of day + 7 x `Interval` days |
| `M` | months | today at the same time of day + `Interval` months |
| `O` | once | the job's end date is set to now; it does not run again |

Defaults on `HostedServiceBase`: `Frequency = "d"`, `Interval = 1`, `RetentionHistory = 10`, `MaximumDuration = 30`
(minutes), `IsEnabled = false`, no start or end date. Times are UTC. Seconds are dropped from every time.

### Per-site work: one DI scope per site, and `SetAliasAsync` in it

A job that does something for each site must, for each site, create a **new DI scope** and in it call
`ITenantManager.SetTenant(tenant)` and then `ITenantManager.SetAliasAsync(tenant.TenantId, site.SiteId)`. That sets the
scope's `Stomme.Shared.SiteState.Alias` to a real alias of the site. Why it matters:

- **The cache is keyed by the alias.** The framework caches per-site data under the alias's site key - a user's roles on a
  site, for example. A job that checks a user's permissions on site B while the scope still points at site A reads site A's
  cached roles: a user with a role on site A only is then allowed on site B. This is a cross-site authorisation leak that a
  one-site installation never shows.
- **Metering and the event log attribute to the alias.** `Stomme.Metering.IUsageRecorder` records against the alias's site
  and returns `QuotaDecisionReason.NoSite` without one; `Stomme.Infrastructure.ILogManager` sends an entry with no site to
  the file log instead of the site's Event Log.
- **A new scope per site** prevents scoped services that resolved the alias once (and cached it) from answering for the
  previous site.

`SetAliasAsync(tenantId, siteId)` picks the first alias of the site. If the site has no alias it leaves
`SiteState.Alias` null; check for that and skip the site.

The tenant's ambient scope that the host entered around `ExecuteJobAsync` carries into the scopes the job creates, so a
module's repositories (which read the ambient tenant) see that tenant's rows in each per-site scope without another
`ITenantScopeFactory.BeginScope` call. The site list comes from `Stomme.Repository.ISiteRepository.GetSitesAsync()`,
resolved from the job's own provider.

To decide a stored user's permission without a request (for example "may the user who queued this export still read the
data?"), use `Stomme.Infrastructure.Security.IStoredRowPermissionEvaluator.IsAuthorizedAsync(siteId, userId, permissionKey)`.
It reads stored role and permission rows, never claims, and it still needs the per-site alias for its cache.

### Retention without a job: `IRetentionPurger`

If your module only needs to delete its rows older than N days per site, do not write a job. Implement
`Stomme.Infrastructure.IRetentionPurger` (`SettingKey`, `DefaultDays`, `DisplayName`, `PurgeAsync(siteId, days, ct)`) and
register it scoped in your `IServerStartup`. The framework's **Purge Job** (daily, 03:00 local time converted to UTC,
enabled by default) reads the per-site setting named by `SettingKey`, falls back to `DefaultDays` for a missing,
non-numeric or negative value, calls `PurgeAsync` for every site, and contains a failure per purger. The cancellation token
it passes is always `CancellationToken.None`.

### One-off work per site: `ISiteTask`

To queue a single piece of work for one site, implement `Stomme.Infrastructure.ISiteTask` (both `ExecuteTask` and
`ExecuteTaskAsync`; return `""` from the one you do not use) and add a `Stomme.Models.SiteTask`
(`new SiteTask(siteId, name, "{Namespace}.{Class}, {Assembly}", parameters)`) through
`Stomme.Repository.ISiteTaskRepository.AddSiteTaskAsync` on the server. The framework's **Site Task Job** (every minute)
sets the alias for the site, creates the task type with `ActivatorUtilities`, runs it, and stores the returned text as the
task's `Status`. Completed tasks are purged after 30 days by the Purge Job. The client can add one through
`Stomme.Services.ISiteTaskService` (the API requires the Administrators role and the caller's own site).

**Any loaded `ISiteTask` type can be queued by a site Administrator** through that API, by its type name, with
parameters of their choosing. So a task does only what that site's Administrators may do, on that site, and checks its
parameters; work that needs the Host's rights, or reaches other sites, belongs in a job of its own, never in an `ISiteTask`.

## Using it from an app

A minimal job, in a module's Server folder (Server/Modules/Notes/Jobs/NightlyTidyJob.cs). The complete per-site version,
with a worked service, is in the [add-background-job skill](../../.claude/skills/stomme-add-background-job/SKILL.md).

```csharp
#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Application.Modules.Notes.Jobs
{
    public class NightlyTidyJob : HostedServiceBase
    {
        public NightlyTidyJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Nightly Tidy Job";
            Frequency = "d";
            Interval = 1;
            StartDate = DateTime.UtcNow.Date.AddHours(2); // 02:00 UTC
            IsEnabled = true;
        }

        // Called once per tenant, in a DI scope routed at that tenant.
        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            return tenant is null ? "No tenant for this run<br />" : $"Tidied tenant {tenant.Name}<br />";
        }
    }
}
```

Rules for the class:

- Public, not abstract, in the Server project (an assembly whose name contains `Stomme`).
- One constructor taking `IServiceScopeFactory` (plus singletons only: the instance is a singleton and is also created by
  `ActivatorUtilities` from a scope at first registration). Resolve everything else from the `provider` argument.
- Override `ExecuteJobAsync` (or `ExecuteJob`; both are called, the unused one returns `""`).

### A switch for your job

A job that an app switch turns off keeps `IsEnabled = true` and checks the switch at the start of each run, reading the
**host's** configuration:

```csharp
var configuration = HostConfiguration.Resolve(provider); // Stomme.Infrastructure.HostConfiguration
if (!bool.TryParse(configuration?["Tidy:Enabled"], out var enabled) || !enabled)
{
    return "Tidy Is Disabled (Tidy:Enabled)<br />";
}
```

Do not inject `IConfiguration` for this. In a Stomme host the container's `IConfiguration` lacks `features.json`, and
before 10.73.2 it lacked `appsettings.override.json` too, where an installed site keeps the settings it writes;
`HostConfiguration.Resolve` returns the layered `IConfigurationRoot` that has both. `Stomme.Infrastructure.IConfigManager` (`GetSetting`) reads the same
root. See [configuration](../architecture/configuration.md).

## Configuration

Jobs have no configuration keys of their own. Each job's schedule lives in its Job row and is edited under
**Admin > Scheduled Jobs**: enabled, name, "runs every" interval and frequency, start and end date and time, maximum
duration, retention of log rows. The same page has **Start** / **Stop** per job and a **Log** view per job. The page
edits a job only while it is disabled: **Disable** it first (refused while it is executing), then change it and enable
it again on Save. The page and the job API are Host only.

Some framework jobs check switches at run time, for example `Health:Enabled` (Health Probe Job), `Metering:Enabled`
(the two metering jobs), `Collaboration:Enabled` (Collaboration Lock Sweep Job) and `Operations:DeadLetterSweep:Enabled`
(Dead Letter Sweep Job, whose Job row also starts disabled). Their notes say so when the switch is off.

## Rules and pitfalls

- **Your job shows up only after install, within a minute of start.** Before installation there is no Job table; the loop
  checks every minute. If it never appears, the class is not being discovered: check it is non-abstract, derives
  from `HostedServiceBase`, and is in the Server project.
- **`IsEnabled` defaults to `false`.** A job whose constructor does not set `IsEnabled = true` registers disabled and never
  runs until someone enables it on the Scheduled Jobs page.
- **Changing the constructor does not reschedule an existing installation.** The row is created once. Change the schedule
  on the Scheduled Jobs page, or delete the row on a development install.
- **Renaming or moving the class leaves an orphan row.** The row is keyed by the type's full name and assembly; the new
  name registers a second job.
- **Do not send `[Requires]`-gated mediator requests from a job.** The mediator's authorization behaviour denies a request
  when there is no `HttpContext`, and a job has none; the request throws `AccessDeniedException` into the job's notes. Call
  the service or repository directly.
- **A job has no user.** `ICurrentUser` is empty and metering records no user. Never invent one.
- **Per-site work needs a DI scope and `SetAliasAsync` per site.** See above. Without it the work silently runs against
  the wrong site's cache, or no site at all.
- **The schedule is not a cross-node lock.** Two instances that read the row in the same minute can both start the job.
  Make the work idempotent, or take a lock (`Stomme.Persistence.Abstractions.IDistributedLock`, see
  [locks and collaboration](locks-and-collaboration.md)).
- **No cancellation token reaches `ExecuteJobAsync`.** A stopping host stops between tenants, not inside your work. Keep a
  run short or bound it yourself.
- **A plain `IHostedService` starts before the installer has run** on a first start, and once per process. It must cope
  with "not installed" itself and has no tenant: enter a tenant scope and set an alias yourself, as the jobs do.
- **Async all the way.** No `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`; the framework bans sync-over-async.

## Related

- [Caching and sync](caching-and-sync.md) - why the alias decides the cache key
- [Logging, health and metering](logging-health-metering.md) - writing to the Event Log from a job, the jobs those modules run
- [Locks and collaboration](locks-and-collaboration.md) - `IDistributedLock` for work that must not run twice
- [Tenancy](../security/tenancy.md) - Alias, Site, Tenant
- [Configuration](../architecture/configuration.md) - the layered configuration and `appsettings.override.json`
- Skill: [add a background job](../../.claude/skills/stomme-add-background-job/SKILL.md)
