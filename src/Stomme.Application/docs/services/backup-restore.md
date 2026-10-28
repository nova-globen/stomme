# Backup and restore

Nova Globen Stomme takes encrypted, logical snapshots of the whole installation - the framework's tables in every tenant database,
every module aggregate, the sites' file trees, and the optional stores that are switched on - and restores one when the
host starts, before any request is served. It is off by default (`Backup:Enabled`). When it is on, the Host manages
snapshots at `admin/backup`. Archives are kept on local disk by default - the `backups` folder in the installation's
data directory (`Server/Data/backups`; in a container, the data volume), so copying that volume offsite copies the
backups too, and the folder is not web-served. An S3-compatible object store is an option (`Backup:Archive:Provider` =
`S3`, below); nothing needs configuring for the local default.

## How it works

A snapshot is made of **parts**, one per **participant** (`Stomme.Backup.IBackupParticipant`, in `Stomme.Shared`).
The engine asks every registered participant, in restore order, to write its part into one archive file.

What is carried, by the participants Stomme registers:

| Part | What it holds | Registered |
|---|---|---|
| `framework:databases` | The master tables and each tenant database's framework tables and ASP.NET Identity users, with their original keys. The search index is not carried; it is rebuilt after a restore. | Always |
| `content:tenants` | The `Content/Tenants` trees under the content root and the web root - every site's folders and files. Upload staging and logs are not carried. | Always |
| `module-aggregate:{aggregate full type name}` | One part per module aggregate registered with `AddModuleAggregatePersistence` - **your app's aggregates included, with no code** - unless the aggregate class carries `[BackupExcluded("reason")]`. | Per aggregate |
| `identity:openiddict` | The OpenID Connect provider's applications and scopes per tenant. A restore deletes every token and authorization, so every client signs in again. | Only with `OpenIddict:Enabled` |
| `pipelines:engine` and the pipeline files | Pipeline definitions, versions, runs and the engine's stores. | Only with `Pipelines:Enabled` |

The archive (`.dcbak`) is encrypted with AES-GCM under a key derived from the operator's passphrase (at least 12
characters) with PBKDF2. The passphrase is never stored. An archive restores on another host - it does not depend on the
host's data-protection keys - but **a lost passphrase makes the archive unreadable**.

Taking a snapshot (`admin/backup` > take a snapshot, or the schedule): a row in the catalogue is `Creating`, every
participant writes its part, and the archive is published with a SHA-256 sidecar; the row becomes `Available`, or
`Failed` with the exception type.

Restoring is in three steps:

1. **Pre-flight** - writes nothing. It checks the archive's checksum, the passphrase, the framework version (below), that
   every part has a participant on this host with the same schema and every participant has a part, every encrypted
   chunk, and each participant's own validation of its part.
2. **Stage** - a safety snapshot of the whole installation is taken (tag `pre-restore`), the target is marked `Staged`,
   and a request file is written beside the archives, protected with the host's data-protection keys.
3. **Apply at the next start** - the app's `Program` calls the framework's `InstallAsync`, which applies a staged restore
   after the database install or upgrade and before the host serves anything or starts any background job. Every
   participant replaces everything it owns with its part (a replace, never a merge), caches are cleared and search is
   re-indexed. If a participant fails, the safety snapshot is applied the same way; if that fails too, the host refuses to
   serve and logs the snapshot id.

**When a snapshot can be restored.** On the same Stomme version, or a later one when no Stomme release in between
changed data in an upgrade step; never on an older version. Then every part's schema must match: a module aggregate's
schema is the shape of its persisted properties (names and types, nested ones included), so **adding, removing, renaming
or retyping a property of an aggregate, renaming the aggregate's type or namespace, or adding a new aggregate makes every
earlier snapshot unrestorable on that build**. Pre-flight names the part and the reason.

A **tenant-scoped restore** puts back only the selected tenants. It is refused when a selected tenant shares a database
with one not selected, and on a RavenDB installation.

Before a framework upgrade started from the admin System Update page, with `Backup:Enabled` on, a `pre-upgrade` snapshot
is taken with the schedule's passphrase; the upgrade is refused when that passphrase is missing or the snapshot fails.

## Using it from an app

Nothing to do for module data: every aggregate registered with `AddModuleAggregatePersistence` is carried. Files in the
sites' folders are carried.

To leave an aggregate out - a cache, something rebuilt, a record that must survive a restore - mark the class:

```csharp
using System;
using Stomme.Backup;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;

[BackupExcluded("Rebuilt from the notes on every start.")]
public sealed class NoteIndexEntry : AggregateRoot<Guid>, ITenantOwned
{
    // ...
}
```

