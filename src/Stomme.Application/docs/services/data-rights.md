# Data rights: erasure, a person's data package, data export, classification

Nova Globen Stomme carries the duties an application has toward the people whose data it holds. Deleting a user **erases** their
data across every module, through participants, and records a receipt without personal data. A person can ask for a
**copy of their data** (a subject-access package), built from contributors. Administrators can **export** tabular data
to CSV, XLSX, JSON or PDF from sources modules contribute. Datasets carry a **classification** that hides personal data.
A module takes part in each by implementing one small interface - an app's module must, for every store that holds
something about a person.

## How it works

### Erasure (always on)

The contract, in `Stomme.Shared`, namespace `Stomme.Erasure`:

- `ISubjectErasureParticipant` - `string Name`, `IReadOnlyList<ErasureDeclaration> Declarations`,
  `Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)`, and
  `Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)` (defaults to nothing).
- `ErasureDeclaration` - one store and its ONE handling: `ErasureDeclaration.Delete(store)`, `.Anonymise(store)`,
  `.Retain(store, RetentionReason reason, TimeSpan period)` (a period is required; `RetentionReason` is `LegalObligation`,
  `LegalClaims` or `Security`).
  Give each one a `Summary` in the person's words - `ErasureDeclaration.Delete(store) with { Summary = "your notes" }`:
  it is what the profile page lists before someone deletes their account (a declaration without one is listed under the
  participant's `Name`).
- `ErasureSubject(int SiteId, int UserId, string? IdentitySubject, string? Username = null)` - who is erased, on which site.
- `ErasureOutcome(string Store, ErasureHandling Handling, int Affected, ...)` - what was done to one store.
- `IErasureQueue.EnqueueAsync(ErasureSubject)` - what a deletion calls.

The flow:

0. Before anyone deletes their account, the **Delete My Account** section of their profile (Security tab) lists what it
   does on this site, from every registered participant's declarations: what is erased, what is kept without their name,
   and what is kept for a time and why. The same list is `GET api/Erasure/summary`, open to anyone - a privacy page can
   show it too. A module that registers a participant is listed with no page change.
1. A user is deleted from a site - by an administrator (Admin > User Management) or by themselves (the Delete account
   action on their profile, `DELETE api/User/me`; a Host account cannot delete itself). The deletion queues an erasure
   request for that site and user, then removes their roles on the site, their `Users/{userId}/` folder, and - when they
   belong to no other site - the account.
2. The **Subject Erasure** job runs every minute, per tenant and per site, with the site's alias set. For each open
   request it calls every registered participant's `EraseAsync`. The handling recorded for each outcome is the one the
   participant **declared** for that store.
3. If any participant throws, nothing is recorded as erased; the request stays pending with the participant's name and
   the exception type and is tried again on the next run (every participant again - so each must be idempotent). After
   ten failures it is `Failed`; the Host can retry it.
4. When all ran, the request becomes a **receipt**: the participant, store, handling and row count for each store, and no
   user id, name or address. While a retention is open the receipt's status is `Retaining` and it keeps the ids; when it
   falls due the same participant's `PurgeRetainedAsync` is called, the ids are dropped and the receipt completes. Since
   Stomme's own audit stores are retained, a fresh receipt is normally `Retaining`, not complete.
5. The Host reads receipts at `admin/erasure` (`GET api/Erasure/receipts`).

Stomme's own participants already cover its data: user settings, the site's notifications, visitor rows, the event log
(anonymised), personal pages, roles and permissions on the site, identity-provider tokens, sessions, the identity audit
(retained for security, one year), notification preferences and requests, metering (anonymised), MCP audit, portability
and data-export records, AI assistant conversations, resource locks and edit sessions, pipeline step comments, the
pipeline audit (retained for security, one year), the author name on HTML content, and the author stamps
(`CreatedBy`/`ModifiedBy`/`DeletedBy`) on framework rows once no account with that user name remains in the tenant. **An app's module data is covered only if the module registers a participant.**

### Retention of personal data (always on)

The daily Purge Job ages out what a site keeps about people, per site, by Site settings in whole days (since 10.79.0 for
the two marked new). A privacy policy can quote these:

| Site setting | Default | What happens past it |
|---|---|---|
| `MeteringUsageRetention` | `90` (never below 62) | AI usage records (user, provider, model, tokens, cost, outcome) and metered usage records lose their user. With `Metering:Enabled` the rows are deleted and only daily rollups with no user remain (`MeteringUsageRollupRetention`, default 730); with metering off (the default) the user id is set to null and the row stays, so the counts are kept - the same rule as erasure. **New** for metering off: before, nothing aged those rows out. |
| `LastIPAddressRetention` | `90` | A user's `LastIPAddress` is cleared once the sign-in that recorded it (or the registration, for a user who never signed in) is older. `LastLoginOn` stays. A user on several sites gets the shortest period. **New.** |
| `IdentityAuditRetention` | `365` (never below 30) | Identity audit entries (sign-ins, lockouts, role changes) are deleted. |
| `LogRetention` | `30` | Event Log entries are deleted. |
| `VisitorRetention` | `30` | Visitor records (with their IP address) are deleted. |

There is no admin page for the two new settings: set them as Site settings (`EntityName` `Site`) from a module, a site
template or the settings API. The Purge Job's notes name each pass and how many rows it changed ("Last IP Addresses
Purged", "Usage Ledger User Ids Purged").

### A person's data package (`Portability:Enabled`)

The contract, namespace `Stomme.Portability` (`Stomme.Shared`):

- `ISubjectDataContributor` - `string SubjectDataType`,
  `Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default)`.
- `SubjectScope(int SiteId, int UserId)`.
- `PortableEntity(PortableEntityRef reference, string displayName, int schemaVersion, string payloadJson, bool containsSecrets = false, IReadOnlyList<PortableEntityRef>? dependencies = null)`
  and `PortableEntityRef(string entityType, string entityId)`.

A person asks for their data on their profile (Security tab, "Request a copy of my data"), or an administrator asks for a
person's data at `admin/portability`. A per-minute job builds the package per site: it calls every registered
`ISubjectDataContributor`, writes one JSON document into the site's Private `Portability/` folder, and the requester (and
the subject) download it through `api/Portability/{id}/download`. A subject package is kept at most seven days. Subject
data is export only; it is never imported.

