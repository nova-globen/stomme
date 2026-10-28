---
name: stomme-support-erasure-and-export
description: Use when a module stores anything about a person (a user id, name, e-mail, address, their content) and must honour the right to erasure and the right to a copy of their data, or offer its data as a tabular export - "delete user", "GDPR", "erasure", "data package", "subject access", "export to CSV/Excel", ISubjectErasureParticipant, ISubjectDataContributor, IExportSource.
---

# Make a module erasable and exportable

You will have a module whose data about a person is erased when that person is deleted from a site (with a line on the
erasure receipt), included in the person's data package, and - optionally - offered as a table in the site's data
export. Read [data rights](../../../docs/services/data-rights.md) for how the three mechanisms work. Nova Globen Stomme cannot see
inside your aggregates: without these classes, "delete user" leaves your module's rows behind and says nothing.

The example is the `Note` aggregate of the Notes module that the `stomme-create-module` skill builds
(`Server/Modules/Notes/Domain/Note.cs`: `SiteId`, `AuthorUserId`, `Title`, `Body`, `CreatedUtc`, registered with
`AddModuleAggregatePersistence<Note, Guid>()`; see [module persistence](../../../docs/persistence/module-persistence.md)).
Do this for every aggregate of yours that holds a person's id or data.

## Steps

