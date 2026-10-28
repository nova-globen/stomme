---
name: stomme-use-cache
description: Use when caching data in a Nova Globen Stomme app - memoizing an expensive query or computation, caching per site or per tenant, invalidating a cache entry when data changes, reacting to framework entity changes (pages, users, settings), or configuring Redis, the distributed cache or scale-out.
---

# Use the cache

You will have a module whose list query is cached per site through Stomme's `ICacheManager`, forgotten by every command
that changes it, and forgotten when the framework changes an entity it depends on (via an `IEventSubscriber`). The example
caches the Notes module's list (the module the `stomme-create-module` skill builds). Read
[caching and sync](../../../docs/services/caching-and-sync.md) for how the cache, its key spaces and sync events work.

Rules before you start:

- Per-site data uses the overloads that take an `Alias`. A global key is shared by every tenant and site.
- Only cache values that are safe to share between callers, and never mutate a cached object.
- Every call is async; there is no sync overload and none may be added.

## Steps

1. **The cache** - `Server/Modules/Notes/Application/NotesListCache.cs`. It keys the entry by the request's site and refuses
   to run without one, so a missing site can never fall back to a key shared by every site.

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>The site's notes list, cached per site. Forget it after every write that changes what the list shows.</summary>
    public interface INotesListCache
    {
        Task<IReadOnlyList<NoteModel>> GetAsync(Func<CancellationToken, Task<List<NoteModel>>> load);

        Task ForgetAsync();
    }

    public sealed class NotesListCache(ICacheManager cache, SiteState siteState, ILogger<NotesListCache> logger) : INotesListCache
    {
        /// <summary>Prefix keys with the module's name so they cannot collide with the framework's or another module's.</summary>
        public const string CacheKey = "Notes:List";

        public async Task<IReadOnlyList<NoteModel>> GetAsync(Func<CancellationToken, Task<List<NoteModel>>> load)
        {
            // Keyed by the alias: SiteKey:{tenantId}:{siteId}:Notes:List. The factory runs only on a miss.
            return await cache.GetCacheAsync(Alias(), CacheKey, async ct =>
                {
                    logger.LogInformation("Notes list loaded from the store for site {SiteKey}.", siteState.Alias!.SiteKey);
                    return await load(ct);
                },
                TimeSpan.FromMinutes(5),   // memory (L1) lifetime of this entry
                TimeSpan.FromMinutes(15)); // distributed (L2) lifetime, when one is configured
        }

        public Task ForgetAsync() => cache.RemoveCacheAsync(Alias(), CacheKey);

        // The request's site; refused when there is none, never a global key.
        private Stomme.Models.Alias Alias() =>
            siteState.Alias is { SiteId: > 0 } alias
                ? alias
                : throw new InvalidOperationException("The notes list is cached per site; no site is resolved.");
    }
}
```

2. **Read through it, forget it on every write** - in `Server/Modules/Notes/Application/NoteRequests.cs`. The list handler
   loads through the cache:

```csharp
    public sealed class ListNotesQueryHandler(INoteRepository notes, SiteState siteState, INotesListCache listCache)
        : IQueryHandler<ListNotesQuery, IReadOnlyList<NoteModel>>
    {
        public Task<IReadOnlyList<NoteModel>> Handle(ListNotesQuery request, CancellationToken ct) =>
            listCache.GetAsync(async loadCt =>
                (await notes.ListForSiteAsync(NotesScope.Site(siteState), loadCt)).Select(NotesScope.ToModel).ToList());
    }
```

   and `CreateNoteCommandHandler`, `EditNoteCommandHandler` and `DeleteNoteCommandHandler` each take one more constructor
   parameter, `INotesListCache listCache`, and forget the entry right after the write is saved:

```csharp
            await unitOfWork.SaveChangesAsync(ct);
            await listCache.ForgetAsync();
