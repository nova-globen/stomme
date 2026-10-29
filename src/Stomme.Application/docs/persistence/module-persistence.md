# Module persistence

A module keeps its data as aggregates that Nova Globen Stomme stores for it. The module declares the aggregate and one line of
registration; the host decides where it lives - a relational database through EF Core, the installation's RavenDB, or a
file store - so the same module runs unchanged on every database the installer offers. The module writes no
`DbContext`, no migration, no store class and no provider branch.

## How it works

The types (all public, all in packages the app already references through `Stomme.Server`):

| Type | Namespace | Role |
|---|---|---|
| `AggregateRoot<TId>` | `Stomme.SharedKernel.Domain` | Base class of an aggregate. Carries `Id` (protected setter), `Version` (the concurrency token) and the raised domain events. |
| `IRepository<TAggregate, TId>` | `Stomme.SharedKernel.Domain` | `GetByIdAsync`, `GetAllAsync`, `AddAsync`, `UpdateAsync`, `DeleteAsync`, `FindAsync(predicate)`. |
| `IUnitOfWork` | `Stomme.SharedKernel.Domain` | `SaveChangesAsync` - dispatches the domain events of the aggregates written in this scope. |
| `ITenantOwned` | `Stomme.Persistence.Abstractions` | Marks an aggregate as belonging to a tenant: one property, `TenantId TenantId { get; }`. |
| `TenantId` | `Stomme.Tenancy.Domain` | The tenant's identity, a value object over a `Guid`. |
| `ITenantContext` | `Stomme.Tenancy.Abstractions` | The ambient tenant: `CurrentTenantId` (null when none) and `IsSystemScope`. |
| `IStringValueObject` | `Stomme.SharedKernel.Domain` | Marks a value object that is stored as one plain string. |
| `ConcurrencyConflictException` | `Stomme.Persistence.Abstractions` | Thrown by a write whose aggregate changed since it was read (relational store and RavenDB; the file store has no concurrency check). |
| `ModuleAggregatePersistenceExtensions.AddModuleAggregatePersistence<TAggregate, TId>()` | `Microsoft.Extensions.DependencyInjection` | The one registration call. |

The flow:

1. The module's `IServerStartup.ConfigureServices` calls `services.AddModuleAggregatePersistence<Note, Guid>()` once per
   aggregate.
2. At composition time the call reads `Database:DefaultDBType` (written by the installer) and picks the store for that
   database type. An unknown type stops the host with an error rather than falling back to another store.
3. It registers the closed `IRepository<Note, Guid>` for that store, plus a shared scoped `IUnitOfWork`, and a backup
   participant for the aggregate (so the aggregate is in the installation's backups with no module code; mark the class
   `[Stomme.Backup.BackupExcluded("reason")]` to leave it out).
