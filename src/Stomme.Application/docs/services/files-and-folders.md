# Files and folders

Every site has a tree of folders holding files, managed at Admin > File Management (`admin/files`). Each folder has
permission rows that decide who may browse, view and change it, a type (Public or Private) that decides where its bytes
live on disk, and a capacity. Every path that brings bytes into a folder - an upload, an upload by URL, an unzip - goes
through one ingestion guard: extension allowlist, size cap, malware-scanner hook, archive caps and folder capacity.

## How it works

The model (`Stomme.Shared`):

| Type | Namespace | Members an app uses |
|---|---|---|
| `Folder` | `Stomme.Models` | `FolderId`, `SiteId`, `ParentId`, `Type`, `Name`, `Path` (ends with `/`; the root is `""`), `Order`, `ImageSizes`, `Capacity` (MB, `0` = no limit), `IsSystem`, `PermissionList` |
| `File` | `Stomme.Models` | `FileId`, `FolderId`, `Name`, `Extension` (lower case, no dot), `Size` (an `int`, bytes), `ImageHeight`, `ImageWidth`, `Description`, `Folder` |
| `FolderTypes` | `Stomme.Shared` | `Private`, `Public` |
| `Permission` | `Stomme.Models` | `new Permission(permissionName, roleName, isAuthorized)`, `new Permission(permissionName, userId, isAuthorized)` |
| `PermissionNames`, `RoleNames` | `Stomme.Shared` | `Browse`, `View`, `Edit`; `Admin` (Administrators), `Registered`, `Everyone`, `Host` |

