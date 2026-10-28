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
// System.Reflection (needed for the parity/round-trip tests) also defines a Module type.
using Module = Stomme.Models.Module;

namespace Stomme.Tests
{
    // The Modules cluster on the persistence seam (D3/D7/D21). Same hazards as Sites and Pages — the
    // aggregate is mapped onto a schema this context does not migrate, and HasColumnName outranks the
    // snake_case rewriter — plus the one with the biggest blast radius so far: PageModule held an EF
    // navigation INTO Module that was ALSO load-bearing as a database-side site filter. See
    // ModuleMustNotBeResurrectedTests and TheSiteFilterOnPageModulesTests.
    public class ModuleAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildModuleEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(ModuleAggregate))!;
        }

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit column name
        // actually breaks: its identifiers are case-sensitive, so "ModuleId" != module_id.
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildModuleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("module", entityType.GetTableName());
            // The explicitly-named column — the one that would silently keep its PascalCase.
            Assert.Equal("module_id", columns[nameof(ModuleAggregate.Id)]);
            // Convention-named columns, proving the rewriter still runs around the explicit one.
            Assert.Equal("module_definition_name", columns[nameof(ModuleAggregate.ModuleDefinitionName)]);
            Assert.Equal("all_pages", columns[nameof(ModuleAggregate.AllPages)]);
            Assert.Equal("site_id", columns[nameof(ModuleAggregate.SiteId)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildModuleEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("Module", entityType.GetTableName());
            Assert.Equal("ModuleId", columns[nameof(ModuleAggregate.Id)]);
            Assert.Equal("ModuleDefinitionName", columns[nameof(ModuleAggregate.ModuleDefinitionName)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token and the harvested Module
        // table has no column for it. Like Page (and unlike Site) there is no name collision to make the
        // breakage loud: if it ever gets mapped EF silently expects a `version` column that does not exist
        // and every read of a module fails.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildModuleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The tripwire for the next agent. Module (the wire contract) and ModuleAggregate (the persisted
        // model) are two hand-maintained lists of the same columns. Add a column to Module, forget the
        // aggregate, and the install still boots — the value just never persists. This fails instead.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Module).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // ModuleId is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == nameof(Module.ModuleId) ? nameof(ModuleAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildModuleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Module
            {
                ModuleId = 11,
                SiteId = 3,
                ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                AllPages = true,
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = ModuleMapper.ToModel(ModuleMapper.ToAggregate(original, assignId: true));

            // Compare every persisted member reflectively, so a new column cannot be quietly left out.
            foreach (var property in typeof(Module).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        [Fact]
        public void An_insert_leaves_the_identity_to_the_database()
        {
            var aggregate = ModuleMapper.ToAggregate(new Module { ModuleId = 99, ModuleDefinitionName = "X" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    // The rule Pages paid for (D19) applied to Module, whose navigation had a far larger blast radius.
    //
    // PageModule.Module WAS an EF navigation, eager-loaded with .Include(item => item.Module) in EVERY
    // PageModuleRepository query. A navigation to Module makes EF discover Module as an entity type mapped
    // to the "Module" table — which ModuleAggregate now owns — and the model fails hard at startup:
    //
    //   "The table 'module' cannot be used for entity type 'ModuleAggregate' since it is being used for
    //    entity type 'Module' and potentially other entity types, but there is no linking relationship."
    //
    // These tests pin both halves: the navigation stays [NotMapped], and a context holding DbSet<PageModule>
    // alongside the framework store still maps Module exactly once.
    public class ModuleMustNotBeResurrectedTests
    {
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            // Folder is the unmigrated neighbour. The PageModule CONTRACT — the entity that referenced BOTH
            // migrated clusters — stood here until D47 put PageModule on the seam itself: it is mapped as
            // PageModuleAggregate by ConfigureFrameworkStore now, so declaring the contract would put two
            // entity types on the `page_module` table and break the model. The reference that matters to THIS
            // class — page_module -> module, by id and never by navigation — is still proven below, just
            // through the aggregate.
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

        // Mutation check: give PageModuleAggregate a `public Module Module { get; set; }` navigation and this
        // fails (the model throws while building) — which is exactly the protection being asserted. Until D47
        // the same check was "remove [NotMapped] from PageModule.Module", back when the PageModule CONTRACT
        // was the entity this context tracked; the hazard is identical, only the type carrying it changed.
        [Fact]
        public void The_module_table_is_owned_by_the_aggregate_alone()
        {
            var model = BuildModel();

            var mappedToTheModuleTable = model.GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "module")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(ModuleAggregate)], mappedToTheModuleTable);
        }

        [Fact]
        public void Module_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(Module)));
        }

        // PageModule moved to the seam in D47. This test used to read
        // "Page_module_is_still_a_plain_entity_referencing_both_aggregates_by_id" and assert over the
        // PageModule CONTRACT; the contract is not an entity type any more, so the assertion is made over the
        // AGGREGATE that replaced it. What is being pinned is unchanged and is D19's and D21's, not D47's:
        // whatever entity holds the page_module row must reach BOTH the Page and Module clusters BY ID,
        // because a navigation either way would rediscover the contract as a second entity type on a table an
        // aggregate already owns.
        [Fact]
        public void The_page_module_row_reaches_both_aggregates_by_id_and_never_by_navigation()
        {
            var model = BuildModel();

            // The contract is gone from the model — D47 replaced it with the aggregate.
            Assert.Null(model.FindEntityType(typeof(PageModule)));

            var pageModule = model.FindEntityType(typeof(PageModuleAggregate))!;
            Assert.Equal("page_module", pageModule.GetTableName());
            Assert.NotNull(pageModule.FindProperty(nameof(PageModuleAggregate.ModuleId)));
            Assert.NotNull(pageModule.FindProperty(nameof(PageModuleAggregate.PageId)));
            // The aggregate has no Page or Module member at all to navigate through — the strongest form of
            // the guarantee, and stronger than the [NotMapped] the contract relied on.
            Assert.Empty(pageModule.GetNavigations());
        }
    }

    // The behaviour of the migrated repository's store, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class ModuleStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Module table exactly as the tenant migrations leave it: ModuleEntityBuilder.BuildTable plus
        // AddAuditableColumns, and no later migration has touched it since 01000000_InitializeTenant.
        // PascalCase — the SQLite provider has no rewriter. Every column is NOT NULL because the entity
        // builder's Add*Column helpers default to nullable: false.
        private const string ModuleTableDdl = """
            CREATE TABLE "Module" (
                "ModuleId" INTEGER NOT NULL CONSTRAINT "PK_Module" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "ModuleDefinitionName" TEXT NOT NULL,
                "AllPages" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
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

        public ModuleStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = ModuleTableDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<ModuleAggregate, int> NewStore(DbContext context) =>
            new EfRepository<ModuleAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static Module NewModule(string moduleDefinitionName, int siteId = 1, bool allPages = false) => new()
        {
            SiteId = siteId,
            ModuleDefinitionName = moduleDefinitionName,
            AllPages = allPages,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_module_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = ModuleMapper.ToAggregate(NewModule("HtmlText"), assignId: false);

            await store.AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("HtmlText", loaded.ModuleDefinitionName);
            Assert.Equal(1, loaded.SiteId);
        }

        // THE query the whole Modules cluster now hangs on. ModuleRepository.GetModules(siteId) is what
        // PageModuleRepository.GetPageModules uses to resolve a site's module ids now that the
        // PageModule.Module navigation (and its `item.Module.SiteId == siteId` join filter) is gone. If this
        // filter were ever dropped or moved in memory, one site would see another site's modules on a shared
        // tenant database. Mutation check: drop the Where and this fails ("Site One" != "Site One","Site Two").
        [Fact]
        public async Task The_modules_of_a_site_are_read_through_a_query_session_filtered_by_site()
        {
            using (var seed = NewContext())
            {
                var store = NewStore(seed);
                await store.AddAsync(ModuleMapper.ToAggregate(NewModule("Site One", siteId: 1), assignId: false));
                await store.AddAsync(ModuleMapper.ToAggregate(NewModule("Site Two", siteId: 2), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var names = session.Query<ModuleAggregate>()
                .Where(item => item.SiteId == 1)
                .ToList()
                .ConvertAll(item => item.ModuleDefinitionName);

            Assert.Equal(["Site One"], names);
        }

        [Fact]
        public async Task An_updated_module_persists_through_the_store()
        {
            int moduleId;
            using (var seed = NewContext())
            {
                var aggregate = ModuleMapper.ToAggregate(NewModule("Before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                moduleId = aggregate.Id;
            }

            // ModuleRepository updates a DETACHED aggregate rebuilt from the wire contract — the same shape
            // the harvested repository used (db.Entry(module).State = Modified), so every column is written.
            using (var context = NewContext())
            {
                var module = NewModule("After", allPages: true);
                module.ModuleId = moduleId;
                await NewStore(context).UpdateAsync(ModuleMapper.ToAggregate(module, assignId: true));
            }

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(moduleId))!;
            Assert.Equal("After", loaded.ModuleDefinitionName);
            Assert.True(loaded.AllPages);
        }

        [Fact]
        public async Task A_deleted_module_is_removed_through_the_store()
        {
            int moduleId;
            using (var seed = NewContext())
            {
                var aggregate = ModuleMapper.ToAggregate(NewModule("Doomed"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                moduleId = aggregate.Id;
            }

            using (var context = NewContext())
            {
                var store = NewStore(context);
                var aggregate = (await store.GetByIdAsync(moduleId))!;
                await store.DeleteAsync(aggregate);
            }

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(moduleId));
        }

        // WHY ModuleRepository.GetModule reads through the query session instead of IModuleStore.GetByIdAsync
        // (D19, inherited from Pages, and Module walks into it even more directly than Page did).
        //
        // The seam's IRepository.GetByIdAsync TRACKS the aggregate. ModuleRepository then updates a DETACHED
        // aggregate rebuilt from the wire contract, and EF refuses to attach a second instance with the same
        // key. ModuleController.Put does exactly that: GetModule(module.ModuleId, false) to authorize and to
        // compare AllPages, then UpdateModule(module) — both on one repository instance == one store == one
        // DbContext. Pages found this by driving a real PUT: the write silently did nothing and returned 200.
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            int moduleId;
            using (var seed = NewContext())
            {
                var aggregate = ModuleMapper.ToAggregate(NewModule("Before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                moduleId = aggregate.Id;
            }

            using var context = NewContext();
            var store = NewStore(context);

            await store.GetByIdAsync(moduleId); // tracks the aggregate

            var module = NewModule("After");
            module.ModuleId = moduleId;

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.UpdateAsync(ModuleMapper.ToAggregate(module, assignId: true)));
            Assert.Contains("cannot be tracked", conflict.Message);
        }

        // ...and the shape ModuleRepository actually uses: read through a query session (its own context, so
        // the store's change tracker stays clean), write through the store. Mutation check: point the read at
        // NewStore(context).GetByIdAsync and this fails with the identity conflict above.
        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            int moduleId;
            using (var seed = NewContext())
            {
                var aggregate = ModuleMapper.ToAggregate(NewModule("Before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                moduleId = aggregate.Id;
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            using var context = NewContext();
            var store = NewStore(context);

            // ModuleController.Put: var _module = _modules.GetModule(module.ModuleId, false)
            await using (var session = sessions.OpenQuerySession())
            {
                Assert.NotNull(session.Query<ModuleAggregate>().FirstOrDefault(item => item.Id == moduleId));
            }

            // ...then _modules.UpdateModule(module)
            var module = NewModule("After");
            module.ModuleId = moduleId;
            await store.UpdateAsync(ModuleMapper.ToAggregate(module, assignId: true));

            using var readContext = NewContext();
            Assert.Equal("After", (await NewStore(readContext).GetByIdAsync(moduleId))!.ModuleDefinitionName);
        }

        public void Dispose() => _connection.Dispose();
    }

    // GetPageModules(siteId) used to filter `item.Module.SiteId == siteId` ACROSS THE JOIN, in the database.
    // Severing the navigation made the obvious translation an in-memory filter over every PageModule row in
    // the tenant — which on a shared tenant database (many sites, one database) reads every other site's
    // page modules on every request. The replacement resolves the site's module ids first and filters
    // `ModuleId IN (...)`, which must still be evaluated by the database.
    //
    // These tests pin that the replacement shape translates to SQL. HONEST LIMITATION: they assert the query
    // SHAPE, not the repository's use of it — a future edit that materializes first and filters in memory
    // would not fail here. The discriminating evidence for the repository itself is the live EF command log
    // captured against the PostgreSQL install; see src/Stomme.Server/README.md (persistence seam).
    //
    // D47 UPDATE: these used to run over a DbSet<PageModule> — the contract — because PageModule was still an
    // unmigrated neighbour. It is on the seam now, so they run over PageModuleAggregate, which is the type
    // PageModuleRepository actually queries. The test got STRICTER by the move, not weaker: it now pins the
    // real production shape rather than a stand-in with the same property names.
    public class TheSiteFilterOnPageModulesTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // No DbSet: ConfigureFrameworkStore maps PageModuleAggregate, and Set<T>() reaches it. Declaring a
        // DbSet<PageModule> alongside it would put the contract on the same table and break the model.
        private sealed class PageModuleContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        public TheSiteFilterOnPageModulesTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        private PageModuleContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new PageModuleContext(builder.Options, _database);
        }

        [Fact]
        public void The_module_id_filter_is_translated_to_sql()
        {
            using var context = NewContext();
            var moduleIds = new List<int> { 3, 7 };

            var sql = context.Set<PageModuleAggregate>().Where(item => moduleIds.Contains(item.ModuleId)).ToQueryString();

            // Translated, not client-evaluated: EF emitted a WHERE on the ModuleId column. (EF Core throws
            // rather than silently client-evaluating an untranslatable Where, so reaching this assertion at
            // all is most of the proof.)
            Assert.Contains("WHERE", sql);
            Assert.Contains("ModuleId", sql);
        }

        [Fact]
        public void An_empty_module_list_still_filters_rather_than_returning_the_whole_table()
        {
            using var context = NewContext();
            var moduleIds = new List<int>();

            // A site with no modules must yield no page modules. The dangerous regression is the opposite:
            // a filter that degrades to "no predicate" would return every site's page modules.
            var sql = context.Set<PageModuleAggregate>().Where(item => moduleIds.Contains(item.ModuleId)).ToQueryString();

            Assert.Contains("WHERE", sql);
        }

        public void Dispose() => _connection.Dispose();
    }
}
