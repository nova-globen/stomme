# Search

Each site has a full-text index of its pages and of the content of the modules placed on them. A background job keeps
the index up to date every minute; the site's search page (`search`) queries it and shows each visitor only results they
may view. A module puts its own content in the index by giving its server manager the `ISearchable` interface.

## How it works

The types (all in `Stomme.Shared`):

| Type | Namespace | What it is |
|---|---|---|
| `ISearchable` | `Stomme.Interfaces` | `Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn)` |
| `SearchContent` | `Stomme.Models` | One indexed item: `EntityName`, `EntityId`, `Title`, `Description`, `Body`, `Url`, `Permissions`, `ContentModifiedBy`, `ContentModifiedOn`, `AdditionalContent`, `IsDeleted` (and `SiteId`, `CreatedOn`, set by the job) |
| `SearchQuery`, `SearchResults`, `SearchResult` | `Stomme.Models` | A query (`SiteId`, `Alias`, `Keywords`, `IncludeEntities`, `ExcludeEntities`, `FromDate`, `ToDate`, `PageIndex`, `PageSize`, `SortField`, `SortOrder`, `BodyLength`) and its answer |
| `ISearchProvider` | `Stomme.Services` | The index store: `Name`, `GetSearchResultsAsync`, `SaveSearchContent`, `DeleteSearchContent(siteId)` |
| `ISearchResultsService` | `Stomme.Services` (`Stomme.Client`) | What a component calls: `GetSearchResultsAsync(SearchQuery)`, over `POST api/SearchResults` |

The **Search Index Job** runs every minute, per tenant. For each site with indexing enabled it:

1. Sets the site's alias for the scope.
2. Reads the site's `Search_LastIndexedOn`. If it is empty (never indexed, or an administrator pressed Reindex), it
   deletes the site's whole index first.
3. Indexes every page changed since then (title and path), skipping the paths in `Search_IgnorePages` and the `login`,
   `register`, `reset` and `404` pages.
4. For every module placed on a page: if the module's `ModuleDefinition.ServerManagerType` names a type that implements
   `ISearchable`, it creates that type with dependency injection (`ActivatorUtilities`, so constructor parameters come
   from the container) and calls `GetSearchContentsAsync(pageModule, since)`. `since` is `DateTime.MinValue` when the page
   or the placement changed since the last run (return everything), otherwise the last run's time (return what changed
   after it). A module whose manager is not `ISearchable` is indexed as one item with its title only.
5. Fills the blanks of each returned item from the placement: `EntityName` = `Module`, `EntityId` = the module id, `Title`
   = the page title, `Url` = the page path, `Permissions` = `Module:{moduleId},Page:{pageId}`, `ContentModifiedOn` = the
   placement's. An item on a deleted or expired page or placement is marked deleted.
6. Saves each item through the site's provider, then stores the run's start time as `Search_LastIndexedOn`.

The stock provider, `DatabaseSearchProvider`, stores the content and a word index in the tenant's database. Saving an item
first deletes any entry with the same `EntityName` and `EntityId`; an item with `IsDeleted = true` is only deleted.

At query time `ISearchService` asks the provider, then **trims by permission**: an item's `Permissions` is a
comma-separated list, each entry either `EntityName:EntityId` (the user needs `View` on that entity on the site) or a
role name (the user must be in it). An item is shown only when every entry passes. Results are grouped by URL.

## Using it from an app