Data that is neither an aggregate nor a file in a site folder (for example files your module writes elsewhere on
disk) is not carried. Keep data in aggregates and site folders. If you must carry something else, implement
`IBackupParticipant` - `Name` (unique), `SchemaVersion` (change it whenever the part's format changes), `RestoreOrder`
(Stomme's module aggregates use 500), `ExportAsync`, `ValidateAsync` (writes nothing), `ReplaceAsync` (deletes
everything it owns and writes the part, honouring `BackupScope.TenantIds` and `IsPartial`) - and register it with
`services.TryAddEnumerable(ServiceDescriptor.Scoped<IBackupParticipant, YourParticipant>())`. Adding a participant makes
every earlier snapshot unrestorable, as a new aggregate does.

The Host-only HTTP surface, `api/Backup` (an unknown route while `Backup:Enabled` is off): list (`GET`), take a snapshot
(`POST` with `{"passphrase": "..."}`, 202), stage a restore (`POST {id}/restore` with the passphrase, 202) and read its
pre-flight report (`GET {id}/restore`), delete, download the encrypted archive, import an archive (multipart: the file
and the passphrase), read and save schedules. The 202 of a restore only means the pre-flight started - a wrong
passphrase is answered 202 too; the report then says `accepted: false` with the problem ("the passphrase is wrong, or
the archive was altered"), and only an accepted pre-flight marks the snapshot `Staged`.

## Configuration

Read from the host's configuration, the installation's override file included:

| Key | Default | Effect |
|---|---|---|
| `Backup:Enabled` | `false` | The surface, the `admin/backup` page, the schedule job, the boot-time restore and the pre-upgrade snapshot. Off, a staged restore is not applied. |
| `Backup:Schedule:Passphrase` | empty | The passphrase scheduled and pre-upgrade snapshots use. Without it the schedule takes nothing and says why. |
| `Backup:Schedule:PassphraseFile` | empty | A file holding the passphrase; wins over `Passphrase`. The way to give a container the secret. |
| `Backup:Archive:Provider` | `Local` | `Local` (the `backups` folder in the installation's data directory) or `S3`. Each snapshot remembers its store, so switching leaves earlier ones restorable. |
| `Backup:Archive:S3:Endpoint` | none | `http(s)` address of the S3-compatible store. Dialled directly. |
| `Backup:Archive:S3:Bucket` | none | Required for `S3`. |
| `Backup:Archive:S3:Region` | `us-east-1` | |
| `Backup:Archive:S3:ForcePathStyle` | `true` | |
| `Backup:Archive:S3:Prefix` | the installation id | Key prefix; set a fixed one so a rebuilt installation finds its archives. |
| `Backup:Archive:S3:PartSizeBytes` | 8388608 | Multipart part size; at least 5 MiB. |
| `Backup:Archive:S3:AccessKeyId`, `SecretAccessKey` | none | The key pair. |
| `Backup:Archive:S3:AccessKeyIdFile`, `SecretAccessKeyFile` | none | Files holding the key pair; each wins over its plain setting. |

With `S3` and an incomplete setting, a snapshot fails naming the missing setting and writes nothing locally. Schedules
(interval, keep-last-N, retention days) are set on the `admin/backup` page; the schedule job is created disabled and
also needs `Backup:Enabled`.

## Rules and pitfalls

- **Take a snapshot after every deploy that changes an aggregate.** Earlier snapshots stop being restorable on that build
  (see above). Keep the previous build to restore an older snapshot, then upgrade.
- **Keep the passphrase outside the installation** (a password manager or a secret store). Nothing can open an archive
  without it.
- **A restore needs a restart.** Staging changes nothing; the app applies it the next time it starts. Restart the app
  after staging, and tell users that the data after the snapshot will be gone.
- **OpenID Connect clients must sign in again** after a restore with `OpenIddict:Enabled`; refreshes fail with
  `invalid_grant` once their access tokens expire. Warn the client owners first.
- **Never write secrets into a backup part** you author; parts are encrypted, but archives are downloaded and moved.
- **A restore replaces, it does not merge.** Everything a participant owns that is not in the snapshot is gone after
  the restore - including data in an aggregate you added since.
- **A backup is not an erasure record.** A user erased after a snapshot comes back with a restore of that snapshot.

## Related

- [Files and folders](files-and-folders.md) - what lives in the carried file trees.
- [Module persistence](../persistence/module-persistence.md) - aggregates and `AddModuleAggregatePersistence`.
- [Install, run and upgrade](../operations/install-run-upgrade.md) and [Deploy](../operations/deploy.md) - the data
  directory, the override file, containers.
- [Configuration](../architecture/configuration.md) - switches.
- [Data rights](data-rights.md).