The same module also moves **configuration** between sites (`IPortableEntityContributor`: export, validate, import, with
Skip / Overwrite / Rename) and carries placed modules' content through their `IPortable` manager - see
[portable, searchable, migratable](../modules/portable-searchable-migratable.md).

### Data export (`DataExport:Enabled`)

Namespace `Stomme.Export` (`Stomme.Shared`). A **source** is something that can be exported as tables:

- `IExportSource` - `string Key` (canonical: lower-case letters, digits, `.`, `-`, `_`; a non-canonical key is refused),
  `string RequiredPermission` (the permission key a requester must hold for this DATA, on top of `export:create`),
  `TableCardinality TableCardinality` (`Single` or `Multiple`; CSV accepts only `Single`), `string? Validate(ExportSourceContext context)`
  (null = acceptable; refuse unknown arguments), `Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)`.
- `ExportSourceContext` - `SiteId`, `RequesterUserId`, `ArgumentsJson`, built by the server, never from client input.
- `ITabularDocument` (`ReadTablesAsync`, `IAsyncDisposable`), `ITabularTable` (`Name`, `Columns`, `ReadRowsAsync` - rows pulled
  one at a time), `ExportColumn(name, ExportValueKind kind, string? format = null)`.

An administrator requests an export at `admin/dataexport` (keys `export:create`, `export:view`, `export:manage`, seeded to
Administrators). A per-minute job, per site, re-checks the requester's permissions from stored rows, renders the file into
the site's Private `Exports/` folder, and notifies the requester in the app. Exports can be scheduled (a cron in UTC, at
least 15 minutes apart, run as the schedule's owner). Spreadsheet formula injection is neutralised by default. Artifacts
expire after the retention.

### Classification and personal data in datasets

Datasets (see [datasets, dashboards and formats](../data/datasets-dashboards-formats.md)) carry a
`DataClassification` (`Stomme.Datasets`): `Public < Internal < Confidential < Restricted`; Restricted is personal data.
`DatasetClassificationPolicy.IsSensitive(classification)` is true for Confidential and Restricted: their summaries are
masked and raw rows are refused on preview, as an export source and over MCP. When a dataset is registered, a column name
that looks secret makes it at least Confidential and personal data in the sample makes it Restricted; a classification
is raised, never lowered, by a refresh.

The detector is `IPiiScanner` (`Stomme.Infrastructure.Governance`, in `Stomme.Server`, registered as a singleton):
`Scan(text)` returns `PiiMatch`es (`PiiCategory.Email`, `SocialSecurityNumber` (US format), `CreditCard` (Luhn-checked)),
`Mask(text)` replaces them with `[REDACTED]`, `ContainsPii(text)`. It recognises those three patterns only - it is not a
general personal-data detector.

## Using it from an app

For every aggregate that holds a user id, a name, an address or anything else about a person:

1. Decide its handling: **Delete** (it is theirs), **Anonymise** (the site keeps it, the link to the person goes), or
   **Retain** with a reason and a period (a legal or security record), then deleted.
2. Write an `ISubjectErasureParticipant` that does exactly that for `subject.SiteId` and `subject.UserId`, idempotently.
3. Write an `ISubjectDataContributor` that returns what the module holds about the person - an explicit list of fields
   serialised to JSON, never the whole row.
4. Register both scoped in the module's `IServerStartup`:

```csharp
services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, NoteErasureParticipant>();
services.AddScoped<Stomme.Portability.ISubjectDataContributor, NoteSubjectDataContributor>();
```