```

   The cached value is the list of `NoteModel` records the API returns - plain, immutable DTOs, which is what a cache entry
   should hold.

3. **Forget it when the framework changes what it depends on** - `Server/Modules/Notes/Application/NotesListInvalidator.cs`.
   Only needed when the cached value is built from framework entities (pages, users, settings, ...); the notes list here is
   not, and the subscriber is shown reacting to page changes, as it would for a list that shows page names. The host finds
   every `IEventSubscriber` by itself; do not register it.

```csharp
#nullable enable
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>
    /// Forgets the site's notes list when the framework changes pages. Found by the host; created from the ROOT provider,
    /// so it injects singletons only.
    /// </summary>
    public sealed class NotesListInvalidator(ICacheManager cache) : IEventSubscriber
    {
        public async Task EntityChangedAsync(SyncEvent syncEvent)
        {
            if (syncEvent.EntityName == EntityNames.Page
                && (syncEvent.Action == SyncEventActions.Create
                    || syncEvent.Action == SyncEventActions.Update
                    || syncEvent.Action == SyncEventActions.Delete))
            {
                // The same key the cache was filled under: the alias's SiteKey is "{TenantId}:{SiteId}".
                var alias = new Alias { TenantId = syncEvent.TenantId, SiteId = syncEvent.SiteId };
                await cache.RemoveCacheAsync(alias, NotesListCache.CacheKey);
            }
        }
    }
}
```

4. **Register the cache** - add the line to `NotesServerStartup.ConfigureServices` in
   `Server/Modules/Notes/Startup/NotesServerStartup.cs`, before the `INotesService` line that is registered last:

```csharp
            // The list cache, forgotten by every write.
            services.AddScoped<INotesListCache, NotesListCache>();
```

   The pages and the API reach it through the module's existing service, controller and mediator; a component never calls
   the cache directly.

5. **Several instances?** Only then configure Redis, on **Admin > System Info > Caching** (it writes every caching key
   together): the distributed cache connection string (`ConnectionStrings:DistributedCache`), then the distributed setting
   (`Caching:Distributed`, Redis as L2) and/or scale-out (`Caching:ScaleOut`, a Redis backplane that keeps every instance's
   memory cache in step). The page saves neither switch as on without a connection string. Restart every instance
   afterwards. Without Scale Out, a change on one instance is invisible to the
   others until the entry expires.

## Verify

1. `dotnet build`, `cd Server`, `dotnet run`, sign in, open the Notes page.
2. Watch the server console: the first list load logs "Notes list loaded from the store for site 1:1." Reload the page, or
   call `GET api/Notes` again: no new line - the second read came from the cache.
3. Add a note through the page (or `POST api/Notes`). The list shows it at once, and the console logs a new "loaded from the
   store" line - the command forgot the entry.
4. Add a page under **Admin > Page Management** (or `POST api/Page`), then load the notes list: a new "loaded" line - the
   invalidator ran.
5. Two sites: each site's list shows only its own notes, and each logs its own key (`1:1`, `1:2`); a write on one site does
   not reload the other's.
6. **Clear All** in the Caching section of **Admin > System Info** (host only) clears it too; the next read loads again.

## Common mistakes

- **One site shows another site's (or tenant's) value.** Cause: the global overload (`GetCacheAsync(string key, ...)`) for
  per-site data. Fix: pass the alias.
- **Stale value after a change.** Cause: no invalidation for that path. Your own writes must call `RemoveCacheAsync`; framework
  writes reach you only through an `IEventSubscriber`, and only those made through the framework's API controllers raise sync
  events - a change made in a job or directly through a repository does not. Fix: remove the entry where you write, or shorten
  the duration.
- **The subscriber does nothing, and the file log (`Content/Log/error.log`) says "Error In EventSubscriber".** Cause: it
  injects a scoped service; it is created from the root provider and its errors are logged and swallowed. Fix: inject
  singletons; create a scope from `IServiceScopeFactory` when you must.
- **Entries expire at once after editing appsettings by hand.** Cause: a `Caching` section with only some keys - the defaults
  apply only when the section is empty, so `Duration` reads 0. Fix: set every key, or use Admin > System Info.
- **Stale values on a second instance.** Cause: L1 only. Fix: Scale Out with Redis, or accept expiry-based staleness.
- **Values vanish from L2 silently.** Cause: the value does not serialize with System.Text.Json; the error is swallowed. Fix:
  cache plain DTOs.
- **`InvalidOperationException: ... no site is resolved` in a job.** Cause: a job's scope has no site until `SetAliasAsync`
  is called. Fix: follow the `stomme-add-background-job` skill.
