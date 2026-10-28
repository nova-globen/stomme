# Caching and sync

Nova Globen Stomme caches through `Stomme.Infrastructure.ICacheManager`, a thin async surface over FusionCache: an in-memory cache
(L1) by default, optionally backed by Redis as a distributed cache (L2) and a Redis backplane that keeps several instances'
memory caches in step. Next to it, `Stomme.Infrastructure.ISyncManager` raises **sync events** when framework entities
change; they invalidate cache entries and tell interactive pages to refresh. Sync events stay inside one process.

## How it works

### The cache

- `ICacheManager` is a singleton registered by the host. Every member is async; there is no synchronous overload.
- Two key spaces:
  - **Global** overloads (`GetCacheAsync(string key, ...)`, `RemoveCacheAsync(string key)`) use your key as given. It is
    shared by every tenant and site of the installation.
  - **Per-site** overloads take a `Stomme.Models.Alias` and prefix the key with `SiteKey:{tenantId}:{siteId}:`. Use these
    for anything that belongs to a site. Passing `null` as the alias gives the global key.
- `GetCacheAsync` is get-or-set: on a miss it calls your factory (`Func<CancellationToken, Task<T>>`) once and stores the
  result. Concurrent misses for the same key wait for one factory call.
- Durations: the overloads without durations use the installation's defaults (below). The overloads with
  `memoryCacheDuration` / `distributedCacheDuration` set them per entry; `TimeSpan.MaxValue` means no expiry, and
  `TimeSpan.MinValue` as the distributed duration skips L2 for that entry.
- **Fail-safe** is on by default: when an entry has expired and the factory then throws, the expired value is served
  again (for up to three times the cache duration) instead of the error.
- `ClearAllAsync()` removes every entry of every tenant and site. It is the **Clear All** action in the Caching section of
  **Admin > System Info** (`admin/system`, host only).
- Every key is also prefixed with the installation's `InstallationId`, so several installations can share one Redis.
- With L2 configured, values are serialized with System.Text.Json. A value that does not serialize is not stored in L2 and
  the error is swallowed (the entry still lives in L1).

### Sync events

- `ISyncManager` is a singleton holding an in-memory list. `AddSyncEventAsync(alias, entityName, entityId, action)` (or the
  `(tenantId, siteId, ...)` overload) records a `Stomme.Models.SyncEvent` and **awaits** every subscriber in order before
  it returns, so cache invalidation finishes before the writer's next statement.
- Actions are the constants in `Stomme.Shared.SyncEventActions`: `Create`, `Update`, `Delete` (server events) and
  `Refresh`, `Reload` (client actions). Entity names are the constants in `Stomme.Shared.EntityNames` (`Site`, `Page`,
  `PageModule`, `User`, ...).
- The framework's own API controllers raise them when they change pages, page modules, modules, users, roles, settings,
  files, folders, aliases, languages, themes and module definitions (a page change also raises a `Refresh` of the site).
- **Subscribers** are classes implementing `Stomme.Infrastructure.IEventSubscriber`
  (`Task EntityChangedAsync(SyncEvent syncEvent)`). The host discovers them by assembly scan in every `Stomme`-named
  assembly and creates one per event with `ActivatorUtilities` from the **root** service provider. The framework's own
  subscriber removes the site, modules and user cache entries when those entities change.
- **Clients**: an interactive page polls the server's sync events on each navigation. A `Refresh` of `EntityNames.Site`
  for its site makes it reload the site; a `Reload` of `EntityNames.Host` reloads the whole page. `Refresh` and `Reload`
  events are kept for one hour; the others are only delivered to subscribers.

### Scale-out

| Deployment | Cache | Sync events |
|---|---|---|
| One instance | L1 memory | in-process: correct |
| Several instances, no Redis | each instance has its own L1; a removal on one instance does not reach the others | in-process only: other instances never see them |
| Several instances, `Caching:ScaleOut` + Redis | L1 per instance, kept in step by the Redis backplane | still in-process only |

`ISyncManager` has **no backplane**. Do not use it to tell other instances anything. For cross-instance messages use the
message bus ([events and messaging](events-and-messaging.md)).

## Using it from an app

