using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Database.PostgreSQL;
using Stomme.Database.Sqlite;
using Stomme.Database.SqlServer;
using Stomme.Databases.Interfaces;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Stomme.Tests
{
    // The Pages cluster on the persistence seam (D3/D7/D19). Same two hazards as Sites — the aggregate is
    // mapped onto a schema this context does not migrate, and HasColumnName outranks the snake_case rewriter
    // — plus one Pages has and Sites did not: PageModule used to hold an EF navigation INTO Page, which makes
    // EF discover Page as a second entity type on the Page table. See PageModuleMustNotResurrectPageTests.
    public class PageAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildPageEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(PageAggregate))!;
        }

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit column name
        // actually breaks: its identifiers are case-sensitive, so "PageId" != page_id.
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildPageEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("page", entityType.GetTableName());
            // The explicitly-named column — the one that would silently keep its PascalCase.
            Assert.Equal("page_id", columns[nameof(PageAggregate.Id)]);
            // Convention-named columns, proving the rewriter still runs around the explicit one.
            Assert.Equal("default_container_type", columns[nameof(PageAggregate.DefaultContainerType)]);
            Assert.Equal("is_navigation", columns[nameof(PageAggregate.IsNavigation)]);
            Assert.Equal("parent_id", columns[nameof(PageAggregate.ParentId)]);
            // D58. The lowercase lookup key added and backfilled by 10020005_AddPageNormalizedColumn — derived
            // in PageAggregate.Path's setter so it cannot drift from `path`, and mapped by convention exactly
            // as UrlMapping.UrlNormalized (D57) and Folder.PathNormalized (D49).
            Assert.Equal("path_normalized", columns[nameof(PageAggregate.PathNormalized)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildPageEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("Page", entityType.GetTableName());
            Assert.Equal("PageId", columns[nameof(PageAggregate.Id)]);
            Assert.Equal("DefaultContainerType", columns[nameof(PageAggregate.DefaultContainerType)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token and the harvested Page table
        // has no column for it. Unlike Site there is no name collision to make the breakage loud, so if it
        // ever gets mapped EF silently expects a `version` column that does not exist and every read fails.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildPageEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The tripwire for the next agent. Page (the wire contract) and PageAggregate (the persisted model)
        // are two hand-maintained lists of the same columns. Add a column to Page, forget the aggregate, and
        // the install still boots — the value just never persists. This fails instead.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Page).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // PageId is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == nameof(Page.PageId) ? nameof(PageAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildPageEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name)
                // PathNormalized (D58) is a storage-only lowercase lookup key with NO wire-contract
                // counterpart — exactly as Folder.PathNormalized / File.NameNormalized (D49),
                // UrlMapping.UrlNormalized (D57) and Identity's NormalizedUserName. It is derived in
                // PageAggregate.Path's setter and is never mapped back onto the Page contract, so it is
                // legitimately on the aggregate and legitimately absent from the contract. Its column mapping
                // is pinned by The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql.
                .Where(name => name != nameof(PageAggregate.PathNormalized))
                .OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Page
            {
                PageId = 7,
                SiteId = 3,
                Path = "about/team",
                ParentId = 4,
                Name = "Team",
                Title = "Our Team",
                Order = 5,
                Url = "https://example.test/team",
                ThemeType = "Theme, Asm",
                DefaultContainerType = "Container, Asm",
                HeadContent = "<meta />",
                BodyContent = "<script></script>",
                Icon = "oi oi-person",
                IsNavigation = true,
                IsClickable = true,
                IsPersonalizable = true,
                UserId = 42,
                EffectiveDate = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                ExpiryDate = new DateTime(2027, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                IsDeleted = true,
                DeletedBy = "someone",
                DeletedOn = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = PageMapper.ToModel(PageMapper.ToAggregate(original, assignId: true));

            // Compare every persisted member reflectively, so a new column cannot be quietly left out.
            foreach (var property in typeof(Page).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        [Fact]
        public void An_insert_leaves_the_identity_to_the_database()
        {
            var aggregate = PageMapper.ToAggregate(new Page { PageId = 99, Name = "New" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    // The rule Pages paid for, and the one Modules must not undo.
    //
    // PageModule.Page WAS an EF navigation (eager-loaded with .Include(item => item.Page)). A navigation to
    // Page makes EF discover Page as an entity type mapped to the "Page" table — which PageAggregate now
    // owns — and the model fails hard:
    //
    //   "The table 'page' cannot be used for entity type 'PageAggregate' since it is being used for entity
    //    type 'Page' and potentially other entity types, but there is no linking relationship."
    //
    // That is a STARTUP failure, not a subtle one, but it is easy to reintroduce by adding an innocuous-
    // looking navigation to any model that shares TenantDBContext. These tests pin both halves: the
    // navigation stays [NotMapped], and a context holding DbSet<PageModule> alongside the framework store
    // still maps Page exactly once.
    public class PageModuleMustNotResurrectPageTests
    {
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            // Folder is the unmigrated neighbour. The PageModule CONTRACT stood here until D47 put PageModule
            // on the seam: it is mapped as PageModuleAggregate by ConfigureFrameworkStore now, so declaring
            // the contract would put two entity types on the `page_module` table and break the model. The
            // reference that matters to THIS class — page_module -> page, by id and never by navigation — is
            // still proven below, just through the aggregate.
            // Folder stood here until D48, File until D49, and the SearchContent family until D50 —
            // declaring any of them now would map its table twice and break the model.
            //
            // THIS CONTEXT IS NOW EMPTY, and that is the END OF THE PATTERN rather than a gap. The neighbour
            // DbSet was never what the probe rested on: measured both ways, removing it leaves the assertions
            // green, and re-adding a migrated contract still fails them with EF's "the table '...' cannot be
            // used for entity type" — so the proof comes from ConfigureFrameworkStore plus the assertion,
            // exactly as PermissionMustNotBeResurrectedTests has shown since D48. MigrationHistory is NOT a
            // substitute: it is [Keyless] on its own __EFMigrationsHistory table, which no aggregate maps, so
            // it could never collide and would prove nothing. The reflective sweep below is what carries the
            // protection now, and it needed no edit across D47, D48, D49 or D50.

            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IModel BuildModel()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new TenantLikeContext(builder.Options, database);
            return context.Model;
        }

        // Mutation check: give PageModuleAggregate a `public Page Page { get; set; }` navigation and this
        // fails (the model throws while building) — which is exactly the protection being asserted. Until D47
        // the same check was "remove [NotMapped] from PageModule.Page", back when the PageModule CONTRACT was
        // the entity this context tracked; the hazard is identical, only the type carrying it changed.
        [Fact]
        public void The_page_table_is_owned_by_the_aggregate_alone()
        {
            var model = BuildModel();

            var mappedToThePageTable = model.GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "page")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(PageAggregate)], mappedToThePageTable);
        }

        [Fact]
        public void Page_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(Page)));
        }

        // PageModule moved to the seam in D47. This test used to read
        // "Page_module_is_still_a_plain_entity_referencing_the_page_by_id" and assert over the PageModule
        // CONTRACT; the contract is not an entity type any more, so the assertion is made over the AGGREGATE
        // that replaced it. What is being pinned is unchanged and is D19's, not D47's: whatever entity holds
        // the page_module row must reach the Page cluster BY ID, because a navigation would rediscover Page as
        // a second entity type on the `page` table and break the model at startup.
        [Fact]
        public void The_page_module_row_reaches_the_page_by_id_and_never_by_navigation()
        {
            var model = BuildModel();

            // The contract is gone from the model — D47 replaced it with the aggregate.
            Assert.Null(model.FindEntityType(typeof(PageModule)));

            var pageModule = model.FindEntityType(typeof(PageModuleAggregate))!;
            Assert.Equal("page_module", pageModule.GetTableName());
            Assert.NotNull(pageModule.FindProperty(nameof(PageModuleAggregate.PageId)));
            // The aggregate has no Page member at all to navigate through — the strongest form of the
            // guarantee, and stronger than the [NotMapped] the contract relied on.
            Assert.Empty(pageModule.GetNavigations());
        }
    }

    // The behaviour of the migrated repository's store, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class PageStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Page table as the tenant migrations leave it: PageEntityBuilder.BuildTable plus every later
        // AddColumn (IsClickable, HeadContent, BodyContent, EffectiveDate, ExpiryDate), minus the dropped
        // ones (EditMode, LayoutType, Meta). PascalCase — the SQLite provider has no rewriter. "Order" is
        // quoted because it is a reserved word, exactly as the migration builder emits it.
        private const string PageTableDdl = """
            CREATE TABLE "Page" (
                "PageId" INTEGER NOT NULL CONSTRAINT "PK_Page" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "Path" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Title" TEXT NULL,
                "ThemeType" TEXT NULL,
                "Icon" TEXT NOT NULL,
                "ParentId" INTEGER NULL,
                "Order" INTEGER NOT NULL,
                "IsNavigation" INTEGER NOT NULL,
                "Url" TEXT NULL,
                "UserId" INTEGER NULL,
                "IsPersonalizable" INTEGER NOT NULL,
                "DefaultContainerType" TEXT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "DeletedBy" TEXT NULL,
                "DeletedOn" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "IsClickable" INTEGER NULL,
                "HeadContent" TEXT NULL,
                "BodyContent" TEXT NULL,
                "EffectiveDate" TEXT NULL,
                "ExpiryDate" TEXT NULL,
                "PathNormalized" TEXT NULL
            );
            """;

        private sealed class StoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private sealed class StubTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        public PageStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = PageTableDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<PageAggregate, int> NewStore(DbContext context) =>
            new EfRepository<PageAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static Page NewPage(string name, string path, int order = 1, int? parentId = null, int? userId = null) => new()
        {
            SiteId = 1,
            Name = name,
            Path = path,
            Icon = "",
            Order = order,
            ParentId = parentId,
            UserId = userId,
            IsNavigation = true,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_page_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = PageMapper.ToAggregate(NewPage("Home", ""), assignId: false);

            await store.AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("Home", loaded.Name);
            Assert.Equal("", loaded.Path);
        }

        // Reads go through IQuerySession.Query<T> (D7: the aggregate repository has no IQueryable). This is
        // the exact call PageRepository.GetPages makes — including the UserId == null filter that excludes
        // personalized pages, which is easy to lose in translation and would leak other users' pages.
        [Fact]
        public async Task Pages_are_read_through_a_query_session_excluding_personalized_pages()
        {
            using (var seed = NewContext())
            {
                var store = NewStore(seed);
                await store.AddAsync(PageMapper.ToAggregate(NewPage("Home", ""), assignId: false));
                await store.AddAsync(PageMapper.ToAggregate(NewPage("Mine", "mine", userId: 5), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var names = session.Query<PageAggregate>()
                .Where(item => item.SiteId == 1 && item.UserId == null)
                .ToList()
                .ConvertAll(item => item.Name);

            Assert.Equal(["Home"], names);
        }

        // PageRepository.GetPage(path, siteId) — the SiteRouter's lookup on every request.
        [Fact]
        public async Task A_page_is_found_by_path_within_its_site()
        {
            using (var seed = NewContext())
            {
                await NewStore(seed).AddAsync(PageMapper.ToAggregate(NewPage("About", "about"), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var found = session.Query<PageAggregate>().FirstOrDefault(item => item.Path == "about" && item.SiteId == 1);
            var wrongSite = session.Query<PageAggregate>().FirstOrDefault(item => item.Path == "about" && item.SiteId == 2);

            Assert.Equal("About", found!.Name);
            Assert.Null(wrongSite);
        }

        [Fact]
        public async Task An_updated_page_persists_through_the_store()
        {
            int pageId;
            using (var seed = NewContext())
            {
                var aggregate = PageMapper.ToAggregate(NewPage("Before", "before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                pageId = aggregate.Id;
            }

            // PageRepository updates a DETACHED aggregate rebuilt from the wire contract — the same shape the
            // harvested repository used (db.Entry(page).State = Modified), so every column is written.
            using (var context = NewContext())
            {
                var page = NewPage("After", "after");
                page.PageId = pageId;
                page.HeadContent = "<meta name=\"probe\" />";
                await NewStore(context).UpdateAsync(PageMapper.ToAggregate(page, assignId: true));
            }

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(pageId))!;
            Assert.Equal("After", loaded.Name);
            Assert.Equal("<meta name=\"probe\" />", loaded.HeadContent);
        }

        [Fact]
        public async Task A_deleted_page_is_removed_through_the_store()
        {
            int pageId;
            using (var seed = NewContext())
            {
                var aggregate = PageMapper.ToAggregate(NewPage("Doomed", "doomed"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                pageId = aggregate.Id;
            }

            using (var context = NewContext())
            {
                var store = NewStore(context);
                var aggregate = (await store.GetByIdAsync(pageId))!;
                await store.DeleteAsync(aggregate);
            }

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(pageId));
        }

        // WHY PageRepository.GetPage reads through the query session instead of IPageStore.GetByIdAsync.
        //
        // The seam's IRepository.GetByIdAsync TRACKS the aggregate. PageRepository then updates a DETACHED
        // aggregate rebuilt from the wire contract, and EF refuses to attach a second instance with the same
        // key. PageController.Put walks straight into it: GetPage(id) to authorize, then UpdatePage(page),
        // both on one repository instance == one store == one DbContext.
        //
        // Found by driving a real PUT against a PostgreSQL install, not by a test: the write silently did
        // nothing. This pins the store behaviour that makes reading-via-the-store wrong.
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            int pageId;
            using (var seed = NewContext())
            {
                var aggregate = PageMapper.ToAggregate(NewPage("Before", "before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                pageId = aggregate.Id;
            }

            using var context = NewContext();
            var store = NewStore(context);

            await store.GetByIdAsync(pageId); // tracks the aggregate

            var page = NewPage("After", "before");
            page.PageId = pageId;

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.UpdateAsync(PageMapper.ToAggregate(page, assignId: true)));
            Assert.Contains("cannot be tracked", conflict.Message);
        }

        // ...and the shape PageRepository actually uses: read through a query session (its own context, so
        // the store's change tracker stays clean), write through the store. Mutation check: point the read
        // at NewStore(context).GetByIdAsync and this fails with the identity conflict above.
        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            int pageId;
            using (var seed = NewContext())
            {
                var aggregate = PageMapper.ToAggregate(NewPage("Before", "before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                pageId = aggregate.Id;
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            using var context = NewContext();
            var store = NewStore(context);

            // PageController.Put: var currentPage = _pages.GetPage(page.PageId, false)
            await using (var session = sessions.OpenQuerySession())
            {
                Assert.NotNull(session.Query<PageAggregate>().FirstOrDefault(item => item.Id == pageId));
            }

            // ...then _pages.UpdatePage(page)
            var page = NewPage("After", "before");
            page.PageId = pageId;
            await store.UpdateAsync(PageMapper.ToAggregate(page, assignId: true));

            using var readContext = NewContext();
            Assert.Equal("After", (await NewStore(readContext).GetByIdAsync(pageId))!.Name);
        }

        public void Dispose() => _connection.Dispose();
    }
}
