# Portable, searchable, migratable modules

The host asks a placed module for things it cannot know by itself: its content for an export, the text for the site search,
the addresses for the sitemap, and the work to do when the module's version changes. A module answers through one server
class, its **server manager**, by implementing the interfaces below. Nothing is registered: the module definition names the
class, and the host creates it when it needs it.

## How it works

### The server manager

1. `ModuleDefinition.ServerManagerType` names the class as an assembly-qualified string, for example
   `"Stomme.Application.Modules.Notes.Manager.NotesManager, Stomme.Application.Server.Stomme"`.
2. Wherever the host needs it, it resolves the name with `Type.GetType`, checks which interfaces the class implements, and
   creates it with `ActivatorUtilities.CreateInstance` in the current scope - so its constructor can take any registered
   service (your repositories, `IMediator`, `Stomme.Repository.IPageRepository`, ...). Do not register the manager itself.
3. A name that does not resolve is ignored for search, sitemap and export (logged as an error only by the upgrade step). Check
   the name after any rename.

| Interface | Namespace (package) | Asked by |
| --- | --- | --- |
| `IPortable` | `Stomme.Modules` (`Stomme.Server`) | Export Content / Import Content, page templates with `Content`, site configuration packages |
| `ISearchable` | `Stomme.Interfaces` (`Stomme.Shared`) | The site search's index job |
| `ISitemap` | `Stomme.Modules` (`Stomme.Server`) | `/sitemap.xml` |
| `ISynchronizable` | `Stomme.Modules` (`Stomme.Server`) | The site-group synchronization job |
| `IInstallable` | `Stomme.Infrastructure` (`Stomme.Server`) | The module upgrade step at startup, and deleting the module definition |

### Export and import (`IPortable`)

```csharp
Task<string> ExportModuleAsync(Stomme.Models.Module module);
Task ImportModuleAsync(Stomme.Models.Module module, string content, string version);
```

1. When `ServerManagerType` implements `IPortable`, the host sets `ModuleDefinition.IsPortable`, and the placement's actions
   menu (edit mode) offers **Import Content** and **Export Content**. Both need `Edit` on the page.
