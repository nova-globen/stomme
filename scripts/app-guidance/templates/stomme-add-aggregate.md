---
name: stomme-add-aggregate
description: Use when a module needs to store data - adding an entity, a table, a record type, a domain object or an aggregate, persisting something to the database, or adding a repository - in a Nova Globen Stomme app; the storage-blind, tenant-owned aggregate that works on every database the installer offers.
---

# Add an aggregate to a module

You will have a storage-blind, tenant-owned aggregate in a module's `Server` project, a repository interface with the
module's reads, and the registration that lets Stomme store it on whichever database the installation chose (SQLite,
SQL Server, LocalDB, Azure SQL, PostgreSQL or RavenDB). There is no `DbContext`, no migration and no provider code. How
it works: [module persistence](../../../docs/persistence/module-persistence.md); the commands that use it:
[the application layer](../../../docs/modules/application-layer.md).

The steps use the reference **Notes** module of [stomme-create-module](../stomme-create-module/SKILL.md), whose server
code lives in `Server/Modules/Notes/`; the code below is that module's, unchanged. Replace `Notes`/`Note` with your module
and aggregate. The module must already exist (its `ModuleInfo` in `Client`, its `NotesServerStartup` in `Server`); if it
does not, run [stomme-create-module](../stomme-create-module/SKILL.md) first.

## Steps

1. **Design the aggregate before writing it.** Decide:
   - Its consistency boundary: what must change together. One aggregate is saved by one repository call; there is no
     transaction across two.
   - Its key: a `Guid`, generated in the factory.
   - Its scope: always a tenant (`ITenantOwned`), and almost always a site (`SiteId`). Data that belongs to one placed
     module instance also carries `ModuleId`.
   - Which text, if any, you will look rows up by. Each such field needs a normalised copy (here `TitleKey`).
   - A class name no other module or Stomme itself is likely to use: it becomes the table name in a store all modules
     share.

2. **Write the aggregate** in `Server/Modules/Notes/Domain/Note.cs`:

   ```csharp
{{FILE:Server/Modules/Notes/Domain/Note.cs|3}}
   ```

   Rules this follows, each required: a private parameterless constructor; every stored property `{ get; private set; }`;
   `TenantId` from the caller (the handler reads it from `ITenantContext`); the id set in the factory; every change
   through a method that keeps the invariants; `DateTime` values in UTC. A value object property must be an
   `IStringValueObject` (see [the application layer](../../../docs/modules/application-layer.md)); a list property must be
   a public `IReadOnlyList<T>` over a private field named `_camelCase`.