1. **Decide the handling per aggregate** and write it down in the participant: `Delete` (the person's own data),
   `Anonymise` (the site keeps the row, the link to the person is removed - clear the id and any identifying text), or
   `Retain` with a `RetentionReason` (`LegalObligation`, `LegalClaims`, `Security`) and a period, after which
   `PurgeRetainedAsync` deletes it. A person's notes are theirs: Delete.

2. **The erasure participant.** Create `Server/Modules/Notes/DataRights/NoteErasureParticipant.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Erasure;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes.DataRights
{
    /// <summary>A person's notes on the site they were deleted from: deleted. Idempotent - a second run finds none.</summary>
    public sealed class NoteErasureParticipant(IRepository<Note, Guid> notes, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public const string Store = "Notes";

        public string Name => "Notes";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete(Store) };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            var siteId = subject.SiteId;
            var userId = subject.UserId;
            if (siteId <= 0 || userId <= 0)
            {
                return new[] { new ErasureOutcome(Store, ErasureHandling.Delete, 0) };
            }

            var mine = await notes.FindAsync(note => note.SiteId == siteId && note.AuthorUserId == userId, ct);
            foreach (var note in mine)
            {
                await notes.DeleteAsync(note, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new[] { new ErasureOutcome(Store, ErasureHandling.Delete, mine.Count) };
        }
    }
}
```

   For an Anonymise store, add a method on the aggregate that clears the author (for example `EraseAuthor()`), call it,
   then `UpdateAsync` instead of `DeleteAsync`, and return `ErasureHandling.Anonymise`. If the module stored files about
   the person (the [stomme-store-files](../stomme-store-files/SKILL.md) skill), delete them here too.

3. **The data-package contributor.** Create `Server/Modules/Notes/DataRights/NoteSubjectDataContributor.cs`. The payload
   is an explicit list of fields, never the serialised aggregate:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Portability;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes.DataRights
{
    /// <summary>The notes a person wrote on the site, for their data package.</summary>
    public sealed class NoteSubjectDataContributor(IRepository<Note, Guid> notes) : ISubjectDataContributor
    {
        public const string EntityType = "note";

        public string SubjectDataType => "notes";

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var siteId = scope.SiteId;
            var userId = scope.UserId;
            return (await notes.FindAsync(note => note.SiteId == siteId && note.AuthorUserId == userId, cancellationToken))
                .OrderBy(note => note.CreatedUtc)
                .Select(note => new PortableEntity(
                    new PortableEntityRef(EntityType, note.Id.ToString()),
                    note.Title,
                    1,
                    JsonSerializer.Serialize(new { title = note.Title, body = note.Body, createdUtc = note.CreatedUtc, updatedUtc = note.UpdatedUtc })))
                .ToList();
        }
    }
}
```

4. **Register both**, scoped, in `NotesServerStartup.ConfigureServices`, before the last registration
   (`AddTransient<INotesService, ServerNotesService>()` stays last):

```csharp
services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Stomme.Application.Modules.Notes.DataRights.NoteErasureParticipant>();
services.AddScoped<Stomme.Portability.ISubjectDataContributor, Stomme.Application.Modules.Notes.DataRights.NoteSubjectDataContributor>();
```

5. **Optional: a tabular export source.** For administrators who export the module's data to CSV, XLSX, JSON or PDF
   (`DataExport:Enabled`). `RequiredPermission` must be a permission key your module declares in its `ModuleInfo` (see
   [identity and permissions](../../../docs/security/identity-and-permissions.md)); here `NotesPermissions.View`. Create
   `Server/Modules/Notes/DataRights/NotesExportSource.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Export;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes.DataRights
{
    /// <summary>The site's notes as one table. The site comes from the server-built context, never from arguments.</summary>
    public sealed class NotesExportSource(IRepository<Note, Guid> notes) : IExportSource
    {
        public const string SourceKey = "notes.notes";

        private static readonly ExportColumn[] Columns =
        {
            new("Title", ExportValueKind.Text),
            new("Body", ExportValueKind.Text),
            new("AuthorUserId", ExportValueKind.Integer),
            new("CreatedUtc", ExportValueKind.DateTimeUtc),
        };

        public string Key => SourceKey;

        public string RequiredPermission => NotesPermissions.View;

        public TableCardinality TableCardinality => TableCardinality.Single;

        public string? Validate(ExportSourceContext context) =>
            string.IsNullOrWhiteSpace(context?.ArgumentsJson) || context.ArgumentsJson.Trim() is "{}" or "null"
                ? null
                : "The notes export takes no arguments.";

        public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            return Task.FromResult<ITabularDocument>(new Document(new Table(notes, context.SiteId)));
        }

        private sealed class Document(ITabularTable table) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                yield return table;
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Table(IRepository<Note, Guid> notes, int siteId) : ITabularTable
        {
            public string Name => "Notes";

            IReadOnlyList<ExportColumn> ITabularTable.Columns => NotesExportSource.Columns;

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                // Nothing is read until the renderer pulls. For a very large store, page through it instead of one FindAsync.
                var siteNotes = await notes.FindAsync(n => n.SiteId == siteId, ct);
                foreach (var note in siteNotes.OrderBy(n => n.CreatedUtc))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new object?[] { note.Title, note.Body, (long)note.AuthorUserId, note.CreatedUtc };
                }
            }
        }
    }
}
```

   Register it in `NotesServerStartup.ConfigureServices`, also before the last registration, with
   `services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, Stomme.Application.Modules.Notes.DataRights.NotesExportSource>());`
   (`Microsoft.Extensions.DependencyInjection.Extensions` is already imported there).

## Verify

Erasure (always on):

1. Run the app, sign in as the host, create a user with a role on the site, sign in as them and create two notes.
2. As the host, delete that user (Admin > User Management).
3. Within about a minute, open `admin/erasure` (or `GET api/Erasure/receipts`): the newest receipt lists your store
   `Notes` under the participant `Notes`, handling Delete, rows `2`, beside Stomme's own participants; no name or
   e-mail appears anywhere on it. Its status is usually `Retaining`, not complete: some of Stomme's own stores (audit
   entries) are retained for a period and purged later.
4. The notes are gone from your module's UI; other users' notes are untouched. Your participant is not run again for
   that subject.
5. To see the failure path, make `EraseAsync` throw once: the receipt stays pending with your participant's name and
   the exception type, and completes on the next minute after you fix it.

Data package (`Portability:Enabled` = `true` in `Server/appsettings.json`, then restart):

1. Sign in as a user who has notes, open the profile's Security tab, My Data, "Request a copy of my data" (over HTTP:
   `POST api/Portability/mine`).
2. Within about a minute it is `Ready`; download it (`GET api/Portability/{id}/download`): the JSON's `entities` hold one
   entry per note with `entityType` `note`, `displayName` the title and `payload` the JSON string with `title`, `body`,
   `createdUtc`, `updatedUtc` - and no other user's notes.

Export (`DataExport:Enabled` = `true`, restart; the host or an administrator holding your data key):

1. Open `admin/dataexport`: the source `notes.notes` is offered (`GET api/DataExport/sources`).
2. Request a CSV (`POST api/DataExport` with `{"sourceKey":"notes.notes","format":"Csv"}`); within about a minute it is
   `Ready`; download it - a header row `Title,Body,AuthorUserId,CreatedUtc` and one row per note on this site.

## Common mistakes

- **Nothing on the receipt for your store.** The participant is not registered, or registered in a startup that is not
  in the app's server assembly. Check `ConfigureServices`.
- **The person's files are still there.** Erasing a note does not delete files stored for it; delete them in the
  participant too.
- **The receipt stays pending.** A participant throws; the run is repeated every minute with every participant. Make
  "nothing to erase" return `0`, never throw.
- **Rows from another site erased.** The filter left out `SiteId`. Erasure is per site.
- **The receipt shows another handling than you meant.** The outcome's `Store` does not match a declaration; the
  receipt uses the declared handling of the matching store.
- **Filtering text in `FindAsync`.** String equality differs between stores; filter on ids, as here
  ([module persistence](../../../docs/persistence/module-persistence.md)).
- **The package contains secrets or other people's data.** The payload is what you serialise: list fields explicitly.
- **The export source is not offered.** `DataExport:Enabled` is off, or the requester lacks `export:create` or your
  `RequiredPermission`.
- **Every export request fails with `InvalidOperationException`.** A source's key is not canonical (lower case, digits,
  `.`, `-`, `_`), it declares no `RequiredPermission`, or two sources claim one key: the source registry refuses to
  build, so the whole export API fails, not just that source.
- **A Retain declaration and no `PurgeRetainedAsync`.** The rows are never deleted; implement the purge.