A participant for the `Note` aggregate of the Notes module (`SiteId`, `AuthorUserId`; see
[module persistence](../persistence/module-persistence.md)):

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
    public sealed class NoteErasureParticipant(IRepository<Note, Guid> notes, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public const string Store = "Notes";

        public string Name => "Notes";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete(Store) with { Summary = "your notes" } };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var siteId = subject.SiteId;
            var userId = subject.UserId;
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

The contributor, the export source and verification are in the
[stomme-support-erasure-and-export](../../.claude/skills/stomme-support-erasure-and-export/SKILL.md) skill.

An export source is registered with
`services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, YourSource>())`; two sources with one
key are refused.

## Configuration

| Key | Default | Effect |
|---|---|---|
| (erasure) | always on | No switch. The Subject Erasure job runs every minute. |
| `Portability:Enabled` | `false` | The `admin/portability` page, the profile's data request, `api/Portability`, the build job. Off, no route matches. |
| `Portability:MaxPackageBytes` | 10485760 | An import larger than this is refused before it is read. |
| `Portability:BuildTimeoutHours` | 24 | A package still building after this is failed as abandoned. |
| `Portability:AuditRetentionDays` | 1095 | How long the portability audit is kept. |
| `Portability:FolderCapacityMb` | 256 | Capacity of the site's `Portability/` folder. |
| `DataExport:Enabled` | `false` | The `admin/dataexport` page, `api/DataExport`, the export job. Off, every route answers 404. |
| `DataExport:MaxArtifactBytes` | 268435456 | Largest artifact; a bigger render fails. |
| `DataExport:JobTimeoutMinutes` | 60 | Render timeout. |
| `DataExport:LeaseMinutes` | 10 | How long a render holds its job before another node may reclaim it. |
| `DataExport:HeartbeatRows` | 10000 | Rows between lease renewals. |
| `DataExport:FolderCapacityMb` | 1024 | Capacity of the site's `Exports/` folder. |
| `DataExport:RetentionDays` | 7 | Days an artifact stays downloadable. Per site: the site setting `ExportRetention`. |
| `DataExport:AuditRetentionDays` | 90 | Days an export audit entry is kept (never shorter than the artifact retention). Per site: `ExportAuditRetention`. |

## Rules and pitfalls

- **No participant, no erasure.** Stomme cannot see inside your aggregates. A module without a participant leaves the
  person's rows behind after "delete user", and the receipt does not mention them. Nothing warns you.
- **Erasure is per site.** `ErasureSubject.SiteId` is the site the person was deleted from; filter on it. Their rows on
  another site of the same tenant stay until they are deleted there.
- **Idempotent, and no throwing for "nothing to do".** A run that fails is repeated with every participant; a second run
  must find nothing and report `0`. A participant that throws blocks the whole receipt for that person.
- **Report only declared stores.** Each `ErasureOutcome.Store` should match one of your `Declarations`; the declared
  handling is what the receipt shows.
- **Anonymise means unlinkable.** Clear the user id (and any name, address or free text that identifies them); a hash of
  the id is still linkable.
- **Retention needs a period and a purge.** A `Retain` declaration without `PurgeRetainedAsync` keeps the rows forever.
- **A data package is an allowlist.** Serialise the fields you choose. Never a password hash, a token, a secret, a reset
  link, or another person's data.
- **Export sources re-check permissions.** `RequiredPermission` must be a permission key your module declares (see
  [identity and permissions](../security/identity-and-permissions.md)); a requester without it gets "does not exist".
  Take the site from `ExportSourceContext.SiteId`, never from the arguments.
- **Stream rows.** `ReadRowsAsync` must yield rows as it reads them; a source that loads a million rows into a list
  defeats the size cap and the memory bound.
- **A config switch must hide routes, not refuse them.** An authorization policy runs before the action body, so an
  `if (!enabled) return NotFound();` inside an action is never reached by a caller without the permission (they get
  403). DataExport, Portability and Backup make their controllers unmatchable with an
  `ActionMethodSelectorAttribute` (`Microsoft.AspNetCore.Mvc.ActionConstraints`) whose `IsValidForRequest` reads the
  switch; do the same for your own switched endpoints.
- **Backups bring erased data back.** Restoring a snapshot taken before an erasure restores the person's rows.

## Related

- [stomme-support-erasure-and-export](../../.claude/skills/stomme-support-erasure-and-export/SKILL.md) - make a module erasable and exportable.
- [Module persistence](../persistence/module-persistence.md) - `FindAsync`, `ITenantOwned`.
- [Portable, searchable, migratable](../modules/portable-searchable-migratable.md) - configuration packages and `IPortable`.
- [Datasets, dashboards and formats](../data/datasets-dashboards-formats.md).
- [Files and folders](files-and-folders.md), [Notifications](notifications.md), [Backup and restore](backup-restore.md).
