---
name: stomme-add-background-job
description: Use when adding scheduled or recurring background work to a Nova Globen Stomme app - a job, a scheduled task, a nightly or hourly run, cleanup, a periodic sync, a timer, a hosted service, per-site background processing, or retention/purge of old rows.
---

# Add a background job

You will have a scheduled job in a module's Server side that Stomme discovers by itself, records under **Admin >
Scheduled Jobs**, runs once per tenant on its schedule, and inside each tenant does its work **per site**, in a DI scope
routed at that site. It has its own on/off switch read from the host's configuration. The example adds a "Notes Digest Job"
to the Notes module (the module the `stomme-create-module` skill builds): every hour it counts each site's notes and
writes the count to that site's Event Log. Read [jobs](../../../docs/services/jobs.md) for how the scheduler works;
[caching and sync](../../../docs/services/caching-and-sync.md) explains why the site's alias matters.

First decide whether you need a job at all:

- Deleting a module's rows older than N days per site: implement `Stomme.Infrastructure.IRetentionPurger` instead (the
  framework's Purge Job calls it). See "Retention without a job" in the jobs doc.
- One-off work for one site, queued by something that happened: an `ISiteTask` (same doc).
- Recurring work: this skill.

## Steps

1. **The work, as a scoped service** - `Server/Modules/Notes/Jobs/NotesDigest.cs`. Put the real work here; it is resolved
   inside one site's scope, so everything it injects already points at that site and its tenant. Replace the body with your
   logic.

```csharp
#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using LogLevel = Stomme.Shared.LogLevel;

namespace Stomme.Application.Modules.Notes.Jobs
{
    public interface INotesDigest
    {
        /// <summary>Does the job's work for one site and returns a line for the job log.</summary>
        Task<string> RunAsync(Site site, CancellationToken ct = default);
    }

    /// <summary>Resolved inside one site's scope: everything it injects already points at that site and its tenant.</summary>
    public sealed class NotesDigest(INoteRepository notes, ILogManager log) : INotesDigest
    {
        public async Task<string> RunAsync(Site site, CancellationToken ct = default)
        {
            var count = (await notes.ListForSiteAsync(site.SiteId, ct)).Count;
            log.Log(site.SiteId, LogLevel.Information, this, LogFunction.Other, "Notes Digest: {Count} Notes On Site {SiteName}", count, site.Name);
            return $"Site {site.Name}: {count} notes<br />";
        }
    }
}
```