2. Export: the host builds a `Stomme.Models.ModuleContent` - `ModuleDefinitionName`, `Version` (the module's), the
   instance's non-private settings, and `Content`, the string your `ExportModuleAsync` returns - and serializes it to JSON.
   Your content can be any string; JSON with a schema number is the practical choice.
3. Import: the host checks the `ModuleDefinitionName` matches, writes the exported settings onto the instance, then calls
   `ImportModuleAsync(module, content, version)` with the EXPORTED version, so you can read older shapes.
4. `module.IPortableContext` tells you why you are called: `"Export Module"`, `"Import Module"`, `"Site Template"` (a page
   template's `Content`, imported when the host creates the placement), `"Copy Page"` (a page copied with its modules:
   exported from the source placement, imported into the copy) or `"Global Replace"` (Admin > Global Replace exports,
   rewrites and re-imports the content). `module.Settings` holds the instance's settings;
   `module.SiteId` and `module.ModuleId` say where the content belongs.
5. An exception thrown by your implementation is caught by the host: an export returns empty content, an import reports
   failure. Log the cause yourself.

The same implementation is used when a site's configuration is exported or imported as a package; see
[Data rights](../services/data-rights.md).

### Site search (`ISearchable`)

```csharp
Task<List<Stomme.Models.SearchContent>> GetSearchContentsAsync(Stomme.Models.PageModule pageModule, DateTime lastIndexedOn);
```

1. The search index job runs per site. For every placement whose module's server manager implements `ISearchable`, it calls
   `GetSearchContentsAsync`. `lastIndexedOn` is the last run's time, or `DateTime.MinValue` when the page, the placement or an
   earlier placement on the same page changed since then - return everything in that case.
2. Return one `SearchContent` per item: `EntityName` (a name for your kind of item), `EntityId` (its id as a string), `Title`,
   `Description`, `Body`, `Url` (the item's address, see below) and `ContentModifiedOn`. Left empty, `Url` becomes the
   placement's page path and `Permissions` the module's and page's `View`.
3. The job only adds and replaces entries (by `EntityName` and `EntityId`). An item you stop returning stays in the index;
   return it once with `IsDeleted = true`, or remove its entry when you delete it (see the sample below).
4. A placement whose manager does not implement `ISearchable` is indexed as the module alone.
5. Search results are trimmed by the placement's view permission. Anything you return is findable by everyone who can view
   the placement, so never return drafts or private rows.

See [Search](../services/search.md) for the index and the query side.

### Sitemap (`ISitemap`)

```csharp
Task<List<Stomme.Models.Sitemap>> GetUrlsAsync(string alias, string path, Stomme.Models.Module module);
```

The sitemap page asks every placed module a visitor may view. `alias` is the alias's path (empty at a host's root), `path`
the page's path. Build each `Url` with `Stomme.Shared.Utilities.NavigateUrl(alias, path, Utilities.AddUrlParameters(...))`
and set `ModifiedOn`.

Each site has its own sitemap: `https://host/sitemap.xml` for a site at the host's root, `https://host/<folder>/sitemap.xml`
for a site in a folder (each lists only its own pages since 10.78.0; before, a folder site's answered the root's). The
login, register, reset, 404 and search pages are never listed. `robots.txt` (a root site's only) is the site's `Robots`
setting, or `User-agent: *`, plus a `Sitemap:` line with the request's scheme unless the setting names one already; behind
a proxy that scheme is `https` only when the proxy sends `X-Forwarded-Proto`, so put the sitemap's full address in the
setting when it is not.

### Upgrades (`ReleaseVersions` and `IInstallable`)

```csharp
Task<bool> InstallAsync(Stomme.Models.Tenant tenant, string version);
Task<bool> UninstallAsync(Stomme.Models.Tenant tenant);
```

1. `ModuleDefinition.ReleaseVersions` lists every version of the module that needs work, oldest first:
   `"1.0.0,1.1.0,2.0.0"`. When it is empty or older than `Version`, the host uses `Version` alone.
2. At startup, after the database is installed or upgraded, the host compares the module's recorded version (one for the
   whole installation, its module definition's `Version`) with the list. For each later version, in order, it sets each
   tenant in turn and calls `InstallAsync(tenant, version)` on the server manager. A module seen for the first time, and a
   tenant being installed, runs every listed version.
3. When every call returns `true`, the recorded version moves to the last listed version. A `false` or an exception is logged
   and the version is not moved, so the step is tried again on the next start - make `InstallAsync` idempotent.
4. When the server manager does not implement `IInstallable`, the host instead looks for an embedded SQL script named
   `{module namespace}.{version}.sql` in the manager's assembly and runs it if present. A storage-blind module needs neither.
5. `UninstallAsync` is called per tenant when a Host user deletes the module definition in Admin > Module Management.

A storage-blind module does not migrate its schema: the host creates the tables for its aggregates and adds a missing column
(nullable) when an aggregate gains a property. See [Module persistence](../persistence/module-persistence.md). Use
`IInstallable` for data changes between versions - backfilling a new property, renaming a stored value.

### Per-site upgrade work

Two contracts run once per site at startup:

- `Stomme.Infrastructure.ISiteContentReconciler` - `string Name { get; }` and
  `Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct)`. Register it scoped in your
  `IServerStartup`. It runs for every site of every tenant, in a fresh scope with the site's alias set; `context` carries
  `Services`, `Alias`, `Site` and `TenantId`. A failure is logged with `Name` and contained to that site. Write it ADDITIVELY:
  create what the site lacks and never re-process what exists, because it runs on every start. This is the asynchronous
  choice for per-site content a module must carry.
- `Stomme.Infrastructure.ISiteMigration` with `[SiteMigration("*", "1.0.0")]` (`Stomme.Infrastructure.SiteMigrationAttribute`,
  alias name or `"*"`, version or `"*"`) - discovered in every `Stomme` assembly and run while the version is higher than
  the site's configuration version. Its `Up(Site site, Alias alias)` is SYNCHRONOUS, so it cannot await a store without
  blocking, which Nova Globen Stomme forbids. Prefer `ISiteContentReconciler`.

### Site-group synchronization (`ISynchronizable`)

`ExtractModuleAsync(module, lastSynchronizedOn)` returns the content changed since a date, and `LoadModuleAsync(module, content)`
applies it to the matching placement on a secondary site. Implement it only when your module takes part in site groups.

## Using it from an app

`Server/Modules/Notes/Manager/NotesManager.cs`, for the Notes module of
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) (export, import and search; the
[skill](../../.claude/skills/stomme-module-import-export-search/SKILL.md) adds the upgrade step):

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Application.Modules.Notes.Persistence;
using Stomme.Interfaces;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Application.Modules.Notes.Manager
{
    public sealed class NotesManager(
        INoteRepository notes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        TimeProvider clock) : IPortable, ISearchable
    {
        public const int ContentSchema = 1;

        public const string SearchEntityName = "Note";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public async Task<string> ExportModuleAsync(Stomme.Models.Module module)
        {
            var items = (await notes.ListForSiteAsync(module.SiteId))
                .OrderBy(note => note.CreatedUtc)
                .Select(note => new NotesContentItem(note.Title, note.Body))
                .ToList();
            return JsonSerializer.Serialize(new NotesContent(ContentSchema, items), Json);
        }

        public async Task ImportModuleAsync(Stomme.Models.Module module, string content, string version)
        {
            var package = JsonSerializer.Deserialize<NotesContent>(content ?? string.Empty, Json);
            if (package is null || package.Schema != ContentSchema)
            {
                throw new InvalidOperationException($"This is not notes content of schema {ContentSchema}.");
            }

            var tenantId = tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved.");
            var author = currentUser.UserId ?? throw new InvalidOperationException("Notes are imported by a signed-in user.");
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var item in package.Notes ?? new List<NotesContentItem>())
            {
                if (await notes.FindByTitleAsync(module.SiteId, item.Title) is null)
                {
                    await notes.AddAsync(Note.Create(Guid.NewGuid(), tenantId, module.SiteId, author, item.Title, item.Body, now));
                }
            }

            await unitOfWork.SaveChangesAsync();
        }

        public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn) =>
            (await notes.ListForSiteAsync(pageModule.Module.SiteId))
                .Where(note => note.UpdatedUtc >= lastIndexedOn)
                .Select(note => new SearchContent
                {
                    EntityName = SearchEntityName,
                    EntityId = note.Id.ToString(),
                    Title = note.Title,
                    Body = note.Body,
                    ContentModifiedOn = note.UpdatedUtc,
                })
                .ToList();

        public sealed record NotesContent(int Schema, List<NotesContentItem>? Notes);

        public sealed record NotesContentItem(string Title, string Body);
    }
}
```

and in `ModuleInfo`:

```csharp
ServerManagerType = "Stomme.Application.Modules.Notes.Manager.NotesManager, Stomme.Application.Server.Stomme",
```

The import writes through the repository rather than through the module's gated commands: the host has already checked
`Edit` on the page for Import Content, and anything stricter is yours to check. A Note needs an author, so this import
takes the signed-in user and refuses to run without one - which also means the Notes module cannot seed notes from a page
template's `Content` (imported at startup, with no user). Titles are unique per site, so a note the site already has is
skipped rather than imported twice.

A deleted note must also leave the search: the index job only adds and replaces entries, so the module's delete handler
removes the entry itself with `Stomme.Repository.ISearchContentRepository`:
`await searchContents.DeleteSearchContentAsync(note.SiteId, NotesManager.SearchEntityName, note.Id.ToString());` (the site's entry only - the overload without a site matches the first entry of that id in the database, whatever its site). That writes the database
index directly; a site set to another search provider (Admin > Search) keeps the entry. To go through the site's provider,
as the index job does, send a deleted marker through `Stomme.Services.ISearchService` (the server's own is injected on
the server): `await search.SaveSearchContentsAsync(new() { new SearchContent { SiteId = siteId, EntityName = NotesManager.SearchEntityName, EntityId = note.Id.ToString(), IsDeleted = true } }, new());`.
The step-by-step
version, driven end to end, is
[stomme-module-import-export-search](../../.claude/skills/stomme-module-import-export-search/SKILL.md).

## Rules and pitfalls

- **Name the manager by string, and check the string.** A typo makes every feature on this page silently absent: no Import /
  Export menu items, no search results, no sitemap entries, no upgrade calls.
- **Never index or export what a viewer must not see.** Search results and sitemap entries go to everyone who may view the
  placement.
- **Deleted items stay findable unless you remove them.** The index job adds and replaces entries by `EntityName` and
  `EntityId`; it removes one only when an item comes back with `IsDeleted = true`. A module that deletes rows outright
  removes the entry when it deletes the row, as above.
- **Scope every read to the site you were given** (`module.SiteId`, `pageModule.Module.SiteId`). The tenant is already
  resolved by the host; the site is your filter.
- **Exceptions are swallowed by the host** for export, import and indexing (the index job records them in its log). Log in
  your own code with the cause.
- **Upgrade hooks run at every start until they succeed.** Make them idempotent and keep `ReleaseVersions` in order.
- **Do not block in `ISiteMigration.Up`.** It is synchronous; `.Result`, `.Wait()` and `.GetAwaiter().GetResult()` deadlock under
  real databases.
- **Keep the exported shape versioned.** Old exports are imported by new code; refuse a schema you do not know with a clear
  message rather than importing half of it.

## Related

- [Module anatomy](anatomy.md)
- [Settings and templates](settings-and-templates.md) - page templates with `Content`
- [Search](../services/search.md)
- [Data rights](../services/data-rights.md) - site configuration packages, a user's data, erasure
- [Backup and restore](../services/backup-restore.md) - a storage-blind module's aggregates are backed up with no code
- [Module persistence](../persistence/module-persistence.md)
- Skill: [stomme-module-import-export-search](../../.claude/skills/stomme-module-import-export-search/SKILL.md)
