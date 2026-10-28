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
    // The Settings cluster on the persistence seam (D3/D7/D23) — the fifth and last of Phase 3.
    //
    // The hazards Sites/Pages/Modules/Users paid for apply here too (the aggregate is mapped onto a schema
    // this context does not migrate; HasColumnName outranks the snake_case rewriter). What is NEW here, and
    // what these tests exist for, is that Setting is the ONLY framework-store aggregate mapped in BOTH
    // MasterDBContext and TenantDBContext — see TheMasterAndTenantSettingStoresTests.
    public class SettingAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildSettingEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(SettingAggregate))!;
        }

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit column name
        // actually breaks: its identifiers are case-sensitive, so "SettingId" != setting_id.
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildSettingEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("setting", entityType.GetTableName());
            // The explicitly-named column — the one that would silently keep its PascalCase.
            Assert.Equal("setting_id", columns[nameof(SettingAggregate.Id)]);
            // Convention-named columns, proving the rewriter still runs around the explicit one.
            Assert.Equal("entity_name", columns[nameof(SettingAggregate.EntityName)]);
            Assert.Equal("entity_id", columns[nameof(SettingAggregate.EntityId)]);
            Assert.Equal("setting_name", columns[nameof(SettingAggregate.SettingName)]);
            Assert.Equal("setting_value", columns[nameof(SettingAggregate.SettingValue)]);
            Assert.Equal("is_private", columns[nameof(SettingAggregate.IsPrivate)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildSettingEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("Setting", entityType.GetTableName());
            Assert.Equal("SettingId", columns[nameof(SettingAggregate.Id)]);
            Assert.Equal("EntityName", columns[nameof(SettingAggregate.EntityName)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token and the harvested Setting
        // table has no column for it. Like Page/Module (and unlike Site) there is no name collision to make
        // the breakage loud: if it ever gets mapped EF silently expects a `version` column that does not
        // exist and every read of a setting fails — i.e. every request.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildSettingEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The tripwire for whoever comes next. Setting (the wire contract) and SettingAggregate (the
        // persisted model) are two hand-maintained lists of the same columns. Add a column to Setting, forget
        // the aggregate, and the install still boots — the value just never persists.
        //
        // Setting is the strictest case in the framework store: unlike Site/Page/Module/User it has NO
        // [NotMapped] view state at all, so the contract and the aggregate must match completely.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Setting).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // SettingId is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == nameof(Setting.SettingId) ? nameof(SettingAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildSettingEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Setting
            {
                SettingId = 11,
                EntityName = "Site",
                EntityId = 3,
                SettingName = "SMTPHost",
                SettingValue = "smtp.example.com",
                IsPrivate = true,
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = SettingMapper.ToModel(SettingMapper.ToAggregate(original, assignId: true));

            // Compare every persisted member reflectively, so a new column cannot be quietly left out.
            foreach (var property in typeof(Setting).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        [Fact]
        public void An_insert_leaves_the_identity_to_the_database()
        {
            var aggregate = SettingMapper.ToAggregate(new Setting { SettingId = 99, EntityName = "Site", SettingName = "X" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    // The distinguishing feature of this cluster (D23): Setting is mapped in BOTH the tenant and the master
    // context, because SettingRepository.IsMaster routes Host/Job/ModuleDefinition/Theme/master:* settings to
    // the installation-wide database and everything else to the tenant's.
    //
    // Two hazards live here. (1) The two mappings could drift, so a Host setting and a Site setting would
    // disagree about their own table. (2) ConfigureMasterFrameworkStore could be "helpfully" changed to call
    // ConfigureFrameworkStore, which would map Site/Page/Module/User/Role/UserRole onto the master database —
    // tables its migration set does not create. Both are pinned below.
    public class TheMasterAndTenantSettingStoresTests
    {
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            // THIS CONTEXT IS NOW EMPTY of raw DbSets, and that is the END OF THE PATTERN for the master side. Job
            // and JobLog left in D52's first wave; Theme and ModuleDefinition followed; Tenant and Alias — the last
            // two — leave here. ConfigureMasterFrameworkStore now maps every master-side aggregate (Setting,
            // Job/JobLog, Theme, ModuleDefinition, Tenant, Alias) to its table, so declaring the raw CONTRACT DbSet
            // for any of them (e.g. DbSet<Tenant>) alongside it would put two entity types on the same table — the
            // D45 mechanism, and exactly what this probe would hit. The proof now comes from
            // ConfigureMasterFrameworkStore plus the assertions below, not from a neighbour DbSet.
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        private static IModel BuildTenantModel()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new TenantLikeContext(builder.Options, database);
            return context.Model;
        }

        private static IModel BuildMasterModel()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new MasterLikeContext(builder.Options, database);
            return context.Model;
        }

        // The master half of the cluster exists at all. Without this, SettingRepository's IsMaster branch has
        // no store to reach and every Host setting read throws.
        [Fact]
        public void The_master_context_maps_the_setting_aggregate()
        {
            var setting = BuildMasterModel().FindEntityType(typeof(SettingAggregate))!;

            Assert.NotNull(setting);
            Assert.Equal("setting", setting.GetTableName());
        }

        // Mutation check: change either mapping alone and this fails.
        [Fact]
        public void The_master_and_tenant_contexts_map_the_setting_aggregate_identically()
        {
            var tenant = BuildTenantModel().FindEntityType(typeof(SettingAggregate))!;
            var master = BuildMasterModel().FindEntityType(typeof(SettingAggregate))!;

            Assert.Equal(tenant.GetTableName(), master.GetTableName());
            Assert.Equal(
                tenant.GetProperties().Select(p => $"{p.Name}:{p.GetColumnName()}").OrderBy(x => x),
                master.GetProperties().Select(p => $"{p.Name}:{p.GetColumnName()}").OrderBy(x => x));
        }

        // The master database's migration set creates Tenant, Alias, Job, JobLog, ModuleDefinition and Theme
        // — NOT Site, Page, Module, User, Role or UserRole. ConfigureMasterFrameworkStore maps the master-side
        // framework-store aggregates (Setting, D23; Job/JobLog/Theme/ModuleDefinition/Tenant/Alias, D52) but must
        // NOT map the per-TENANT ones: mapping those here would invent entity types over tables that do not exist.
        // Mutation check: make ConfigureMasterFrameworkStore delegate to ConfigureFrameworkStore and this fails.
        [Fact]
        public void The_master_context_maps_only_the_master_side_aggregates_and_none_of_the_per_tenant_aggregates()
        {
            var model = BuildMasterModel();

            Assert.Null(model.FindEntityType(typeof(SiteAggregate)));
            Assert.Null(model.FindEntityType(typeof(PageAggregate)));
            Assert.Null(model.FindEntityType(typeof(ModuleAggregate)));
            Assert.Null(model.FindEntityType(typeof(UserAggregate)));
            Assert.Null(model.FindEntityType(typeof(RoleAggregate)));
            Assert.Null(model.FindEntityType(typeof(UserRoleAggregate)));
        }

        // The master-bookkeeping wave (D52) is on the seam's MASTER half, mapped by ConfigureMasterFrameworkStore
        // onto the master database's own tables. Mutation check: drop any aggregate from
        // ConfigureMasterFrameworkStore and this fails. Job/Theme/ModuleDefinition/Tenant/Alias ARE IAuditable,
        // JobLog is NOT — see the aggregates.
        [Fact]
        public void The_master_context_maps_the_master_bookkeeping_aggregates()
        {
            var model = BuildMasterModel();

            Assert.Equal("job", model.FindEntityType(typeof(JobAggregate))!.GetTableName());
            Assert.Equal("job_log", model.FindEntityType(typeof(JobLogAggregate))!.GetTableName());
            Assert.Equal("theme", model.FindEntityType(typeof(ThemeAggregate))!.GetTableName());
            Assert.Equal("module_definition", model.FindEntityType(typeof(ModuleDefinitionAggregate))!.GetTableName());
            Assert.Equal("tenant", model.FindEntityType(typeof(TenantAggregate))!.GetTableName());
            Assert.Equal("alias", model.FindEntityType(typeof(AliasAggregate))!.GetTableName());
        }

        // The Tenant/Alias routing pair (D52) — the FINAL master cluster — maps the `version` column past the
        // AggregateRoot.Version shadow (Tenant only; Alias has no version column) and carries the builder-omitted
        // db_type/is_default columns. Mutation check on the shadow: leave TenantVersion to convention (tenant_version)
        // or fail to ignore the uint token and this fails.
        [Fact]
        public void The_master_context_maps_the_tenant_version_string_and_ignores_the_uint_token()
        {
            var tenant = BuildMasterModel().FindEntityType(typeof(TenantAggregate))!;

            Assert.Equal("version", tenant.FindProperty(nameof(TenantAggregate.TenantVersion))!.GetColumnName());
            Assert.Null(tenant.FindProperty(nameof(AggregateRoot<int>.Version))); // the uint concurrency token has no column
            Assert.Equal("db_type", tenant.FindProperty(nameof(TenantAggregate.DBType))!.GetColumnName());
        }

        // The master side now has NO raw content neighbours: Tenant and Alias — the last two — left for the seam,
        // so their CONTRACT CLR types are no longer entity types on the master model. They are mapped as AGGREGATES
        // (asserted above). Mutation check: re-declare DbSet<Tenant> on MasterLikeContext and the whole master model
        // fails to build (two entity types on the `tenant` table — the D45 mechanism), which is what END OF THE
        // PATTERN means here.
        [Fact]
        public void The_master_side_has_no_raw_content_contract_types_left()
        {
            var model = BuildMasterModel();

            Assert.Null(model.FindEntityType(typeof(Tenant)));
            Assert.Null(model.FindEntityType(typeof(Alias)));
        }
    }

    // The rule Pages paid for (D19) and Users generalised (D22 rule 6): sweep for EF navigations INTO the
    // cluster's CLR type before trusting that removing the DbSet is enough.
    //
    // The sweep found NONE for Setting — every `Settings` member in the framework is either a [NotMapped]
    // Dictionary<string,string> (Site/Page/Module/User) or lives on a type that is not an entity at all
    // (SiteTemplate, ModuleContent, ExternalLoginProvider). That is the honest finding, and unlike
    // Visitor.User there was nothing to sever. These tests pin the finding so it stays true: the danger is a
    // future edit adding `public List<Setting> Settings` to an entity, which would resurrect Setting onto the
    // table SettingAggregate now owns and break the model at startup.
    public class SettingMustNotBeResurrectedTests
    {
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            // The unmigrated neighbours that settings hang off, as TenantDBContext still holds them.
            // NOTE: Permission was here until D24 moved it onto the seam. It is now mapped as
            // PermissionAggregate by ConfigureFrameworkStore, so declaring the Permission CONTRACT here would
            // put two entity types on the `permission` table and break the model — which is precisely what
            // these tests exist to catch, and what they did catch when the cluster moved.
            // PageModule left the same way in D47, and caught the same failure on the way out.
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

        [Fact]
        public void The_setting_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToTheSettingTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "setting")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(SettingAggregate)], mappedToTheSettingTable);
        }

        [Fact]
        public void Setting_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(Setting)));
        }

        // The sweep, made executable. Nothing in the tenant model may navigate to the Setting contract — if
        // someone adds such a navigation, EF resurrects Setting and this fails here rather than at startup.
        [Fact]
        public void No_entity_in_the_model_navigates_into_the_setting_contract()
        {
            var navigationsIntoSetting = BuildModel().GetEntityTypes()
                .SelectMany(entityType => entityType.GetNavigations())
                .Where(navigation => navigation.TargetEntityType.ClrType == typeof(Setting)
                    || navigation.TargetEntityType.ClrType == typeof(SettingAggregate))
                .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name}")
                .ToList();

            Assert.Empty(navigationsIntoSetting);
        }
    }

    // The behaviour of the migrated repository's store, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class SettingStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Setting table exactly as the tenant migrations leave it: SettingEntityBuilder.BuildTable plus
        // AddAuditableColumns (01000000_InitializeTenant), then 02030002_AddSettingIsPublic and
        // 03000202_UpdateSettingIsPrivate, which together drop IsPublic and leave IsPrivate.
        //
        // NOTE IsPrivate is NULLABLE — AddBooleanColumn("IsPrivate", true) passes nullable: true, and the
        // migration backfills it with UpdateData. The CLR property is a non-nullable bool. That mismatch is
        // harvested and deliberate; this DDL reproduces it rather than tidying it, because tidying it here
        // would hide a real read failure against a real database.
        private const string SettingTableDdl = """
            CREATE TABLE "Setting" (
                "SettingId" INTEGER NOT NULL CONSTRAINT "PK_Setting" PRIMARY KEY AUTOINCREMENT,
                "EntityName" TEXT NOT NULL,
                "EntityId" INTEGER NOT NULL,
                "SettingName" TEXT NOT NULL,
                "SettingValue" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "IsPrivate" INTEGER NULL
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

        public SettingStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = SettingTableDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<SettingAggregate, int> NewStore(DbContext context) =>
            new EfRepository<SettingAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static Setting NewSetting(string entityName, int entityId, string name, string value, bool isPrivate = false) => new()
        {
            EntityName = entityName,
            EntityId = entityId,
            SettingName = name,
            SettingValue = value,
            IsPrivate = isPrivate,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_setting_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "DefaultTheme", "StommeTheme"), assignId: false);

            await store.AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("Site", loaded.EntityName);
            Assert.Equal(1, loaded.EntityId);
            Assert.Equal("DefaultTheme", loaded.SettingName);
            Assert.Equal("StommeTheme", loaded.SettingValue);
        }

        // IsPrivate is what SettingController filters responses on — a private setting that reads back as
        // public is a server-side secret (SMTP passwords) sent to the browser. Worth its own assertion
        // against a real database because of the nullable-column/non-nullable-property mismatch above.
        [Fact]
        public async Task The_private_flag_persists_against_the_nullable_column_the_migration_created()
        {
            using var context = NewContext();
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "SMTPPassword", "hunter2", isPrivate: true), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.True(loaded.IsPrivate);
        }

        // The polymorphic key is (EntityName, EntityId) and NOTHING enforces it — there is no foreign key and
        // no unique index. Two entities of different kinds can share an EntityId, so a filter on EntityId
        // alone silently returns another entity's settings. Pinned because that is the shape of a settings
        // regression: not an exception, a wrong value.
        [Fact]
        public async Task Settings_are_isolated_by_entity_name_as_well_as_entity_id()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(SettingMapper.ToAggregate(NewSetting("Site", 1, "Title", "the site"), assignId: false));
            await store.AddAsync(SettingMapper.ToAggregate(NewSetting("Page", 1, "Title", "the page"), assignId: false));

            using var readContext = NewContext();
            var siteSettings = readContext.Set<SettingAggregate>()
                .Where(item => item.EntityName == "Site" && item.EntityId == 1)
                .ToList();

            Assert.Equal("the site", Assert.Single(siteSettings).SettingValue);
        }

        [Fact]
        public async Task An_updated_setting_persists_its_new_value()
        {
            using var context = NewContext();
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "DefaultTheme", "OldTheme"), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            var model = SettingMapper.ToModel(aggregate);
            model.SettingValue = "NewTheme";

            using var writeContext = NewContext();
            await NewStore(writeContext).UpdateAsync(SettingMapper.ToAggregate(model, assignId: true));

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("NewTheme", loaded.SettingValue);
        }

        [Fact]
        public async Task A_deleted_setting_is_gone()
        {
            using var context = NewContext();
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "Doomed", "x"), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var deleteContext = NewContext();
            var deleteStore = NewStore(deleteContext);
            await deleteStore.DeleteAsync((await deleteStore.GetByIdAsync(aggregate.Id))!);

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(aggregate.Id));
        }

        // DeleteSettings deletes aggregates that were read through a SESSION (a different context), so they
        // arrive at the store DETACHED. Remove() must still delete them. This pins the one non-obvious move
        // in SettingRepository.DeleteSettings.
        [Fact]
        public async Task The_store_deletes_a_detached_aggregate_read_through_another_context()
        {
            using var context = NewContext();
            var aggregate = SettingMapper.ToAggregate(NewSetting("Module", 7, "Doomed", "x"), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            // Read through one context (as the query session does)...
            using var readContext = NewContext();
            var detached = readContext.Set<SettingAggregate>()
                .Where(item => item.EntityName == "Module" && item.EntityId == 7)
                .ToList();

            // ...and delete through a different, clean one (as the store does).
            using var deleteContext = NewContext();
            var deleteStore = NewStore(deleteContext);
            foreach (var item in detached)
            {
                await deleteStore.DeleteAsync(item);
            }

            using var verifyContext = NewContext();
            Assert.Empty(verifyContext.Set<SettingAggregate>().Where(item => item.EntityName == "Module" && item.EntityId == 7));
        }

        // The D19 pair. This is the bug that made Pages' PUT return 200 and persist NOTHING, and it is live
        // in this cluster's shape: SettingController.Put calls GetSetting to authorize, then UpdateSetting —
        // one repository instance, one store, one context.
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "DefaultTheme", "OldTheme"), assignId: false);
            await store.AddAsync(aggregate);

            // A tracked read through the STORE — what GetSetting must never do.
            var tracked = await store.GetByIdAsync(aggregate.Id);
            Assert.NotNull(tracked);

            var rebuilt = SettingMapper.ToAggregate(
                new Setting
                {
                    SettingId = aggregate.Id,
                    EntityName = "Site",
                    EntityId = 1,
                    SettingName = "DefaultTheme",
                    SettingValue = "NewTheme",
                    CreatedBy = "installer",
                    CreatedOn = DateTime.UtcNow,
                    ModifiedBy = "installer",
                    ModifiedOn = DateTime.UtcNow
                },
                assignId: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(rebuilt));
        }

        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            using var seedContext = NewContext();
            var aggregate = SettingMapper.ToAggregate(NewSetting("Site", 1, "DefaultTheme", "OldTheme"), assignId: false);
            await NewStore(seedContext).AddAsync(aggregate);

            // The read goes through a session-like context, leaving the store's tracker clean...
            using var sessionContext = NewContext();
            var read = SettingMapper.ToModel(sessionContext.Set<SettingAggregate>().FirstOrDefault(item => item.Id == aggregate.Id));
            read.SettingValue = "NewTheme";

            // ...so the store accepts the rebuilt instance.
            using var storeContext = NewContext();
            await NewStore(storeContext).UpdateAsync(SettingMapper.ToAggregate(read, assignId: true));

            using var verifyContext = NewContext();
            Assert.Equal("NewTheme", (await NewStore(verifyContext).GetByIdAsync(aggregate.Id))!.SettingValue);
        }

        public void Dispose() => _connection.Dispose();
    }
}