1. Give the module a server manager that implements `ISearchable` (`Server/Modules/Notes/NotesManager.cs`). This one
   indexes the `Note` aggregate from [module persistence](../persistence/module-persistence.md):

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Application.Modules.Notes.Domain;
using Stomme.Interfaces;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Application.Modules.Notes
{
    public sealed class NotesManager(IRepository<Note, Guid> notes) : ISearchable
    {
        public async Task<List<SearchContent>> GetSearchContentsAsync(PageModule pageModule, DateTime lastIndexedOn)
        {
            var siteId = pageModule.Module.SiteId;
            var changed = await notes.FindAsync(note => note.SiteId == siteId && note.UpdatedUtc >= lastIndexedOn);
            return changed
                .Select(note => new SearchContent
                {
                    EntityName = "Note",
                    EntityId = note.Id.ToString(),
                    Title = note.Title,
                    Body = note.Body,
                    ContentModifiedOn = note.UpdatedUtc,
                })
                .ToList();
        }
    }
}
```

2. Name it in the module's `ModuleInfo` (in the `Client` project) with its assembly-qualified name. The app's server
   assembly is `Stomme.Application.Server.Stomme`:

```csharp
ServerManagerType = "Stomme.Application.Modules.Notes.NotesManager, Stomme.Application.Server.Stomme",
```

3. Place the module on a page. Content of a module that sits on no page is never indexed.

Items you leave `Url` and `Permissions` empty on take the page's path and the module's and page's `View` permissions, so
a result is shown only to people who can see the page and the module. To link to one item, set `Url` to the page path
plus your own parameters (for example `/notes/!/note/{id}`), and keep the permissions of the placement.

Querying from a component: inject `ISearchResultsService` and send a `SearchQuery` with `SiteId`, `Alias`, `Keywords`,
`FromDate = DateTime.MinValue`, `ToDate = DateTime.MaxValue`, `PageIndex`, `PageSize` and a `BodyLength` for the
snippet. `FromDate` and `ToDate` bound the content's modification time; left unset (or at `MinValue` / `MaxValue`) they
bound nothing (since 10.73.2; before, a query that left `ToDate` unset found nothing). The site already has a search page at `search`; link to it before you build another.

The module-level hooks (`ISearchable`, `ISitemap`, `IPortable`) are described together in
[portable, searchable, migratable](../modules/portable-searchable-migratable.md).

## Configuration

Per site, at Admin > Search Settings (`admin/search`), stored as site settings:

| Setting | Default | Effect |
|---|---|---|
| `Search_Enabled` | `true` | The job indexes the site. |
| `Search_SearchProvider` | `DatabaseSearchProvider` | The `ISearchProvider.Name` to use; an unknown name falls back to the default. |
| `Search_IgnorePages` | empty | Comma-separated page paths not indexed. |
| `Search_IgnoreEntities` | `File` | Comma-separated `EntityName`s not indexed. Files are not indexed by default. |
| `Search_MinimumWordLength` | 3 | Shortest word indexed. The admin page writes this key (since 10.73.2; before, the misspelt `Search_MininumWordLength`, which the index still reads when the correct key is absent). |
| `Search_IgnoreWords` | a list of common English words | Words not indexed. |
| `Search_LastIndexedOn` | empty | The last run (UTC). The Reindex button clears it; setting it earlier re-indexes from that time. |

## Rules and pitfalls

- **Return what changed since `lastIndexedOn`, by your own timestamp.** Editing your aggregate does not touch the page
  or the placement, so the job passes the last run's time; content you do not return is not re-indexed. Keep an
  "updated" UTC instant on the aggregate and filter on it. Content written before the manager was added is indexed only
  after a Reindex (Admin > Search Settings).
- **Deletions are yours.** An entry stays in the index until an item with the same `EntityName` and `EntityId` arrives
  with `IsDeleted = true`, or the site is re-indexed. If your module deletes rows, either return a deleted marker for them
  or ask an administrator to Reindex.
- **Make `EntityName` and `EntityId` unique.** The provider replaces by that pair across the tenant's index, not per
  site. Use a module-specific `EntityName` and a Guid id.
- **`Permissions` entries must parse.** An `EntityName:EntityId` entry needs an integer id; anything else throws during
  the query. A role-name entry is a plain role name such as `Registered Users`.
- **Do not index what the page's viewers may not read.** The trim is by the entries in `Permissions`, not by your
  module's own rules. Leave out private or per-user content, or give it permissions that only the right people pass.
- **A manager failure is logged in the job's history**, not thrown: open Admin > Scheduled Jobs > Search Index Job to
  see "Error Indexing Module ...".
- **The manager is created fresh for every placement of the module in a run**, from the job's scope with the site's
  alias set. Inject repositories and services; do not keep state in it.
- **`ServerManagerType` must name the server assembly.** A wrong type name is silently treated as "not searchable".

## Related

- [Portable, searchable, migratable](../modules/portable-searchable-migratable.md) - the module hooks together.
- [Module anatomy](../modules/anatomy.md) - `ModuleDefinition` and placement.
- [Module persistence](../persistence/module-persistence.md) - `FindAsync` and its safe predicates.
- [Background jobs](jobs.md) - how the index job runs.