2. **The job** - `Server/Modules/Notes/Jobs/NotesDigestJob.cs`. The host calls `ExecuteJobAsync` once per tenant, with a
   provider whose scope is already routed at that tenant (the tenant's data is visible, no site is set). The job lists the
   tenant's sites and gives each one its own scope with the site's alias set.

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Modules.Notes.Jobs
{
    public class NotesDigestJob : HostedServiceBase
    {
        public const string EnabledKey = "Notes:Digest:Enabled";

        public NotesDigestJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Notes Digest Job";
            Frequency = "H"; // m, H, d, w, M or O (once)
            Interval = 1;
            IsEnabled = true; // the Job row is created enabled; the switch below decides at run time
        }

        // Called once per tenant, in a DI scope routed at that tenant (but at no site).
        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            // The HOST's configuration: it includes appsettings.override.json, which IConfiguration does not.
            if (!bool.TryParse(HostConfiguration.Resolve(provider)?[EnabledKey], out var enabled) || !enabled)
            {
                return $"Notes Digest Is Disabled ({EnabledKey})<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            if (tenant is null)
            {
                return "Notes Digest: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<NotesDigestJob>>();
            List<Site> sites = (await provider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                .Where(site => !site.IsDeleted)
                .ToList();

            var log = new StringBuilder();
            foreach (var site in sites)
            {
                // ONE DI scope per site, with the site's alias set in it.
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant.TenantId, site.SiteId);
                    if (siteScope.ServiceProvider.GetRequiredService<SiteState>().Alias is null)
                    {
                        log.Append("Skipped site ").Append(site.Name).Append(": it has no alias<br />");
                        continue;
                    }

                    log.Append(await siteScope.ServiceProvider.GetRequiredService<INotesDigest>().RunAsync(site));
                }
                catch (Exception ex)
                {
                    // Contained per site: one site's failure must not stop the others.
                    logger.LogError(ex, "Notes digest failed for site {SiteId} of tenant {TenantId}.", site.SiteId, tenant.TenantId);
                    log.Append("Notes Digest Failed For Site ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Notes Digest: No Sites<br />" : log.ToString();
        }
    }
}
```

   The tenant scope the host opened around this call carries into the per-site scopes, so the module's repositories read
   this tenant's rows there too; no `ITenantScopeFactory` call is needed in the job.

3. **Register the work service** in the module's `IServerStartup` - add the line (and
   `using Stomme.Application.Modules.Notes.Jobs;`) to `NotesServerStartup.ConfigureServices` in
   `Server/Modules/Notes/Startup/NotesServerStartup.cs`, before the `INotesService` line that is registered last. Do **not**
   register the job: the host finds it.

```csharp
            // The digest job's per-site work (the job itself is found by the host).
            services.AddScoped<INotesDigest, NotesDigest>();
```

4. **The switch.** Add it to `Server/appsettings.json` so it is visible and off by default:

```json
"Notes": {
  "Digest": {
    "Enabled": false
  }
}
```

   An installation turns it on in its `appsettings.override.json` (or an environment variable
   `Notes__Digest__Enabled=true`). The job reads it at every run, so a change to the override file takes effect at the next
   run without a restart. If the job should always run, drop the check and the setting.

## Verify

1. `dotnet build`, then `cd Server` and `dotnet run`; install if it is a fresh database.
2. Sign in as the host and open **Admin > Scheduled Jobs** (search "Notes" - the list has two pages). "Notes Digest Job" is
   listed, status Idle, "Every 1 Hour(s)", with a next execution time. The row is created at the first tick after start. If it
   is not there after two minutes, the class is not discovered (see Common mistakes).
3. Open its **Log**. The first run happens at that first tick: its notes say
   `Notes Digest Is Disabled (Notes:Digest:Enabled)` - the switch works.
4. Set `"Notes": { "Digest": { "Enabled": true } }` in `Server/Data/appsettings.override.json` (or wherever
   `STOMME_APPSETTINGS_PATH` points). To see a run without waiting an hour, change its schedule on the Scheduled Jobs page:
   an enabled job cannot be edited, so open **Edit**, click **Disable**, open **Edit** again, set **Runs Every** to 1
   Minute(s) with **Enabled?** Yes, and **Save**. The next log entry is **Succeeded** and shows one "Site ...: N notes" line per site.
5. With two sites (add one under **Admin > Site Management**, give each a different number of notes), each site's line
   carries its own count, each site once. Open **Admin > Event Log** on each site (sign in at that site's URL): the
   "Notes Digest: N Notes On Site ..." entry is on that site, with that site's count.
6. Set the interval back the same way (Disable, then Edit and Save).

## Common mistakes

- **The job never appears on Scheduled Jobs.** Cause: the class is abstract, not derived from `HostedServiceBase`, or in the
  Client or Shared project. Fix: a non-abstract class in the Server project. Also check the app is installed - jobs
  register only after install.
- **It appears but never runs.** Cause: `IsEnabled` not set to `true` in the constructor (the default is false), or the row
  was created disabled earlier. Fix: enable it on the Scheduled Jobs page; constructor changes do not touch an existing row.
- **The switch has no effect on an installed site.** Cause: reading `IConfiguration`, which lacks `features.json` (and,
  before 10.73.2, the override file). Fix: `HostConfiguration.Resolve(provider)` as above.
- **Work lands on the wrong site, or a user is allowed on a site where they have no role.** Cause: per-site work done in the
  job's own scope, or several sites in one scope, so the cache and the alias answer for another site. Fix: one DI scope per
  site and `SetAliasAsync(tenant.TenantId, site.SiteId)` in it, as in step 2.
- **Log entries end up in `error.log` instead of the Event Log.** Cause: the overload of `ILogManager.Log` without a site id,
  in a scope with no alias. Fix: set the alias as above, or use the `siteId` overload as step 1 does.
- **The job throws `AccessDeniedException`.** Cause: it sends a mediator command or query that carries `[Requires]`; with no
  `HttpContext` the authorization behaviour denies it. Fix: call the module's repository or a service directly from the job,
  as step 1 does with `INoteRepository`.
- **Container error at startup about the job's constructor.** Cause: the constructor takes a scoped service. Fix: take only
  `IServiceScopeFactory` and resolve everything from the `provider` argument.
- **Renaming or moving the job class leaves an orphan row.** The Job row is keyed by the class's full type name; a renamed
  class gets a new row and the old one stays. Fix: the Scheduled Jobs page has no delete; Disable the
  old row there, or as a Host remove it with `DELETE api/Job/{id}`.
- **It runs twice when the app runs on two instances.** Cause: the Job row's executing flag is not a cross-instance lock.
  Fix: make the work idempotent, or wrap it in `IDistributedLock.AcquireAsync` (see
  [locks and collaboration](../../../docs/services/locks-and-collaboration.md)).
