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
using Stomme.Repository;
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
    // The Permission cluster on the persistence seam (D3/D7/D24) — the sixth framework-store cluster, and the
    // one that gates everything else: permissions decide what renders and what an admin may do.
    //
    // The hazards the five earlier clusters paid for apply here too (the aggregate is mapped onto a schema
    // this context does not migrate; HasColumnName outranks the snake_case rewriter). What is different about
    // this cluster is the FAILURE MODE, and it is why these tests lean on behaviour rather than status:
    // a broken permission does not throw. It 404s a page or silently refuses an admin action — or, far worse,
    // silently ALLOWS one. Both directions are asserted.
    public class PermissionAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildPermissionEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(PermissionAggregate))!;
        }

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit column name
        // actually breaks: its identifiers are case-sensitive, so "PermissionId" != permission_id.
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildPermissionEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("permission", entityType.GetTableName());
            // The explicitly-named column — the one that would silently keep its PascalCase.
            Assert.Equal("permission_id", columns[nameof(PermissionAggregate.Id)]);
            // Convention-named columns, proving the rewriter still runs around the explicit one.
            Assert.Equal("site_id", columns[nameof(PermissionAggregate.SiteId)]);
            Assert.Equal("entity_name", columns[nameof(PermissionAggregate.EntityName)]);
            Assert.Equal("entity_id", columns[nameof(PermissionAggregate.EntityId)]);
            Assert.Equal("permission_name", columns[nameof(PermissionAggregate.PermissionName)]);
            Assert.Equal("role_id", columns[nameof(PermissionAggregate.RoleId)]);
            Assert.Equal("user_id", columns[nameof(PermissionAggregate.UserId)]);
            Assert.Equal("is_authorized", columns[nameof(PermissionAggregate.IsAuthorized)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildPermissionEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("Permission", entityType.GetTableName());
            Assert.Equal("PermissionId", columns[nameof(PermissionAggregate.Id)]);
            Assert.Equal("EntityName", columns[nameof(PermissionAggregate.EntityName)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token and the harvested Permission
        // table has no column for it. If it ever gets mapped, EF silently expects a `version` column that does
        // not exist and every permission read fails — i.e. every authorization decision, i.e. every request.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildPermissionEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // RoleId and UserId are NULLABLE by construction: a permission is granted to EITHER a role OR a user,
        // never both, and the "other" column is null. A non-nullable mapping would make every role permission
        // unreadable — and the harvested migration says AddIntegerColumn(..., true) for both.
        [Fact]
        public void The_role_and_user_references_stay_nullable_because_a_permission_targets_one_or_the_other()
        {
            var entityType = BuildPermissionEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.True(entityType.FindProperty(nameof(PermissionAggregate.RoleId))!.IsNullable);
            Assert.True(entityType.FindProperty(nameof(PermissionAggregate.UserId))!.IsNullable);
        }

        // The tripwire for whoever comes next. Permission (the wire contract) and PermissionAggregate (the
        // persisted model) are two hand-maintained lists of the same columns. Add a column to Permission,
        // forget the aggregate, and the install still boots — the value just never persists.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Permission).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // PermissionId is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == nameof(Permission.PermissionId) ? nameof(PermissionAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildPermissionEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Permission
            {
                PermissionId = 11,
                SiteId = 3,
                EntityName = "Page",
                EntityId = 7,
                PermissionName = "View",
                RoleId = 2,
                UserId = null,
                IsAuthorized = true,
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var round = PermissionMapper.ToModel(PermissionMapper.ToAggregate(original, assignId: true));

            foreach (var property in typeof(Permission).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(round));
            }
        }

        // RoleName is [NotMapped] view state with no column: the mapper deliberately DROPS it and
        // PermissionRepository.GetPermissions re-derives it in memory from IRoleRepository. Pinned because a
        // future "fix" that round-trips RoleName through the aggregate would be trying to persist a column
        // that does not exist.
        [Fact]
        public void The_mapper_does_not_round_trip_rolename()
        {
            var original = new Permission(1, "Page", 7, "View", "Administrators", null, true) { RoleId = 2 };

            var round = PermissionMapper.ToModel(PermissionMapper.ToAggregate(original, assignId: false));

            Assert.Equal("Administrators", original.RoleName);
            Assert.Null(round.RoleName);
            // ...but the id it is derived FROM does survive, which is what makes the re-derivation possible.
            Assert.Equal(2, round.RoleId);
        }

        // An insert must leave the identity to the database — the harvested table is an autoincrement column
        // and callers (PermissionController.Post) read PermissionId back off the response.
        [Fact]
        public void An_insert_does_not_carry_a_client_assigned_identity()
        {
            var aggregate = PermissionMapper.ToAggregate(new Permission { PermissionId = 99, EntityName = "Page" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    // Rule (g)/D19/D22 rule 6: deleting the DbSet is NOT enough — EF rediscovers CLR types through
    // navigations. THIS IS NOT HYPOTHETICAL FOR THIS CLUSTER: deleting DbSet<Permission> from TenantDBContext
    // immediately broke SEVEN tests, because two test probe contexts still declared the Permission CONTRACT as
    // an "unmigrated neighbour" and EF then put two entity types on the `permission` table:
    //
    //   "The table 'permission' cannot be used for entity type 'PermissionAggregate' since it is being used
    //    for entity type 'Permission' and potentially other entity types."
    //
    // The sweep of PRODUCTION code found nothing to sever — every List<Permission> PermissionList in the
    // framework (Folder, Module, ModuleDefinition, Page, Theme, SiteTemplate/PageTemplate) is already
    // [NotMapped] or lives on a type that is not an entity, and Permission.Role is [NotMapped] + [Obsolete].
    // That is the finding, and these tests pin it so it stays true.
    public class PermissionMustNotBeResurrectedTests
    {
        // The unmigrated neighbours that permissions hang off, as TenantDBContext still holds them.
        //
        // THIS CONTEXT IS NOW EMPTY, and that is a finding rather than a gap. PageModule and Visitor stood here
        // until D47; FOLDER stood here — as the last tenant-side entity carrying a [NotMapped] PermissionList —
        // until D48 put it on the seam. Declaring any of them now would put two entity types on its table and
        // break the model. Nothing that TenantDBContext still maps (File, the SearchContent family,
        // MigrationHistory) carries a PermissionList at all, so there is no neighbour left to probe.
        //
        // The class is kept, not deleted: it still proves that ConfigureFrameworkStore maps exactly ONE type
        // onto `permission` and that no AGGREGATE navigates into the Permission contract — and the day a
        // neighbour with permissions is added back, this is where it goes. The reflective sweep below
        // (No_entity_the_tenant_context_maps_carries_a_mapped_permission_member) is what actually covers the
        // entities this context no longer names; it walks TenantDBContext's own DbSets, so it kept working
        // across D47 and D48 without an edit.
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
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

        // Mutation check: map a PermissionList onto any entity this context declares and this fails (the model
        // throws while building). Until D48 the mutation was concrete — remove [NotMapped] from
        // Folder.PermissionList — but Folder is on the seam now and this context declares nothing. The
        // reflective sweep below is what carries that protection for the entities TenantDBContext still maps.
        [Fact]
        public void The_permission_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToThePermissionTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "permission")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(PermissionAggregate)], mappedToThePermissionTable);
        }

        [Fact]
        public void Permission_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(Permission)));
        }

        // The sweep, made executable. Nothing in the tenant model may navigate to the Permission contract — if
        // someone adds such a navigation, EF resurrects Permission and this fails here rather than at startup.
        [Fact]
        public void No_entity_in_the_model_navigates_into_the_permission_contract()
        {
            var navigationsIntoPermission = BuildModel().GetEntityTypes()
                .SelectMany(entityType => entityType.GetNavigations())
                .Where(navigation => navigation.TargetEntityType.ClrType == typeof(Permission)
                    || navigation.TargetEntityType.ClrType == typeof(PermissionAggregate))
                .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name}")
                .ToList();

            Assert.Empty(navigationsIntoPermission);
        }

        // The reflective half of the sweep, and the one that catches what the probe context above cannot: a
        // PermissionList added to an entity TenantDBContext maps but this test class never names. It walks
        // TenantDBContext's OWN DbSet declarations, so a new entity is covered the day it is added.
        //
        // Scoped to real entity types on purpose. PageTemplate/PageTemplateModule/SiteTemplate DO carry a
        // plain (unattributed) List<Permission> PermissionList and that is CORRECT: they are site-template
        // DTOs, not entities — no DbSet, never in any model — so [NotMapped] would be noise on them. An
        // earlier, broader version of this test flagged them, which is the false positive worth not shipping.
        [Fact]
        public void No_entity_the_tenant_context_maps_carries_a_mapped_permission_member()
        {
            var entityTypes = typeof(TenantDBContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .ToList();

            // Guard the guard: if the DbSet discovery ever silently finds nothing, the assertion below would
            // pass vacuously and this whole sweep would be worthless.
            Assert.NotEmpty(entityTypes);

            var mappedPermissionMembers = entityTypes
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(property => typeof(IEnumerable<Permission>).IsAssignableFrom(property.PropertyType)
                    || property.PropertyType == typeof(Permission))
                .Where(property => property.GetCustomAttribute<NotMappedAttribute>() is null)
                .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
                .ToList();

            Assert.Empty(mappedPermissionMembers);
        }

        // Permission is TENANT-ONLY — the finding that distinguishes it from Setting (D23), which is mapped in
        // both databases. MasterDBContext never had a DbSet<Permission> and PermissionEntityBuilder appears
        // only in the TENANT migration set, so mapping it on the master context would invent an entity type
        // over a table the master migration set does not create.
        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        [Fact]
        public void The_master_context_does_not_map_the_permission_aggregate()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new MasterLikeContext(builder.Options, database);

            Assert.Null(context.Model.FindEntityType(typeof(PermissionAggregate)));
            // ...while the tenant context does. One database, not two.
            Assert.NotNull(BuildModel().FindEntityType(typeof(PermissionAggregate)));
        }
    }

    // The behaviour of the migrated repository's store, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class PermissionStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Permission table exactly as the tenant migrations leave it: PermissionEntityBuilder.BuildTable
        // plus AddAuditableColumns (01000000_InitializeTenant). RoleId and UserId are nullable; the foreign
        // keys are omitted because this fixture exercises the Permission table alone.
        private const string PermissionTableDdl = """
            CREATE TABLE "Permission" (
                "PermissionId" INTEGER NOT NULL CONSTRAINT "PK_Permission" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "EntityName" TEXT NOT NULL,
                "EntityId" INTEGER NOT NULL,
                "PermissionName" TEXT NOT NULL,
                "RoleId" INTEGER NULL,
                "UserId" INTEGER NULL,
                "IsAuthorized" INTEGER NOT NULL,
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

        public PermissionStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = PermissionTableDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<PermissionAggregate, int> NewStore(DbContext context) =>
            new EfRepository<PermissionAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static Permission NewPermission(int siteId, string entityName, int entityId, string permissionName, int? roleId = null, int? userId = null, bool isAuthorized = true) => new()
        {
            SiteId = siteId,
            EntityName = entityName,
            EntityId = entityId,
            PermissionName = permissionName,
            RoleId = roleId,
            UserId = userId,
            IsAuthorized = isAuthorized,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_permission_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "View", roleId: 2), assignId: false);

            await store.AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal(1, loaded.SiteId);
            Assert.Equal("Page", loaded.EntityName);
            Assert.Equal(7, loaded.EntityId);
            Assert.Equal("View", loaded.PermissionName);
            Assert.Equal(2, loaded.RoleId);
            Assert.Null(loaded.UserId);
            Assert.True(loaded.IsAuthorized);
        }

        // A user permission is the other half of the nullable pair. Worth its own assertion against a real
        // database: if RoleId were mapped non-nullable, this insert would fail at runtime only.
        [Fact]
        public async Task A_user_permission_persists_with_a_null_role()
        {
            using var context = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Folder", 3, "Edit", userId: 5), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.Null(loaded.RoleId);
            Assert.Equal(5, loaded.UserId);
        }

        // IsAuthorized=false is a DENY, and a deny outranks a grant in UserSecurity.IsAuthorized. If this flag
        // failed to persist, a deny would silently read back as absent and the permission would be GRANTED —
        // the single worst silent failure available in this cluster.
        [Fact]
        public async Task A_deny_persists_as_a_deny_and_not_as_an_absent_permission()
        {
            using var context = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "Edit", roleId: 3, isAuthorized: false), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.False(loaded.IsAuthorized);
        }

        // Permissions are scoped by SiteId, and on a shared tenant database several sites live in one table.
        // PermissionRepository.GetPermissions filters on (SiteId, EntityName) in the DATABASE; if that filter
        // ever moved in-memory or lost the SiteId half, one site would read another site's permissions.
        // Pinned because that is a silent authorization leak, not an exception.
        [Fact]
        public async Task Permissions_are_isolated_by_site_as_well_as_entity()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "View", roleId: 2), assignId: false));
            await store.AddAsync(PermissionMapper.ToAggregate(NewPermission(2, "Page", 7, "View", roleId: 9), assignId: false));

            using var readContext = NewContext();
            var siteOne = readContext.Set<PermissionAggregate>()
                .Where(item => item.SiteId == 1 && item.EntityName == "Page")
                .ToList();

            Assert.Equal(2, Assert.Single(siteOne).RoleId);
        }

        // The polymorphic key is (EntityName, EntityId) and NOTHING enforces it — a Page and a Folder can
        // share an EntityId, so a filter on EntityId alone silently returns another entity's permissions.
        [Fact]
        public async Task Permissions_are_isolated_by_entity_name_as_well_as_entity_id()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(PermissionMapper.ToAggregate(NewPermission(1, "Page", 1, "View", roleId: 2), assignId: false));
            await store.AddAsync(PermissionMapper.ToAggregate(NewPermission(1, "Folder", 1, "View", roleId: 3), assignId: false));

            using var readContext = NewContext();
            var pagePermissions = readContext.Set<PermissionAggregate>()
                .Where(item => item.EntityName == "Page" && item.EntityId == 1)
                .ToList();

            Assert.Equal(2, Assert.Single(pagePermissions).RoleId);
        }

        [Fact]
        public async Task An_updated_permission_persists_its_new_authorization()
        {
            using var context = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "Edit", roleId: 2, isAuthorized: true), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            var model = PermissionMapper.ToModel(aggregate);
            model.IsAuthorized = false;

            using var writeContext = NewContext();
            await NewStore(writeContext).UpdateAsync(PermissionMapper.ToAggregate(model, assignId: true));

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.False(loaded.IsAuthorized);
        }

        [Fact]
        public async Task A_deleted_permission_is_gone()
        {
            using var context = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "View", roleId: 2), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var deleteContext = NewContext();
            var deleteStore = NewStore(deleteContext);
            await deleteStore.DeleteAsync((await deleteStore.GetByIdAsync(aggregate.Id))!);

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(aggregate.Id));
        }

        // DeletePermissions — and RoleRepository.DeleteRole and UserRepository.DeleteUser — delete aggregates
        // that were read through a SESSION (a different context), so they arrive at the store DETACHED.
        // Remove() must still delete them. This pins the one non-obvious move in all three.
        [Fact]
        public async Task The_store_deletes_a_detached_aggregate_read_through_another_context()
        {
            using var context = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Module", 7, "View", roleId: 2), assignId: false);
            await NewStore(context).AddAsync(aggregate);

            // Read through one context (as the query session does)...
            using var readContext = NewContext();
            var detached = readContext.Set<PermissionAggregate>()
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
            Assert.Empty(verifyContext.Set<PermissionAggregate>().Where(item => item.EntityName == "Module" && item.EntityId == 7));
        }

        // The D19 pair, first half. This is the bug that made Pages' PUT return 200 and persist NOTHING, and
        // it is live in this cluster's shape: PermissionRepository.UpdatePermissions reads existing
        // permissions and then writes them back on ONE repository instance — one store, one context.
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "Edit", roleId: 2), assignId: false);
            await store.AddAsync(aggregate);

            // A tracked read through the STORE — what GetPermissions must never do.
            var tracked = await store.GetByIdAsync(aggregate.Id);
            Assert.NotNull(tracked);

            var rebuilt = PermissionMapper.ToAggregate(
                new Permission
                {
                    PermissionId = aggregate.Id,
                    SiteId = 1,
                    EntityName = "Page",
                    EntityId = 7,
                    PermissionName = "Edit",
                    RoleId = 2,
                    IsAuthorized = false,
                    CreatedBy = "installer",
                    CreatedOn = DateTime.UtcNow,
                    ModifiedBy = "host",
                    ModifiedOn = DateTime.UtcNow
                },
                assignId: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(rebuilt));
        }

        // The D19 pair, second half: the shape the repository actually uses — read through a SESSION (its own
        // context), write through the store — persists.
        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            using var seedContext = NewContext();
            var aggregate = PermissionMapper.ToAggregate(NewPermission(1, "Page", 7, "Edit", roleId: 2), assignId: false);
            await NewStore(seedContext).AddAsync(aggregate);

            // The read: a separate context, as IQuerySession opens.
            using var sessionContext = NewContext();
            var read = sessionContext.Set<PermissionAggregate>().First(item => item.Id == aggregate.Id);
            var model = PermissionMapper.ToModel(read);
            model.IsAuthorized = false;

            // The write: the store's own context, tracker clean.
            using var storeContext = NewContext();
            await NewStore(storeContext).UpdateAsync(PermissionMapper.ToAggregate(model, assignId: true));

            using var verifyContext = NewContext();
            Assert.False((await NewStore(verifyContext).GetByIdAsync(aggregate.Id))!.IsAuthorized);
        }

        public void Dispose() => _connection.Dispose();
    }
}