3. **Write the repository** in `Server/Modules/Notes/Persistence/NoteRepository.cs`. It wraps the host's
   `IRepository<Note, Guid>` and adds the module's reads; every read is one site's.

   ```csharp
{{FILE:Server/Modules/Notes/Persistence/NoteRepository.cs|3}}
   ```

   Only non-string comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=` on `int`, `long`, `Guid`, `enum`, `bool`, `DateTime`
   and their nullable forms) and the normalised-key comparison above may appear in a `FindAsync` predicate. No
   `.ToLower()`, no `StartsWith`, no `Contains` on a string.

4. **Register both** in the module's `Server/Modules/Notes/Startup/NotesServerStartup.cs`, inside `ConfigureServices`:

   ```csharp
   services.AddModuleAggregatePersistence<Note, Guid>();
   services.AddScoped<INoteRepository, NoteRepository>();
   ```

   with `using System;`, `using Stomme.Application.Modules.Notes.Domain;`,
   `using Stomme.Application.Modules.Notes.Persistence;` and `using Microsoft.Extensions.DependencyInjection;`. If the
   module has no server startup yet, this is the whole file for now (the complete one, with the handlers, is in
   [stomme-create-module](../stomme-create-module/SKILL.md)):

   ```csharp
   using System;
   using Stomme.Application.Modules.Notes.Domain;
   using Stomme.Application.Modules.Notes.Persistence;
   using Stomme.Infrastructure;
   using Microsoft.AspNetCore.Builder;
   using Microsoft.AspNetCore.Hosting;
   using Microsoft.Extensions.DependencyInjection;

   namespace Stomme.Application.Modules.Notes.Startup
   {
       public class NotesServerStartup : IServerStartup
       {
           public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
           {
           }

           public void ConfigureMvc(IMvcBuilder mvcBuilder)
           {
           }

           public void ConfigureServices(IServiceCollection services)
           {
               services.AddModuleAggregatePersistence<Note, Guid>();
               services.AddScoped<INoteRepository, NoteRepository>();
           }
       }
   }
   ```

   Call `AddModuleAggregatePersistence` once per aggregate. Do not add a `DbContext`, a `DbSet`, a store class or a
   check of the database type.

5. **Use it** from a command handler - [stomme-add-command-or-query](../stomme-add-command-or-query/SKILL.md). The
   handler stamps the tenant (`ITenantContext.CurrentTenantId`, refused when null) and the site
   (`SiteState.Alias.SiteId`), writes through the repository and calls `IUnitOfWork.SaveChangesAsync`.

6. **Changing an aggregate later.** A new property on an aggregate whose table already exists becomes a NULLABLE column,
   and the existing rows hold NULL: declare the property nullable (`int?`, `DateTime?`, `string`) or give the rows a
   value when they are next written. Renaming a property gives an empty new column; the old one stays. Nothing is ever
   dropped.

## Verify

1. `dotnet build` from the app's root: no errors, no new warnings in your files.
2. `cd Server`, `dotnet run`, and open the site. On a new installation, complete the installer.
3. Read the host's console output (these lines are written through .NET logging at startup, not to Admin > Event Log).
   There must be NO line
   "Unable to initialize the ModuleAggregatesDbContext module store; it will read as empty." and NO line
   "Module aggregate ... is not tenant-owned". On SQLite, PostgreSQL and SQL Server the store creates a table named after
   the class (`CREATE TABLE "Note"` in the console on a new installation, `[Note]` on SQL Server); on an installation
   that existed before the aggregate, there is one line "Module store ModuleAggregatesDbContext: created 1 missing table(s) ...: Note".
4. Create a note through the module (after the command and endpoint skills), stop the app, start it again, and read it
   back: it survived the restart.
5. As a second site of the same installation (Admin > Site Management), the first site's notes do not appear.
6. If the installation is SQLite, the table is in the database `ConnectionStrings:ModuleAggregates` names, and in
   `Server/Data/Stomme.ModuleAggregates.db` when that key is not set; on RavenDB it is a collection in the tenant's
   database.
7. Before calling it done on a module that will run on several databases, run it once on PostgreSQL or RavenDB as well:
   that is where text comparisons and nullability differ from SQLite.

## Common mistakes

- **Every module, Stomme's included, suddenly reads nothing; the log says "Unable to initialize the
  ModuleAggregatesDbContext module store".** Your aggregate cannot be mapped: a property typed as a value object that is
  not `IStringValueObject`, a list with neither a setter nor a private backing field (`_camelCase`), an element type
  with no usable constructor, or a class name another aggregate already uses. The exception logged with the line names
  the property (for a duplicate class name, the colliding types). Fix the aggregate; all modules share that store.
- **A "not tenant-owned" warning at startup.** The aggregate does not implement `ITenantOwned`. Its rows are visible
  to every tenant. Add the interface and the `TenantId` property, and stamp it in the handler.
- **Rows from another site appear.** A read without `SiteId`, or a `GetByIdAsync` whose result was not checked against
  the site. Use `ListForSiteAsync` / `GetForSiteAsync`.
- **A lookup by name works on SQLite and fails on PostgreSQL or RavenDB** (or the reverse). A raw string comparison in
  `FindAsync`. Compare a normalised column against the same normalisation of the argument.
- **`NotSupportedException: FindAsync is implemented by the provider repositories`.** The wrapper repository does not
  forward `FindAsync`. Add the forwarding member from step 3.
- **The host does not start in Development, naming `INoteRepository` or a handler.** A registration is missing, or a
  scoped service is injected into a singleton. Register the repository in the same change as the class.
- **Reading old rows fails after adding a property.** The new column is NULL in existing rows and the property is not
  nullable. Make it nullable.
- **Two edits of the same note, the second fails with `ConcurrencyConflictException`.** That is the concurrency check
  working: the note changed since it was read. Answer 409 and let the user reload.