Cache per site, keyed by the request's site, from a module's server code - here the Notes module's list
(`Server/Modules/Notes/Application/NotesListCache.cs`):

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Models;
using Stomme.Infrastructure;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Application
{
    public interface INotesListCache
    {
        Task<IReadOnlyList<NoteModel>> GetAsync(Func<CancellationToken, Task<List<NoteModel>>> load);

        Task ForgetAsync();
    }

    public sealed class NotesListCache(ICacheManager cache, SiteState siteState) : INotesListCache
    {
        public const string CacheKey = "Notes:List";

        // Stored under SiteKey:{tenantId}:{siteId}:Notes:List; the factory runs only on a miss.
        public async Task<IReadOnlyList<NoteModel>> GetAsync(Func<CancellationToken, Task<List<NoteModel>>> load) =>
            await cache.GetCacheAsync(Alias(), CacheKey, load, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

        // Called by every command that changes the list, right after its SaveChangesAsync.
        public Task ForgetAsync() => cache.RemoveCacheAsync(Alias(), CacheKey);

        private Stomme.Models.Alias Alias() =>
            siteState.Alias is { SiteId: > 0 } alias
                ? alias
                : throw new InvalidOperationException("The notes list is cached per site; no site is resolved.");
    }
}
```

Forget an entry when the framework changes an entity it was built from - here, pages:

```csharp
#nullable enable
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes.Application
{
    // Found by the host; created from the root provider, so it injects singletons only.
    public sealed class NotesListInvalidator(ICacheManager cache) : IEventSubscriber
    {
        public async Task EntityChangedAsync(SyncEvent syncEvent)
        {
            if (syncEvent.EntityName == EntityNames.Page
                && (syncEvent.Action == SyncEventActions.Create
                    || syncEvent.Action == SyncEventActions.Update
                    || syncEvent.Action == SyncEventActions.Delete))
            {
                var alias = new Alias { TenantId = syncEvent.TenantId, SiteId = syncEvent.SiteId };
                await cache.RemoveCacheAsync(alias, NotesListCache.CacheKey);
            }
        }
    }
}
```
The step-by-step version, including registration and how to verify, is the
[use-cache skill](../../.claude/skills/stomme-use-cache/SKILL.md).

## Configuration

The `Caching` section and one connection string. All but `Caching:Cache-Control` are read **once, at startup**: a change needs a
restart. `Caching:Cache-Control` is read on every static-file response, so a change applies at once. They are
shown and edited on **Admin > System Info**, which writes all of them together to the installation's override file.

| Key | Default | Effect |
|---|---|---|
| `Caching:Duration` | 10 (minutes) when absent or not positive | L1 entry lifetime. L2 lifetime is three times this; fail-safe serves an expired entry for up to three times this. |
| `Caching:FailSafe` | `true` when absent | Serve the last value when a factory fails after expiry. |
| `Caching:Distributed` | `false` | Add Redis as L2. Needs `ConnectionStrings:DistributedCache`. |
| `Caching:ScaleOut` | `false` | Add the Redis backplane (keeps L1 caches of several instances in step). Needs `ConnectionStrings:DistributedCache`. |
| `ConnectionStrings:DistributedCache` | empty | StackExchange.Redis configuration string for L2 and the backplane. |
| `Caching:Cache-Control` | empty | The HTTP `Cache-Control` header for static files; not the data cache. |

## Rules and pitfalls

- **Writing part of the `Caching` section by hand keeps the other defaults** since 10.73.2: each missing key takes its own
  default (`Duration` 10, `FailSafe` true). Before, a section holding only, say, `Cache-Control` or `Distributed` read
  `Duration` as 0 and `FailSafe` as false, and entries expired at once - on an older Stomme, set all keys.
- **Use the per-site overloads for per-site data.** A global key is shared by every tenant: a cached site value under a
  global key leaks one tenant's data to another.
- **In a job, the alias must be the site's.** A job's scope has a placeholder alias (`SiteId = -1`) until you call
  `ITenantManager.SetAliasAsync(tenantId, siteId)`; per-site cache reads then use the wrong key. See [jobs](jobs.md).
- **An `IEventSubscriber` gets root services only.** It is created from the root provider; inject singletons
  (`ICacheManager`, `ISyncManager`, `IServiceScopeFactory`) and create a scope for anything else. It runs inside the
  writing request and its exceptions are logged to the file log and swallowed, so keep it short.
- **Sync events come from the framework's controllers.** A change made by a repository call in a job or in your own code
  raises none unless you call `ISyncManager.AddSyncEventAsync` yourself.
- **Several instances without `Caching:ScaleOut` serve stale data** until expiry after a change on another instance.
- **Cache values must be safe to share.** The same instance is handed to every caller from L1; do not mutate a cached
  object.
- Do not use FusionCache's `IFusionCache` directly unless you need a feature `ICacheManager` lacks: you then bypass the
  site-key convention and `Clear All` still removes your entries anyway.

## Related

- [Jobs](jobs.md) - per-site work and the alias
- [Events and messaging](events-and-messaging.md) - telling other instances about a change
- [Locks and collaboration](locks-and-collaboration.md) - the cache-backed startup lock and the real distributed lock
- [Configuration](../architecture/configuration.md)
- [Deploy](../operations/deploy.md) - running several instances
- Skill: [use the cache](../../.claude/skills/stomme-use-cache/SKILL.md)
