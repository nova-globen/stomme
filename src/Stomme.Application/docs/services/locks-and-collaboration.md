# Locks and collaboration

Two different things share the word "lock" in Nova Globen Stomme. **`IDistributedLock`** is mutual exclusion for code: one holder of
a key at a time, across instances where the installation's database can coordinate them. **Collaboration** is a feature for
people: when two people open the same pipeline or dashboard editor, they see each other (presence), one of them holds an
**advisory** edit lock, and the others are told when something changes. Collaboration is off by default.

## How it works

### `IDistributedLock` - mutual exclusion for code

- `Stomme.Persistence.Abstractions.IDistributedLock.AcquireAsync(key, ttl, ct)` returns an
  `ILockHandle` (dispose it to release) or `null` when someone else holds the key. It never waits: a `null` means "try
  later". Leases are **not re-entrant**: acquiring a key you already hold in the same flow returns `null`.
- The host registers one implementation that picks its primitive from the **installation's database**, at the time of the
  call:

  | Database | Primitive | Across instances | TTL |
  |---|---|---|---|
  | PostgreSQL | advisory lock on a dedicated connection | yes | not enforced: held until disposed or the connection drops |
  | SQL Server / Azure SQL / LocalDB | `sp_getapplock` on a dedicated session | yes | not enforced: held until disposed or the session drops |
  | RavenDB | compare-exchange value with an expiry | yes | expires after `ttl` |
  | SQLite, the File store, not yet installed | in-process table | **no** - this process only | expires after `ttl` |