Where the bytes live, per folder type (the server's `IFolderRepository.GetFolderPathAsync(folder)` returns the path):

- **Private**: `<content root>/Content/Tenants/{tenantId}/Sites/{siteId}/{folder path}`. Only reachable through
  Nova Globen Stomme's file routes, which check the folder's `View` permission.
- **Public**: the same layout under the **web root**, but never served as a static file: a request for
  `/Content/Tenants/{tenantId}/Sites/{siteId}/{folder path}{name}` is redirected to the same file's `/files/...` route
  on its own site, which checks the folder's `View` permission (another tenant's or site's path answers 404). So a
  Public folder's permission rows protect its files like a Private folder's. (Before 10.73.0 the static path served the
  bytes to anyone, whatever the permissions said.)

The server-side services (`Stomme.Server`, namespace `Stomme.Repository`, registered transient):

- `IFolderRepository` - `GetFoldersAsync(siteId)`, `GetFolderAsync(folderId)`, `GetFolderAsync(siteId, path)`,
  `AddFolderAsync(folder)` (writes the folder's permission rows from `PermissionList`), `UpdateFolderAsync`,
  `DeleteFolderAsync`, `GetFolderPathAsync(folder)`.
- `IFileRepository` - `GetFilesAsync(folderId)`, `GetFileAsync(fileId)`, `GetFileAsync(folderId, fileName)`,
  `GetFileAsync(siteId, folderPath, fileName)`, `AddFileAsync(file)`, `UpdateFileAsync(file)`, `DeleteFileAsync(fileId)`,
  `GetFilePathAsync(file)`. The repository keeps the ROW; it does not write or delete the bytes.

The ingestion guard (`Stomme.Server`, namespace `Stomme.Infrastructure`, scoped):

- `IFileIngestionGuard.AcceptChunkAsync(FileIngestionTarget target, string fileName, int partNumber, int totalParts, Stream content, long length, CancellationToken)`
  - one part of an upload; parts are 1-based, a single-part upload is `1` of `1`.
- `IFileIngestionGuard.DownloadAsync(target, url, fileName, ct)` - fetches a URL into the folder.
- `IFileIngestionGuard.ExtractZipAsync(target, zipPath, ct)` - unpacks an archive already on disk.
- `IFileIngestionGuard.IsAllowedFileNameAsync(alias, fileName)` and `ExceedsCapacityAsync(folder, incomingBytes, replacing)`.
- `FileIngestionTarget(Alias Alias, Folder? Folder, string Directory)` - the site, the folder row and its directory.
- `FileIngestionResult` - `IsAccepted`, `IsPending` (a multi-part upload still arriving), `IsRefused`, `Files` (the names
  now in the folder), `Skipped` (archive entries left out for their name or extension), `Reason`
  (`FileIngestionRefusalReason`), `Message`.

Every path stages the bytes outside every folder, then checks in this order: the file name and its extension against the
site's allowlist, the size cap (for an archive the entry count, then each entry's size and the total and ratio caps as
it decompresses), the scanner, then the folder's capacity. An archive entry whose name or extension is not allowed is
skipped, not refused: the rest of the archive is still placed and the entry is listed in `Skipped`. Only when all pass is the file moved into the folder, replacing a file of the same name in one step. A refusal
leaves the folder exactly as it was. The guard places bytes; it does not create the `File` row - the caller adds or
updates it.

Upload by URL (`POST api/File/upload/url`, the file manager's "upload from URL") goes through the host's outbound
connection guard: only `http`/`https`, no redirects, and no loopback, private, link-local or similar destinations unless
the host name is in `Files:UploadByUrl:AllowedHosts`.

The download routes check the folder's `View` permission for the current user and that the file belongs to the
request's site: `/files/{folder path}{name}`, `/files/id/{fileId}` (add `?download` for an attachment), and
`api/File/download/{fileId}`. `Utilities.FileUrl(alias, fileId)` (`Stomme.Shared`) builds the `/files/id/` form.

Per-user folders: every user of a site has `Users/{userId}/` ("My Folder"), Private, with Browse, View and Edit for the
user and View for Administrators, capacity 20 MB. Deleting the user from the site deletes that folder and its files.

From a Razor component, use the client services (`Stomme.Services`, in `Stomme.Client`): `IFileService`
(`GetFilesAsync`, `GetFileAsync`, `UploadFileAsync(url, folderId, name)`, `DownloadFileAsync`, `DeleteFileAsync`, ...)
and `IFolderService`. They call the API above, so the current user's permissions apply. The file picker and upload
controls are described in [components and controls](../ui/components-and-controls.md).

## Using it from an app

The usual shape for a module that keeps files of its own: one Private folder per site, created on first use, with
permission rows for the people who may see it through Stomme's routes, and a module service that writes through the
guard, adds the `File` row, and re-checks on every read that the file is still in that folder.

Creating the module's folder (inside a scoped service that has `IFolderRepository folders` injected):

```csharp
var folder = await folders.GetFolderAsync(siteId, "Notes/");
if (folder is null)
{
    var root = await folders.GetFolderAsync(siteId, string.Empty)
        ?? throw new InvalidOperationException($"Site {siteId} has no root folder.");
    folder = await folders.AddFolderAsync(new Folder
    {
        SiteId = siteId,
        ParentId = root.FolderId,
        Name = "Notes",
        Type = FolderTypes.Private,
        Path = "Notes/",
        Order = 10,
        ImageSizes = string.Empty,
        Capacity = 100,
        IsSystem = true,
        PermissionList = new List<Permission>
        {
            new Permission(PermissionNames.Browse, RoleNames.Admin, true),
            new Permission(PermissionNames.View, RoleNames.Admin, true),
            new Permission(PermissionNames.Edit, RoleNames.Admin, true),
        },
    });
}
```

Writing bytes through the guard and recording the row:

```csharp
var alias = siteState.Alias ?? throw new InvalidOperationException("No site is resolved.");
var directory = await folders.GetFolderPathAsync(folder);
var result = await guard.AcceptChunkAsync(new FileIngestionTarget(alias, folder, directory), fileName, 1, 1, content, length, ct);
if (result.IsRefused)
{
    throw new InvalidOperationException(result.Message);
}

var row = await files.GetFileAsync(folder.FolderId, fileName);
var size = (int)new FileInfo(Path.Combine(directory, fileName)).Length;
if (row is null)
{
    row = await files.AddFileAsync(new Stomme.Models.File
    {
        FolderId = folder.FolderId,
        Name = fileName,
        Extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant(),
        Size = size,
        Description = string.Empty,
    });
}
else
{
    row.Size = size;
    row = await files.UpdateFileAsync(row);
}
```

The complete service, registration, a download endpoint and verification are in the
[stomme-store-files](../../.claude/skills/stomme-store-files/SKILL.md) skill.

A replacement malware scanner: implement `IFileScanner` (`Stomme.Infrastructure`:
`Task<FileScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken)`, returning
`FileScanResult.Clean` or `new FileScanResult(false, "threat name")`) and register it in a module's startup with
`services.AddSingleton<IFileScanner, YourScanner>()`. The framework registers its pass-through `NullFileScanner` earlier
with `TryAdd`, so the later registration is the one resolved. A scanner that throws refuses the file.

## Configuration

Installation-wide, the `Files` section, read on every use:

| Key | Default | Effect |
|---|---|---|
| `Files:MaxUploadBytes` | 104857600 (100 MB) | One file, from any path. Must be above 0 and below 2147483647 (`File.Size` is an `int`); outside that range the guard throws naming the key. |
| `Files:Unzip:MaxEntries` | 1000 | Entries in one archive, skipped ones included. |
| `Files:Unzip:MaxTotalBytes` | 1073741824 (1 GB) | Decompressed bytes of one archive, counted while decompressing. |
| `Files:Unzip:MaxEntryRatio` | 100 | Decompressed / compressed bytes of one entry. |
| `Files:Unzip:RatioFloorBytes` | 65536 | The ratio is enforced only after an entry has produced this many bytes. |
| `Files:Chunks:OrphanMinutes` | 60 | Age after which an incomplete multi-part upload is swept. |
| `Files:UploadByUrl:AllowedHosts` | empty | Host names an upload by URL may reach even when they resolve to an internal address. |

Per site (Admin > File Management, its settings): `UploadableFiles`, the comma-separated extensions any path accepts
(default: images, common office, media and text formats, `zip`, `csv`, `json`, `md` and others). A folder's `Capacity`
is set per folder.

## Rules and pitfalls

- **Permissions decide, whatever the folder type.** A Public folder's static path redirects to `/files/...`, which checks
  `View`. Still prefer `FolderTypes.Private` for a module's own files: nothing about it suggests "for everyone", and the
  bytes stay outside the web root.
- **A file id is not proof.** Before returning or deleting a file your module stored, re-check that `file.FolderId` is
  your folder on the request's site. Anyone can send any id.
- **Stomme's routes authorise by folder `View`, not by owner.** If one person's files must be invisible to another in
  the same role, do not rely on the folder rows: keep the file id on your own aggregate, serve the bytes from your own
  endpoint, and decide there.
- **Write through the guard for anything a user supplied.** Writing the bytes yourself skips the extension allowlist,
  the size cap, the scanner and the capacity check. For bytes your module generates itself (an export, a report), writing
  directly into the folder path is acceptable; still add the `File` row.
- **The extension allowlist applies to the guard.** A name whose extension is not in the site's `UploadableFiles` is
  refused (`Reason = Extension`), including files your module writes through the guard; in an archive such an entry is
  skipped instead.
- **Rows and bytes are separate.** `DeleteFileAsync` removes the row; delete the bytes at `GetFilePathAsync(file)` too.
- **Files belong to a site.** Always pass the request's site (`SiteState.Alias.SiteId`); in a background job, set the
  site's alias first (see [jobs](jobs.md)).
- **Files over 2 GB cannot be represented** (`File.Size` is an `int`).
- **Files are in backups.** The `Content/Tenants` trees under the content root and the web root are part of every backup
  snapshot (see [backup and restore](backup-restore.md)); files you write elsewhere are not.
- **Erasure.** Only `Users/{userId}/` is removed when a user is deleted. Files your module stored about a person in its
  own folder are yours to erase (see [data rights](data-rights.md)).

## Related

- [stomme-store-files](../../.claude/skills/stomme-store-files/SKILL.md) - a module that stores files privately.
- [Components and controls](../ui/components-and-controls.md) - the file manager and upload controls.
- [Identity and permissions](../security/identity-and-permissions.md) - roles and permission rows.
- [Security posture](../security/security-posture.md) - the outbound connection guard.
- [Backup and restore](backup-restore.md), [Data rights](data-rights.md).
