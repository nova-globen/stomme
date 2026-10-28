---
name: stomme-store-files
description: Use when a module must keep files - store an upload, an attachment, a generated report or document privately per site, read or delete it later, serve it to the right people - or when "file upload", "attachment", "private file", "IFileRepository", "folder" or "FileIngestionGuard" comes up.
---

# Store files privately from a module

You will have a module service that keeps files in a Private folder of its own on each site, writes them through
Nova Globen Stomme's file-ingestion guard (extension allowlist, size cap, malware-scanner hook, folder capacity), records them as
Stomme `File` rows, reads and deletes them safely, and an endpoint that serves one only to people allowed to have it.
Read [files and folders](../../../docs/services/files-and-folders.md) first. Do not write to your own directory on disk:
files outside the site folders are not in backups, not under the capacity limits and not visible in Admin > File
Management.

If the files belong to one user and only they (and administrators) should see them, consider the user's own folder,
`Users/{userId}/`, instead of a module folder: it already has those permissions and is removed when the user is deleted.

## Steps

1. **The store.** In the `Server` project, create `Server/Modules/Notes/Files/NoteFileStore.cs`:

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using FileRow = Stomme.Models.File;

namespace Stomme.Application.Modules.Notes.Files
{
    /// <summary>
    /// The Notes module's files: a Private "Notes/" folder per site, Administrators only through Stomme's own file
    /// routes. Every read re-checks that the file is still in THIS site's folder - a file id alone proves nothing.
    /// </summary>
    public sealed class NoteFileStore(IFolderRepository folders, IFileRepository files, IFileIngestionGuard guard, SiteState siteState)
    {
        public const string FolderName = "Notes";

        public const string FolderPath = "Notes/";

