---
name: stomme-module-import-export-search
description: Use when a module's content must be exportable and importable ("Export Content", "Import Content", seed a page with content), findable in the site search, listed in the sitemap, or upgraded between module versions ("server manager", "IPortable", "ISearchable", "ISitemap", "IInstallable", "ReleaseVersions").
---

# Make a module portable, searchable and upgradeable

You will have given a module (the Notes module of `stomme-create-module` in the examples) a **server manager**: one class
in `Server` that exports and imports the module's content, feeds the site search and runs upgrade steps between versions -
and, where the module's page serves one address per item, lists sitemap entries. The host creates the class by the name the
module definition gives it; nothing is registered. Read
[Portable, searchable, migratable](../../../docs/modules/portable-searchable-migratable.md) first.

## Steps

1. Create `Server/Modules/Notes/Manager/NotesManager.cs`:

   ```csharp
   #nullable enable
   using System;
   using System.Collections.Generic;
   using System.Linq;
   using System.Text.Json;
   using System.Threading.Tasks;
   using Stomme.Application.Modules.Notes.Domain;
   using Stomme.Application.Modules.Notes.Persistence;
   using Stomme.Infrastructure;
   using Stomme.Interfaces;
   using Stomme.Models;
   using Stomme.Modules;
   using Stomme.Security.Abstractions;
   using Stomme.SharedKernel.Domain;
   using Stomme.Tenancy.Abstractions;
   using Microsoft.Extensions.Logging;

   namespace Stomme.Application.Modules.Notes.Manager
   {
       /// <summary>The Notes server manager: content export and import, the site search, and the upgrade step.</summary>
       public sealed class NotesManager(
           INoteRepository notes,
           IUnitOfWork unitOfWork,
           ITenantContext tenantContext,
           ICurrentUser currentUser,
           TimeProvider clock,
           ILogger<NotesManager> logger) : IPortable, ISearchable, IInstallable
       {
           public const int ContentSchema = 1;

           // The EntityName of every search entry this module writes; deleting a note removes the entry by this name.
           public const string SearchEntityName = "Note";

           private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

           // Export Content: the site's notes, oldest first. Notes belong to a site, so every placement on it exports the same.
           public async Task<string> ExportModuleAsync(Stomme.Models.Module module)
           {
               var items = (await notes.ListForSiteAsync(module.SiteId))
                   .OrderBy(note => note.CreatedUtc)
                   .Select(note => new NotesContentItem(note.Title, note.Body))
                   .ToList();
               return JsonSerializer.Serialize(new NotesContent(ContentSchema, items), Json);
           }

           // Import Content. Refuses a shape it does not know; skips a note whose title the site already has.
           public async Task ImportModuleAsync(Stomme.Models.Module module, string content, string version)
           {
               try
               {
                   var package = JsonSerializer.Deserialize<NotesContent>(content ?? string.Empty, Json);
                   if (package is null || package.Schema != ContentSchema)
                   {
                       throw new InvalidOperationException($"This is not notes content of schema {ContentSchema}.");
                   }

                   var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved.");
                   // A note has an author. A page template's Content is imported at startup, with no user: refuse it.
                   var author = currentUser.UserId ?? throw new InvalidOperationException("Notes are imported by a signed-in user.");
                   var now = clock.GetUtcNow().UtcDateTime;
                   foreach (var item in package.Notes ?? new List<NotesContentItem>())
                   {
                       if (await notes.FindByTitleAsync(module.SiteId, item.Title) is not null)
                       {
                           continue;
                       }

                       await notes.AddAsync(Note.Create(Guid.NewGuid(), tenantId, module.SiteId, author, item.Title, item.Body, now));
                   }

                   await unitOfWork.SaveChangesAsync();
               }
               catch (Exception ex)
               {
                   // The host swallows the exception and reports only that the import failed: log the cause here.
                   logger.LogError(ex, "Importing notes into module {ModuleId} failed: {Message}", module.ModuleId, ex.Message);
                   throw;
               }
           }

           // The site search. lastIndexedOn is DateTime.MinValue when everything must be re-indexed.
           public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn) =>
               (await notes.ListForSiteAsync(pageModule.Module.SiteId))
                   .Where(note => note.UpdatedUtc >= lastIndexedOn)
                   .Select(note => new SearchContent
                   {
                       EntityName = SearchEntityName,
                       EntityId = note.Id.ToString(),
                       Title = note.Title,
                       Description = note.Body.Length <= 200 ? note.Body : note.Body.Substring(0, 200),
                       Body = note.Body,
                       ContentModifiedOn = note.UpdatedUtc,
                   })
                   .ToList();

           // Runs at startup, once per tenant, for each version in ReleaseVersions above the module's recorded one.
           public Task<bool> InstallAsync(Tenant tenant, string version)
           {
               if (version == "1.1.0")
               {
                   // A data change between versions goes here, through the repository. It must be idempotent.
                   logger.LogInformation("Notes upgrade step {Version} ran for tenant {Tenant}.", version, tenant.Name);
               }

               return Task.FromResult(true);
           }

           public Task<bool> UninstallAsync(Tenant tenant) => Task.FromResult(true);

           public sealed record NotesContent(int Schema, List<NotesContentItem>? Notes);

           public sealed record NotesContentItem(string Title, string Body);
       }
   }
   ```

   The search entries carry no `Url`, so the host links each one to the page the placement is on (`/notes`).
   `Stomme.Models.Module` is written in full on purpose: a bundled module package whose namespace starts with the app's
   first segment (for example `MyCompany.Module.X` in an app `MyCompany.MyProject`) otherwise turns `Module` into that
   namespace and the class stops compiling.