4. A handler takes `IRepository<Note, Guid>` (or the module's own repository interface wrapping it), reads and writes
   aggregates, then calls `IUnitOfWork.SaveChangesAsync`.

Where the data lives:

| Installation database | Where the module's aggregates go |
|---|---|
| SQLite, SQL Server, Azure SQL, LocalDB, PostgreSQL | One host-owned EF Core context, `ModuleAggregatesDbContext` (`Stomme.Persistence.EfCore`), shared by every module, framework modules included. By default it is a SQLite file, `Stomme.ModuleAggregates.db`, in the app's data folder (`Server/Data` unless the host is configured otherwise). Set `ConnectionStrings:ModuleAggregates` to put it on the installation's own relational provider instead. |
| RavenDB | The installation's document store, in the tenant's database. |
| The file store (`Stomme.Database.File.FileDatabase`; set through `Database:DefaultDBType`, not offered by the installer) | JSON files under `ModuleStore:FilePath`, default `module-store` in the data folder, one folder per tenant. |

On the relational store:

- Each aggregate is a table named after the aggregate's class name. The tables are created at startup with EF Core's
  `EnsureCreated`; there are no migrations.
- When the database already exists, startup adds what is missing: a table for a newly registered aggregate, and a column
  for a property added to an existing aggregate. A column added this way is always NULLABLE, because the existing rows
  have no value for it. Nothing is ever dropped, renamed or retyped.
- An `IStringValueObject` property becomes a string column. A `public const int MaxLength` on the value object sets the
  column's length.
- A collection property - a public `IReadOnlyList<T>` (or other `IEnumerable<T>`) over a private backing field named
  `_camelCase`, `_PropertyName`, `m_camelCase` or `camelCase` - becomes ONE column holding the collection as JSON. `T` can
  be an enum, a string, a record, or a class with a usable constructor.
- `DateTime` values are read back as UTC on every provider; a `Local` value is converted to UTC when written.
- `TenantId` maps to a `Guid` column.
- `Version` is the concurrency token, incremented on every write.
- A property typed as any other `ValueObject` (one that is not an `IStringValueObject`) is refused: the shared model
  throws `NotSupportedException` when it is built.

Tenancy. Every store filters the reads of an `ITenantOwned` aggregate to `ITenantContext.CurrentTenantId`: EF Core
through a query filter, RavenDB and the file store by checking each document. The tenant comes from the request's alias
(host name and path), or from the scope a background job sets. When no tenant is resolved, the filter matches nothing - it
fails closed. The tenant filter does NOT separate sites: two sites of one tenant read each other's rows unless the
aggregate carries a `SiteId` and every read filters by it.

Read tracking. On the relational store the repository does not track what it reads (`AsNoTracking`) and detaches what
it saves, so every read comes from the database. This matters because an Interactive Server page keeps ONE dependency
injection scope for as long as it is open: a tracking context would keep returning the first value it read. RavenDB and
the file store open a session per call and behave the same way.

Writes are immediate. `AddAsync`, `UpdateAsync` and `DeleteAsync` each save at once. There is no transaction across two
calls, and `IUnitOfWork.SaveChangesAsync` commits nothing: it dispatches the domain events of the aggregates written in
the scope (see [the application layer](../modules/application-layer.md)).

## Using it from an app

A module's server code lives in the app's `Server` project, for example under `Server/Modules/Notes/`. The complete,
step-by-step version of what follows is the [stomme-add-aggregate](../../.claude/skills/stomme-add-aggregate/SKILL.md)
skill.

The aggregate of the reference Notes module (`Server/Modules/Notes/Domain/Note.cs`):

```csharp
#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Domain
{
    public sealed record NoteCreatedEvent(Guid NoteId, int SiteId, int AuthorUserId) : DomainEvent;

    /// <summary>A note on one site of a tenant. Storage-blind and tenant-owned.</summary>
    public sealed class Note : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxTitleLength = 120;

        public const int MaxBodyLength = 4000;

        // Every store builds the aggregate through this constructor and then sets the properties. Not for application code.
        private Note()
        {
            TenantId = null!;
            Title = string.Empty;
            TitleKey = string.Empty;
            Body = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Title { get; private set; }

        /// <summary>The title normalised for lookups: trimmed, upper-case invariant. Set with Title, never alone.</summary>
        public string TitleKey { get; private set; }

        public string Body { get; private set; }

        public int AuthorUserId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public static string KeyOf(string title) => (title ?? string.Empty).Trim().ToUpperInvariant();

        public static Note Create(Guid id, TenantId tenantId, int siteId, int authorUserId, string title, string body, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A note belongs to a site.");
            }

            if (authorUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorUserId), "A note is written by a signed-in user.");
            }

            var note = new Note
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                AuthorUserId = authorUserId,
                CreatedUtc = nowUtc,
            };
            note.Apply(title, body, nowUtc);
            note.RaiseDomainEvent(new NoteCreatedEvent(note.Id, note.SiteId, note.AuthorUserId));
            return note;
        }

        public void Edit(string title, string body, DateTime nowUtc) => Apply(title, body, nowUtc);

        private void Apply(string title, string body, DateTime nowUtc)
        {
            var trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0 || trimmedTitle.Length > MaxTitleLength)
            {
                throw new ArgumentException($"A title is 1 to {MaxTitleLength} characters.", nameof(title));
            }

            var trimmedBody = (body ?? string.Empty).Trim();
            if (trimmedBody.Length > MaxBodyLength)
            {
                throw new ArgumentException($"A note is at most {MaxBodyLength} characters.", nameof(body));
            }

            Title = trimmedTitle;
            TitleKey = KeyOf(trimmedTitle);
            Body = trimmedBody;
            UpdatedUtc = nowUtc;
        }
    }
}
```

The registration, in the module's `IServerStartup.ConfigureServices` (`NotesServerStartup`), with the module's own
repository over the host's (`Server/Modules/Notes/Persistence/NoteRepository.cs`, in the skill):

```csharp
services.AddModuleAggregatePersistence<Note, Guid>();
services.AddScoped<INoteRepository, NoteRepository>();
```

Writing, in a command handler (the handler pattern is in [the application layer](../modules/application-layer.md)):

```csharp
var tenantId = tenantContext.CurrentTenantId
    ?? throw new InvalidOperationException("Notes belong to a tenant; none is resolved.");
var siteId = siteState.Alias?.SiteId is int id && id > 0
    ? id
    : throw new InvalidOperationException("Notes belong to the request's site; none is resolved.");
var note = Note.Create(Guid.NewGuid(), tenantId, siteId, userId, request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
await notes.AddAsync(note, ct);
await unitOfWork.SaveChangesAsync(ct);
```

`tenantContext` is `ITenantContext`, `siteState` is `Stomme.Shared.SiteState` (its `Alias.SiteId` is the request's
site), `notes` is `IRepository<Note, Guid>` (or `INoteRepository`, which wraps it), `unitOfWork` is `IUnitOfWork`,
`clock` is `TimeProvider` - all injected.

Reading one site's rows with `FindAsync`:

```csharp
var siteNotes = await notes.FindAsync(note => note.SiteId == siteId, ct);
```

Reading one row by id - the tenant filter applies, the site check is yours:

```csharp
var note = await notes.GetByIdAsync(noteId, ct);
if (note is null || note.SiteId != siteId)
{
    throw new KeyNotFoundException($"Note '{noteId}' was not found.");
}
```

Updating - read, change through a method of the aggregate, save the same instance:

```csharp
note.Edit(request.Title, request.Body, clock.GetUtcNow().UtcDateTime);
await notes.UpdateAsync(note, ct);
await unitOfWork.SaveChangesAsync(ct);
```

### FindAsync and text

`FindAsync` runs the filter in the store (SQL `WHERE`, an indexed RavenDB query, an in-memory filter on the file store).
The stores agree ONLY for equality, inequality and range comparisons on non-string values - `int`, `long`, `Guid`,
`enum`, `bool`, `DateTime`, and their nullable forms - combined with `&&`, `||` and `!`.

Strings do not agree, and the compiler cannot tell you:

- `==` on a string is case-sensitive on EF Core (SQLite, SQL Server collations aside, PostgreSQL) and case-insensitive on
  RavenDB.
- `StartsWith` is case-insensitive on SQLite, case-sensitive on PostgreSQL, case-insensitive on RavenDB.
- `.ToLower()` inside the predicate throws on RavenDB.
- A string value object compares as a raw string does on EF Core and RavenDB, but on the file store by the value
  object's own `Equals`, so it diverges too.

To look a row up by text, store a normalised copy and compare the normalised copy against the same normalisation of the
argument, computed in C# outside the predicate:

```csharp
// In the aggregate: set both whenever the title changes.
public string Title { get; private set; }

public string TitleKey { get; private set; }   // Title.Trim().ToUpperInvariant()

// In the repository:
var key = (title ?? string.Empty).Trim().ToUpperInvariant();
var matches = await notes.FindAsync(note => note.SiteId == siteId && note.TitleKey == key, ct);
```

`FindAsync` returns every match; there is no ordering, paging or projection in the store. Order and page the result in
memory, and keep the filter narrow (always the site, plus whatever else you can filter on).

### Your own repository interface

A module usually wraps the generic repository in its own interface to give its reads names. The wrapper must forward
`FindAsync` explicitly: `IRepository<,>.FindAsync` has a default body that throws `NotSupportedException`, so a wrapper
that omits it compiles and then fails when called. See the
[stomme-add-aggregate](../../.claude/skills/stomme-add-aggregate/SKILL.md) skill for a complete wrapper.

## Configuration

| Key | Default | Effect |
|---|---|---|
| `Database:DefaultDBType` | written by the installer | Selects the store for every module aggregate. Read once when the host starts. |
| `ConnectionStrings:ModuleAggregates` | absent | Absent: relational installations keep module tables in the SQLite file `Stomme.ModuleAggregates.db` in the data folder. Set: the module store uses this connection string with the installation's relational provider. Give it a database of its own; the store creates its schema with `EnsureCreated`. |
| `ModuleStore:FilePath` | `module-store` in the data folder | Root folder of the file store. Used only when `Database:DefaultDBType` names the file store. |

## Rules and pitfalls

- **Every module aggregate is tenant-owned.** One module store serves the whole installation, and a site id repeats
  across tenants. Implement `ITenantOwned`, set `TenantId` in the factory from `ITenantContext.CurrentTenantId`, and refuse
  to create the aggregate when it is null. An aggregate that is not `ITenantOwned` is still stored, but its rows are shared
  by every tenant; the host logs at startup: "Module aggregate <its full type name> is not tenant-owned (ITenantOwned): on a multi-tenant
  installation its rows are shared by every tenant of this store".
- **The tenant filter is not a site filter.** Carry `SiteId` and filter every read by it, including after
  `GetByIdAsync` - an id from another site of the same tenant is found otherwise.
- **Set the `Guid` id yourself** (`Guid.NewGuid()` in the factory). Do not rely on a store to generate it.
- **Give every stored property a setter** (`{ get; private set; }`) and give the class a private parameterless
  constructor. Stores build the aggregate through that constructor and set the properties.
- **An aggregate the relational store cannot map switches its own module off.** All module aggregates - the framework's
  too - share one EF Core model. If yours cannot be mapped (an unsupported value object, a collection with no backing
  field, an element type with no usable constructor, two aggregate classes with the same name, which EF Core cannot put
  in one table), it is left out of that model: the host logs at startup "Module aggregate <its full type name> cannot be
  mapped into the shared module store, so its module is switched off on this host; every other module works." with the
  reason, and every call to its repository throws `InvalidOperationException` naming it. Every other module on the
  store keeps reading and writing. When two modules' aggregates share a class name, both are switched off; when yours
  shares one with a Stomme aggregate, yours is. Run the app and read the log after adding or changing an aggregate.
- **Binary data: a `byte[]` property is stored, for small values only.** It round-trips byte for byte on every store
  (a binary column on SQLite, PostgreSQL and SQL Server; base64 inside the document on RavenDB and the file store), and a
  `byte[]?` may be null. Keep it to small values - a hash, a key, a thumbnail of a few kilobytes: the whole aggregate is
  loaded on every read, including every match of `FindAsync`. Files, uploads and anything large belong in the site's
  folders, through the file repository and its ingestion guard ([files and folders](../services/files-and-folders.md),
  the [stomme-store-files](../../.claude/skills/stomme-store-files/SKILL.md) skill); keep the file's id in the
  aggregate.
- **Name aggregate classes distinctly.** The class name is the table name in one store shared with every other module
  and with Stomme's own aggregates.
- **Make a property you add to an existing aggregate nullable** (`int?`, `DateTime?`, a `string`). Its new column is
  created nullable and the rows already stored hold NULL in it.
- **Renaming a property is a new, empty column.** The old column stays with its data; nothing migrates it.
- **No transaction spans two writes.** Each repository call saves immediately. Order writes so that a failure part-way
  leaves valid data, or keep the change inside one aggregate.
- **A stale write throws `ConcurrencyConflictException`.** Two users who edit the same aggregate: the second save fails
  (on the relational store and RavenDB; the file store has no concurrency check, and the last write wins).
  Catch it at the API boundary and answer 409, and let the user reload. After the failure the scope is still usable.
- **Strings in `FindAsync` diverge between stores** (see above). A test that passes on SQLite proves nothing about
  PostgreSQL or RavenDB.
- **`IUnitOfWork.SaveChangesAsync` is what dispatches domain events.** Forget it and no handler runs for that write; the
  events stay queued in the scope and fire on the next `SaveChangesAsync` in the same scope, which on an Interactive
  Server page can be a much later, unrelated action.
- **Do not add a `DbContext`, a `DbSet` or a provider check to a module.** It would work on one database and fail on the
  others, and it bypasses tenancy and backups. The framework's own tables are not for app code either; see
  [framework data](framework-data.md).
- **In Development the container is validated when the host starts** (the ASP.NET Core default). A repository or
  handler whose dependency is not registered stops the host from starting, with an error naming both types. Register every
  dependency in the same change that adds the class.

## Related

- [Framework data](framework-data.md) - sites, pages, users, settings and files, which an app reads through services.
- [The application layer](../modules/application-layer.md) - commands, queries, handlers, validation, domain events,
  value objects.
- [API and client services](../modules/api-and-client-services.md) - how the browser reaches the handlers.
- [Module anatomy](../modules/anatomy.md) - where a module's files go.
- [Tenancy](../security/tenancy.md) - aliases, sites and tenants.
- [Backup and restore](../services/backup-restore.md) - what the backup participant does.
- Skills: [stomme-add-aggregate](../../.claude/skills/stomme-add-aggregate/SKILL.md),
  [stomme-add-command-or-query](../../.claude/skills/stomme-add-command-or-query/SKILL.md),
  [stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md).