        public async Task<Folder> EnsureFolderAsync(int siteId)
        {
            var existing = await folders.GetFolderAsync(siteId, FolderPath);
            if (existing is not null)
            {
                return existing;
            }

            var root = await folders.GetFolderAsync(siteId, string.Empty)
                ?? throw new InvalidOperationException($"Site {siteId} has no root folder.");

            return await folders.AddFolderAsync(new Folder
            {
                SiteId = siteId,
                ParentId = root.FolderId,
                Name = FolderName,
                Type = FolderTypes.Private,
                Path = FolderPath,
                Order = 10,
                ImageSizes = string.Empty,
                Capacity = 100, // MB; 0 = no limit
                IsSystem = true,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.Browse, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true),
                },
            });
        }

        /// <summary>Stores <paramref name="content"/> as <paramref name="fileName"/> (replacing a file of that name) and returns its row.</summary>
        public async Task<FileRow> SaveAsync(string fileName, Stream content, long length, CancellationToken ct = default)
        {
            var alias = siteState.Alias ?? throw new InvalidOperationException("No site is resolved in this scope.");
            var folder = await EnsureFolderAsync(alias.SiteId);
            var directory = await folders.GetFolderPathAsync(folder);

            var result = await guard.AcceptChunkAsync(new FileIngestionTarget(alias, folder, directory), fileName, 1, 1, content, length, ct);
            if (!result.IsAccepted)
            {
                throw new InvalidOperationException(result.IsRefused ? result.Message : "The file was not completed.");
            }

            var size = (int)new FileInfo(Path.Combine(directory, fileName)).Length;
            var row = await files.GetFileAsync(folder.FolderId, fileName);
            if (row is null)
            {
                return await files.AddFileAsync(new FileRow
                {
                    FolderId = folder.FolderId,
                    Name = fileName,
                    Extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant(),
                    Size = size,
                    ImageHeight = 0,
                    ImageWidth = 0,
                    Description = string.Empty,
                });
            }

            row.Size = size;
            return await files.UpdateFileAsync(row);
        }

        /// <summary>The file's path on disk, or null when the id is not a file of this site's Notes folder or its bytes are gone.</summary>
        public async Task<(FileRow File, string Path)?> FindAsync(int fileId)
        {
            var siteId = siteState.Alias?.SiteId ?? -1;
            var folder = await folders.GetFolderAsync(siteId, FolderPath);
            var file = await files.GetFileAsync(fileId);
            if (folder is null || file is null || file.FolderId != folder.FolderId)
            {
                return null;
            }

            var path = await files.GetFilePathAsync(file);
            return System.IO.File.Exists(path) ? (file, path) : null;
        }

        /// <summary>Removes the bytes AND the row. Does nothing for a file outside this site's Notes folder.</summary>
        public async Task DeleteAsync(int fileId)
        {
            if (await FindAsync(fileId) is { } found)
            {
                System.IO.File.Delete(found.Path);
                await files.DeleteFileAsync(found.File.FileId);
            }
            else if (await files.GetFileAsync(fileId) is { } orphan
                && await folders.GetFolderAsync(siteState.Alias?.SiteId ?? -1, FolderPath) is { } folder
                && orphan.FolderId == folder.FolderId)
            {
                await files.DeleteFileAsync(fileId); // the row of a file whose bytes are already gone
            }
        }
    }
}
```

2. **Register it** in `NotesServerStartup.ConfigureServices`, before the last registration
   (`AddTransient<INotesService, ServerNotesService>()` stays last):

```csharp
services.AddScoped<Stomme.Application.Modules.Notes.Files.NoteFileStore>();
```

3. **Tie each file to what it belongs to.** Here a file belongs to a note: its stored name starts with the note's id
   (`{noteId:N}-{original name}`), so the endpoint below can check the note before it serves the file. If your aggregate
   should know its files, keep the file id on it instead (for example `int? AttachmentFileId`). Either way the folder
   check in `FindAsync` is how you trust an id.

4. **Upload and serve from your own endpoint**, deciding who may there. Create
   `Server/Modules/Notes/Files/NoteFilesController.cs`. This one allows a note's author and holders of `notes:manage`
   (Administrators) - the same rule the Notes module uses for changing a note; anyone else gets 404, so a stranger learns
   nothing about which notes have files:

```csharp
#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Controllers;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Application.Modules.Notes.Files
{
    /// <summary>
    /// api/NoteFiles: a note's attachments, for the note's author and for holders of notes:manage only. A file belongs to a
    /// note by its stored name, which starts with the note's id.
    /// </summary>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class NoteFilesController : ModuleControllerBase
    {
        private readonly NoteFileStore _store;
        private readonly INoteRepository _notes;
        private readonly SiteState _siteState;
        private readonly ICurrentUser _currentUser;
        private readonly IAccessGuard _accessGuard;

        public NoteFilesController(NoteFileStore store, INoteRepository notes, SiteState siteState, ICurrentUser currentUser,
            IAccessGuard accessGuard, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _store = store;
            _notes = notes;
            _siteState = siteState;
            _currentUser = currentUser;
            _accessGuard = accessGuard;
        }

        // POST api/NoteFiles/{noteId} (multipart/form-data, one field "file")
        [HttpPost("{noteId:guid}")]
        [RequiresPermissionKey(NotesPermissions.Edit)]
        public async Task<IActionResult> Upload(Guid noteId, IFormFile file, CancellationToken ct)
        {
            if (await FindMyNoteAsync(noteId, ct) is null)
            {
                return NotFound();
            }

            if (file is null || file.Length == 0)
            {
                return BadRequest(new { error = "Choose a file to attach." });
            }

            var name = Prefix(noteId) + Path.GetFileName(file.FileName);
            try
            {
                await using var stream = file.OpenReadStream();
                var row = await _store.SaveAsync(name, stream, file.Length, ct);
                return Ok(new { fileId = row.FileId, name = row.Name, size = row.Size });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // GET api/NoteFiles/{noteId}/{fileId}
        [HttpGet("{noteId:guid}/{fileId:int}")]
        [RequiresPermissionKey(NotesPermissions.View)]
        public async Task<IActionResult> Download(Guid noteId, int fileId, CancellationToken ct)
        {
            if (await FindMyNoteAsync(noteId, ct) is null
                || await _store.FindAsync(fileId) is not { } found
                || !found.File.Name.StartsWith(Prefix(noteId), StringComparison.Ordinal))
            {
                return NotFound();
            }

            return PhysicalFile(found.Path, "application/octet-stream", found.File.Name.Substring(Prefix(noteId).Length));
        }

        private static string Prefix(Guid noteId) => noteId.ToString("N") + "-";

        // The note on this site, when the caller is its author or holds notes:manage; otherwise null.
        private async Task<Note?> FindMyNoteAsync(Guid noteId, CancellationToken ct)
        {
            var siteId = _siteState.Alias?.SiteId ?? -1;
            var note = siteId > 0 ? await _notes.GetForSiteAsync(siteId, noteId, ct) : null;
            if (note is null)
            {
                return null;
            }

            var isAuthor = _currentUser.UserId is int userId && note.AuthorUserId == userId;
            return isAuthor || await _accessGuard.IsAllowedAsync(null, NotesPermissions.Manage, ct) ? note : null;
        }
    }
}
```

   The upload is a POST, so a browser call needs the antiforgery token in the `X-XSRF-TOKEN-HEADER` header (see
   [API and client services](../../../docs/modules/api-and-client-services.md)). From a component, post the file through
   your module's client service; Stomme's own file manager control uploads into a folder through the same guard, with
   the folder's `Edit` permission deciding who may (see [components and controls](../../../docs/ui/components-and-controls.md)).

5. **Delete with what owns them.** Files outlive the note unless you remove them: when a note is deleted, call
   `NoteFileStore.DeleteAsync` for each of its files in the delete handler, and do the same in the module's erasure
   participant (the [stomme-support-erasure-and-export](../stomme-support-erasure-and-export/SKILL.md) skill) -
   Stomme removes only the user's own `Users/{userId}/` folder.

## Verify

1. Build, run, sign in as the host. Create a second user and sign in as them; write a note.
2. As that user, `POST api/NoteFiles/{noteId}` with a small `.txt` (multipart field `file`): 200 with `fileId`.
3. Admin > File Management: the folder `Notes` exists, is Private, and holds `{noteId}-name.txt` with its size.
4. `GET api/NoteFiles/{noteId}/{fileId}`: the author gets the bytes, the host gets them (notes:manage), a third user gets
   404, an anonymous caller 403. The third user's upload to the author's note is 404 too.
5. Stomme's own route `/files/id/{fileId}` serves the file to the host only (the folder's `View` is Administrators);
   the author and anonymous callers get 403. The file is not under the web root, so no static path serves it.
6. Upload a name whose extension is not in the site's uploadable list (for example `.exe`): 400 "... is invalid or its
   extension is not uploadable."; the folder is unchanged.
7. Set `Files:MaxUploadBytes` to `1024` in the configuration (the override file, or `Server/appsettings.json`), restart,
   upload a 2 KB file: 400 "... is 2048 bytes or more, over the 1024-byte upload cap." Put the setting back.

## Common mistakes

- **The folder is Public.** Its permission rows apply (its static path redirects to the checked `/files/` route), but
  a module's own files belong in a Private folder, outside the web root. Use `FolderTypes.Private`.
- **Trusting a file id.** Without the folder check in `FindAsync` and the note check in the controller, any id of any
  file on the site would be served. Keep both.
- **Writing the bytes yourself for user uploads.** That skips the allowlist, the cap, the scanner and the capacity.
  Use `IFileIngestionGuard`.
- **Bytes on disk, no row (or the reverse).** The guard writes bytes only; `AddFileAsync` writes the row only;
  `DeleteFileAsync` deletes the row only. Do both halves.
- **Attachments left behind.** Deleting or erasing the note does not touch its files; delete them yourself (step 5).
- **"No site is resolved in this scope" in a job.** Set the site's alias in the job's per-site scope before using the
  store ([jobs](../../../docs/services/jobs.md)).
- **Refused with `Capacity`.** The folder's `Capacity` (MB) is reached; raise it in Admin > File Management or in
  `EnsureFolderAsync`.
- **A file over 2 GB.** `File.Size` is an `int`; it cannot be stored.
- **A `<video>` or `<audio>` element that must send a token.** It cannot set headers. With a cookie-signed-in page the
  cookie is enough; for a token, copy `?access_token=` into the `Authorization` header in an `IServerPipelineStartup` at
  stage `BeforeJwt` - `IServerStartup.Configure` runs after authentication and is too late. Example:
  [Middleware earlier in the pipeline](../../../docs/architecture/startup-and-discovery.md#middleware-earlier-in-the-pipeline).
  A token in a URL can end up in proxy and browser logs: use a short-lived one.