2. Name the manager in `Client/Modules/Notes/ModuleInfo.cs`, inside the `ModuleDefinition` initializer:

   ```csharp
   ServerManagerType = "Stomme.Application.Modules.Notes.Manager.NotesManager, Stomme.Application.Server.Stomme",
   ```

   It is a string on purpose: `Client` cannot reference `Server`.

3. Take a deleted note out of the search. The index job only adds and replaces entries; a note that no longer exists is
   simply not returned, and its entry stays findable. In `DeleteNoteCommandHandler`
   (`Server/Modules/Notes/Application/NoteRequests.cs`) take `Stomme.Repository.ISearchContentRepository searchContents`
   as one more constructor parameter and, after `await unitOfWork.SaveChangesAsync(ct);`:

   ```csharp
   await searchContents.DeleteSearchContentAsync(note.SiteId, Manager.NotesManager.SearchEntityName, note.Id.ToString());
   ```

   This removes the site's entry from the search's database index (the default search provider). Pass the site: the
   overload without one matches the first entry of that id in the database, whatever its site.

4. Optional - seed content on the page template. `PageTemplateModule.Content` takes an export of the module (the whole JSON
   the Export Content page shows), and is imported once, when the host creates the placement - at startup, with no user.
   The Notes import above refuses that (a note needs an author), so the Notes module cannot seed notes this way. A module
   whose items need no user can.

5. Optional - an upgrade step. `NotesManager` already implements `Stomme.Infrastructure.IInstallable`. When version 1.1.0
   needs a data change, set in `ModuleInfo`: `Version = "1.1.0"` and `ReleaseVersions = "1.0.0,1.1.0"`, and put the change
   in `InstallAsync` under `version == "1.1.0"`. Schema changes need no step: the host adds a missing column for a new
   aggregate property. See [Module persistence](../../../docs/persistence/module-persistence.md).

6. Optional - sitemap entries. Add `ISitemap` (namespace `Stomme.Modules`) and
   `Task<List<Sitemap>> GetUrlsAsync(string alias, string path, Module module)` only when the page shows one item at its own
   address (see [Module UI](../../../docs/modules/module-ui.md#parameterised-addresses-inside-one-page)). The Notes page has
   no per-note address, so it lists none.

## Verify

1. `dotnet build`, then `cd Server` and `dotnet run`. Sign in as the host and add two notes on `/notes`.
2. Switch the page to edit mode and open the Notes placement's actions menu: **Import Content** and **Export Content** are
   there (they are absent when `ServerManagerType` does not resolve; `GET api/ModuleDefinition?siteid=1` shows `isPortable`
   true for Notes). On Export Content (`/notes/*/{moduleId}/Export`) choose **Export**: the Content box shows JSON with
   `ModuleDefinitionName`, `Version`, `Settings` and `Content` - your notes, as a JSON string inside it.
3. Copy it, delete the notes (or open a Notes placement on another site), and paste it into **Import Content**
   (`/notes/*/{moduleId}/Import`), then **Import**: "Content Imported Successfully", and the notes are back. Import the same
   wrapper with `"Content":"{\"schema\":7}"`: "A Problem Was Encountered Importing Content..." appears, nothing is added,
   and the server log has "Importing notes into module ... failed: This is not notes content of schema 1."
4. Wait for the search index job (it runs every minute; Admin > Scheduled Jobs, "Search Index Job", shows "Items Indexed"
   in its log), then open `/search?q=` with a word of a note: the note is found by its title, linking to `/notes`.
5. Delete that note and search again: it is no longer found - immediately, without waiting for the job.
6. For the upgrade step: raise the version as in step 5, restart, and confirm the server log shows "Notes upgrade step 1.1.0
   ran" once and no "An Error Occurred Executing IInstallable Interface"; restart again and the line does not repeat.

## Common mistakes

- **No Import/Export items, no search results.** `ServerManagerType` is wrong: namespace, class name or assembly name
  (`Stomme.Application.Server.Stomme`). The host ignores a name it cannot resolve.
- **Export returns empty content; import says it failed, with no detail.** The host catches your exception. Log inside the
  manager, as above.
- **A deleted item is still found by the search.** Nothing removed its entry; see step 3.
- **Notes of every site in one export.** Filter by `module.SiteId`; the store separates tenants, not sites.
- **Importing twice doubles the notes - or the import fails on the second note.** Notes titles are unique per site; skip
  what the site already has, as above.
- **Drafts or private rows in search results.** Everything `GetSearchContentsAsync` returns is shown to every user who may
  view the placement. Return only what all of them may read.
- **An import through the module's commands is refused.** Commands with `[Requires]` need a signed-in user holding the key; a
  page template's `Content` is imported at startup with no user. Write through the repository, as above, and validate
  through the aggregate's own factory.
- **The upgrade step runs at every start.** It returns `false` or throws. Fix the cause; keep it idempotent meanwhile.
- **Constructor parameters the container does not have.** The manager is created with `ActivatorUtilities`; every parameter
  must be a registered service.