- Keys are global to the installation: include the tenant and site (and your module's name) in the key when the work is
  per site, for example `notes:import:{tenantId}:{siteId}`.

`Stomme.Infrastructure.IDistributedLockManager` also exists. It is a best-effort startup mutex over the distributed
cache, grants unconditionally unless `Caching:ScaleOut` and a Redis connection are configured, and is not atomic even then.
Do not use it for application work.

### Collaboration - presence, advisory locks, change notices

- **Off unless `Collaboration:Enabled` is `true`.** Off, the `api/Collaboration` routes answer 404, the editors' panels
  render nothing, and editing is single-editor as always.
- **Which resources**: the pipeline editor and the dashboard builder. The resource types are the constants in
  `Stomme.Collaboration.CollaborativeResourceTypes` (`pipeline`, `dashboard`). Whether a user may coordinate on a resource
  is decided by the module that owns it (the user must hold that module's write permission and be able to read the
  resource); collaboration never grants anything on the resource itself. **There is no supported extension point for an app's
  own resource types**: the one access check knows only the framework's types, so a resource type not owned by a
  framework module is refused.
- **Advisory lock**: one live holder per resource and scope (`Stomme.Collaboration.LockScope.Resource` or `Field`), on a
  lease (default `Collaboration:LeaseSeconds`, at most an hour) that the editor renews while it is open. Taking a lock that
  someone else holds is not an error: the answer (`ResourceLockResult`) names the holder. The lock **never blocks a save**;
  the owning module's optimistic concurrency still decides a conflicting save. An administrator can take a lock over.
- **Edit session and presence**: the first editor opens the resource's session, the next ones join it. Each participant has
  a position (`ActiveRegion`, `CursorPosition`) and a heartbeat every half minute; a participant silent for
  `Collaboration:PresenceSeconds` is removed, and the last one out closes the session. Announced changes are short
  descriptors, never content; a change can be announced only by a participant holding a live lock.
- **Notices**: a lock taken or released, a presence change, an announced change or a save by the owning module becomes a
  notice (resource, kind, who). On this instance it reaches Interactive Server pages through an in-process signal, and
  browsers through the `/hubs/v1/collaboration` hub when `Transport:SignalR:Enabled` is on; otherwise the panel polls every
  five seconds. A notice carries no content; the editor re-reads through the API. With `Messaging:Enabled` on, notices travel on
  the bus, which Stomme subscribes to them (since 10.73.2), so every instance's editors are told. See
  [events and messaging](events-and-messaging.md).
- **Sweep**: the **Collaboration Lock Sweep Job** (every minute, per site) expires lapsed leases and forgets silent
  participants. It does nothing while the switch is off.
- **Who may do what**: `collaboration:view` (join, take and renew a lock, see who is there) and `collaboration:manage`
  (take over a lock, close a session, the overview). Both seed to Administrators. The overview page is
  **Admin > Collaboration** (`admin/collaboration`).
- **Across instances**: the check-then-write of a lock is serialised **within one process**. On several instances two
  editors can both be granted at the same instant; acceptable for an advisory lock, because the save still decides.
- **Data rights**: when a user is erased, their locks are deleted and they are removed from every session; a user's data
  package includes their locks, presence and announced changes.

### The HTTP API (`api/Collaboration`)

All routes answer 404 while the switch is off. Lock routes: `GET locks`, `POST locks/acquire`, `POST locks/renew`,
`POST locks/release` (`collaboration:view`), `POST locks/take-over` (`collaboration:manage`). Session routes:
`POST sessions` (join), `GET sessions`, `GET sessions/{id}`, `POST sessions/{id}/presence`, `POST sessions/{id}/heartbeat`,
`POST sessions/{id}/changes`, `POST sessions/{id}/leave` (`collaboration:view`), `POST sessions/{id}/close` and
`GET overview` (`collaboration:manage`). Request and response shapes are the records in `Stomme.Collaboration`
(`LockRequest`, `JoinSessionRequest`, `PresenceRequest`, `ChangeRequest`, `ResourceLockResult`, `EditSessionView`,
`CollaborationOverviewView`, `CollaborationRefusalView`). A refusal is 404 (a resource the user may not edit, or a session
they are not part of), 409 (a state that refuses the act, or a change without a live lock) or 400 (a malformed reference).
Enums are answered as numbers and read as a number or a name: `LockRequest.Scope` may be `0` or `"Resource"` (a name since
10.73.2; before, only the number, and the name answered 400). A lock request is `{ "resourceType": "dashboard", "resourceId":
"{id}", "scope": "Resource", "leaseSeconds": 120 }`;
acquire answers 200 with the `ResourceLockResult`, release 204.

## Using it from an app

Run a piece of work at most once at a time across instances:

```csharp
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;

namespace Stomme.Application.Modules.Notes.Jobs
{
    public sealed class NotesImportRunner
    {
        private readonly IDistributedLock _lock;

        public NotesImportRunner(IDistributedLock distributedLock)
        {
            _lock = distributedLock;
        }

        /// <summary>Returns false when another run holds the lock; the caller tries again later.</summary>
        public async Task<bool> TryRunAsync(int tenantId, int siteId, Func<CancellationToken, Task> work, CancellationToken ct)
        {
            await using var handle = await _lock.AcquireAsync($"notes:import:{tenantId}:{siteId}", TimeSpan.FromMinutes(10), ct);
            if (handle is null)
            {
                return false;
            }

            await work(ct);
            return true;
        }
    }
}
```

`await using` on a `null` handle is allowed; nothing is disposed.

## Configuration

| Key | Default | Effect |
|---|---|---|
| `Collaboration:Enabled` | `false` | Turns on locks, sessions, presence, notices, the API and the sweep. |
| `Collaboration:LeaseSeconds` | 120 | Lease of a lock when the editor asks for none. |
| `Collaboration:PresenceSeconds` | 120 | How long a participant may be silent before the sweep removes them. |
| `Transport:SignalR:Enabled` | `false` | Live notices to browsers over the collaboration hub; off, panels poll. |
| `module.collaboration.enabled` | seeded | Feature flag that stops the overview page being seeded. |

`IDistributedLock` has no configuration: the installation's database decides it.

## Rules and pitfalls

- **On SQLite and the File store the lock is single-process.** That is correct for one instance; it does not protect
  several instances (they cannot share a SQLite file safely anyway).
- **On PostgreSQL and SQL Server the TTL is not a deadline.** A holder that hangs keeps the lock until its connection drops.
  Keep the held section short and dispose the handle.
- **Never wait for a lock in a loop inside a request.** `AcquireAsync` does not block; return "busy" to the caller or retry
  on the next job tick.
- **Not re-entrant**: a method that takes the lock must not call another that takes the same key.
- **Put tenant and site in the key** for per-site work; a bare key serialises every site of every tenant.
- **Collaboration is advisory.** Do not rely on its lock to prevent lost updates; your own aggregate's concurrency check
  does that.
- **Collaboration covers pipelines and dashboards only.** Do not try to coordinate your own resource type through
  `api/Collaboration`; it is refused.

## Related

- [Jobs](jobs.md) - a job is not a lock; take `IDistributedLock` for work that must not overlap
- [Caching and sync](caching-and-sync.md) - `Caching:ScaleOut` and the Redis backplane
- [Events and messaging](events-and-messaging.md) - hubs, the in-process signal and the bus behind the notices
- [Pipelines editor](../pipelines/editor.md) and [datasets and dashboards](../data/datasets-dashboards-formats.md) - the
  editors that use collaboration
- [Data rights](data-rights.md) - erasure and the data package
