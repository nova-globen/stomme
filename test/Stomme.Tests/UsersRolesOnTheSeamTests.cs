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
    // The Users/Roles cluster on the persistence seam (D3/D7/D22). Same hazards as Sites, Pages and Modules —
    // the aggregates are mapped onto a schema this context does not migrate, and HasColumnName outranks the
    // snake_case rewriter — plus two that are new to this cluster:
    //
    //  * TenantDBContext IS the ASP.NET Identity store, so this is the first cluster whose aggregates share a
    //    context with a THIRD-PARTY EF model. See IdentityAndTheFrameworkStoreCoexistTests.
    //  * Visitor held an EF navigation INTO User, and UserRole's site filters were database-side join
    //    predicates across a navigation into Role. See VisitorMustNotResurrectUserTests and
    //    TheSiteFilterOnUserRolesTests.
    public class UserAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        internal static IEntityType BuildEntityType(Type aggregateType, IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(aggregateType)!;
        }

        private static IEntityType BuildUserEntityType(IRelationalDatabase database, string connectionString) =>
            BuildEntityType(typeof(UserAggregate), database, connectionString);

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit column name
        // actually breaks: its identifiers are case-sensitive, so "UserId" != user_id.
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildUserEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("user", entityType.GetTableName());
            // The explicitly-named column — the one that would silently keep its PascalCase.
            Assert.Equal("user_id", columns[nameof(UserAggregate.Id)]);
            // Convention-named columns, proving the rewriter still runs around the explicit one.
            Assert.Equal("display_name", columns[nameof(UserAggregate.DisplayName)]);
            Assert.Equal("two_factor_required", columns[nameof(UserAggregate.TwoFactorRequired)]);
            Assert.Equal("is_deleted", columns[nameof(UserAggregate.IsDeleted)]);
        }

        // A near-trap worth pinning. UserEntityBuilder created the column from the string "LastIpAddress",
        // while the property is spelled LastIPAddress. The snake_case rewriter maps BOTH to last_ip_address,
        // so the convention-mapped property lands on the migrated column — but only by luck of the rewriter's
        // consecutive-capital handling, and this is the provider where getting it wrong is unrecoverable.
        [Fact]
        public void The_differently_cased_ip_address_property_still_lands_on_the_migrated_column()
        {
            var entityType = BuildUserEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Equal("last_ip_address", ColumnsByProperty(entityType)[nameof(UserAggregate.LastIPAddress)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildUserEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("User", entityType.GetTableName());
            Assert.Equal("UserId", columns[nameof(UserAggregate.Id)]);
            Assert.Equal("Username", columns[nameof(UserAggregate.Username)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token and the harvested User table
        // has no column for it. Like Page and Module (unlike Site) there is no name collision to make the
        // breakage loud: if it ever gets mapped EF silently expects a `version` column that does not exist and
        // every read of a user fails — which, for this cluster, means nobody can log in.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildUserEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The tripwire for the next agent. User (the wire contract) and UserAggregate (the persisted model) are
        // two hand-maintained lists of the same columns. Add a column to User, forget the aggregate, and the
        // install still boots — the value just never persists. This fails instead.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(User).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // UserId is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == nameof(User.UserId) ? nameof(UserAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildUserEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new User
            {
                UserId = 11,
                Username = "host",
                DisplayName = "Host User",
                Email = "host@example.com",
                TimeZoneId = "Etc/UTC",
                CultureCode = "en-US",
                PhotoFileId = 5,
                LastLoginOn = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
                LastIPAddress = "127.0.0.1",
                TwoFactorRequired = true,
                TwoFactorCode = "123456",
                TwoFactorExpiry = new DateTime(2025, 3, 4, 6, 6, 7, DateTimeKind.Utc),
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc),
                DeletedBy = "deleter",
                DeletedOn = new DateTime(2025, 7, 8, 9, 10, 11, DateTimeKind.Utc),
                IsDeleted = true
            };

            var roundTripped = UserMapper.ToModel(UserMapper.ToAggregate(original, assignId: true));

            // Compare every persisted member reflectively, so a new column cannot be quietly left out.
            foreach (var property in typeof(User).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        [Fact]
        public void An_insert_leaves_the_identity_to_the_database()
        {
            var aggregate = UserMapper.ToAggregate(new User { UserId = 99, Username = "x" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    public class RoleAggregateMappingTests
    {
        private static IEntityType BuildRoleEntityType(IRelationalDatabase database, string connectionString) =>
            UserAggregateMappingTests.BuildEntityType(typeof(RoleAggregate), database, connectionString);

        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

            Assert.Equal("role", entityType.GetTableName());
            Assert.Equal("role_id", columns[nameof(RoleAggregate.Id)]);
            Assert.Equal("site_id", columns[nameof(RoleAggregate.SiteId)]);
            Assert.Equal("is_auto_assigned", columns[nameof(RoleAggregate.IsAutoAssigned)]);
        }

        // Role.SiteId is NULLABLE and that nullability is the whole global-role (Host) mechanism: every
        // site-scoped read is `SiteId == siteId || SiteId == null`. If the aggregate mapped it as required,
        // the Host role would vanish from every site — which would lock the host user out of administration.
        [Fact]
        public void The_site_reference_stays_nullable_because_a_null_site_is_a_global_role()
        {
            var entityType = BuildRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.True(entityType.FindProperty(nameof(RoleAggregate.SiteId))!.IsNullable);
        }

        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Role).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                .Select(name => name == nameof(Role.RoleId) ? nameof(RoleAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Role
            {
                RoleId = 7,
                SiteId = 1,
                Name = "Administrators",
                Description = "Site administrators",
                IsAutoAssigned = false,
                IsSystem = true,
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = RoleMapper.ToModel(RoleMapper.ToAggregate(original, assignId: true));

            foreach (var property in typeof(Role).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }
    }

    public class UserRoleAggregateMappingTests
    {
        private static IEntityType BuildUserRoleEntityType(IRelationalDatabase database, string connectionString) =>
            UserAggregateMappingTests.BuildEntityType(typeof(UserRoleAggregate), database, connectionString);

        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildUserRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

            Assert.Equal("user_role", entityType.GetTableName());
            Assert.Equal("user_role_id", columns[nameof(UserRoleAggregate.Id)]);
            // The two cross-aggregate references that replaced the severed navigations.
            Assert.Equal("user_id", columns[nameof(UserRoleAggregate.UserId)]);
            Assert.Equal("role_id", columns[nameof(UserRoleAggregate.RoleId)]);
        }

        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildUserRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The severed navigations must NOT come back as mapped properties on the aggregate either.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(UserRole).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                .Select(name => name == nameof(UserRole.UserRoleId) ? nameof(UserRoleAggregate.Id) : name)
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildUserRoleEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new UserRole
            {
                UserRoleId = 3,
                UserId = 11,
                RoleId = 7,
                EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiryDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = UserRoleMapper.ToModel(UserRoleMapper.ToAggregate(original, assignId: true));

            foreach (var property in typeof(UserRole).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        // The mapper deliberately does NOT populate the severed objects — UserRoleRepository.Hydrate does,
        // from the User and Role aggregates. Pinned so a future "helpful" edit does not half-populate them
        // from a UserRoleAggregate that has no such data to give.
        [Fact]
        public void The_mapper_leaves_the_severed_objects_to_the_repository()
        {
            var model = UserRoleMapper.ToModel(new UserRoleAggregate { UserId = 1, RoleId = 2 });

            Assert.Null(model.User);
            Assert.Null(model.Role);
        }
    }

    // The rule Pages paid for (D19) and Modules restated (rule 6), applied to the Users/Roles cluster.
    //
    // Visitor.User WAS an EF navigation, eager-loaded with .Include(item => item.User) in
    // VisitorRepository.GetVisitors. A navigation to User makes EF discover User as an entity type mapped to
    // the "User" table — which UserAggregate now owns — and the model fails hard at startup:
    //
    //   "The table 'user' cannot be used for entity type 'UserAggregate' since it is being used for entity
    //    type 'User' and potentially other entity types, but there is no linking relationship."
    //
    // This one was NOT flagged in the brief's hazard list; it was found by sweeping for navigations into the
    // cluster's CLR types before starting, which is exactly what rule 6 exists for.
    public class VisitorMustNotResurrectUserTests
    {
        // The unmigrated neighbours that reference the migrated clusters — the shape of TenantDBContext after
        // the Users/Roles cluster moved to the seam.
        //
        // The Permission CONTRACT used to be declared here, because Permission.Role was the OTHER navigation
        // into this cluster. D24 moved Permission onto the seam, so it is now mapped as PermissionAggregate by
        // ConfigureFrameworkStore and must NOT be declared here — two entity types on the `permission` table
        // breaks the model. Permission.Role remains [NotMapped] + [Obsolete] and
        // PermissionMustNotBeResurrectedTests now pins that from the Permission side.
        //
        // The Visitor CONTRACT used to be declared here for exactly the same reason — Visitor.User was the
        // navigation this class is named for. D47 moved Visitor onto the seam, so it is now mapped as
        // VisitorAggregate and declaring the contract here would put two entity types on the `visitor` table.
        // The protection did not go away, it MOVED: VisitorAggregate references the user by id and has no
        // navigation to lose, which is asserted below and pinned from the Visitor side by
        // VisitorMustNotBeResurrectedTests in VisitorsUrlMappingsAndPageModulesOnTheSeamTests.cs. Folder is
        // the neighbour that is still genuinely unmigrated.
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
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

        // Mutation check: give VisitorAggregate a `public User User { get; set; }` navigation and this fails
        // (the model throws while building) — which is exactly the protection being asserted. Until D47 the
        // same check was "remove [NotMapped] from Visitor.User", back when the Visitor CONTRACT was the entity
        // this context tracked; the hazard is identical, only the type carrying it changed.
        [Fact]
        public void The_user_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToTheUserTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "user")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(UserAggregate)], mappedToTheUserTable);
        }

        [Fact]
        public void The_role_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToTheRoleTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "role")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(RoleAggregate)], mappedToTheRoleTable);
        }

        [Fact]
        public void The_wire_contracts_are_not_entity_types_of_the_tenant_context()
        {
            var model = BuildModel();

            Assert.Null(model.FindEntityType(typeof(User)));
            Assert.Null(model.FindEntityType(typeof(Role)));
            Assert.Null(model.FindEntityType(typeof(UserRole)));
        }

        // Visitors moved to the seam in D47. This test used to read
        // "Visitor_is_still_a_plain_entity_referencing_the_user_by_id" and assert over the Visitor CONTRACT;
        // the contract is not an entity type any more, so the assertion is made over the AGGREGATE that
        // replaced it. What is being pinned is unchanged and is D22's, not D47's: whatever entity holds the
        // visitor row must reach the User cluster BY ID, because a navigation would rediscover User as a
        // second entity type on the `user` table and break the model at startup.
        [Fact]
        public void The_visitor_row_reaches_the_user_by_id_and_never_by_navigation()
        {
            var model = BuildModel();

            // The contract is gone from the model — D47 replaced it with the aggregate.
            Assert.Null(model.FindEntityType(typeof(Visitor)));

            var visitor = model.FindEntityType(typeof(VisitorAggregate))!;
            Assert.Equal("visitor", visitor.GetTableName());
            Assert.NotNull(visitor.FindProperty(nameof(VisitorAggregate.UserId)));
            // The aggregate has no User member at all to navigate through — the strongest form of the
            // guarantee, and stronger than the [NotMapped] the contract relied on.
            Assert.Empty(visitor.GetNavigations());
        }
    }

    // The hazard the brief flagged first and hardest: TenantDBContext IS the ASP.NET Identity store
    // (AddIdentityCore<IdentityUser>().AddEntityFrameworkStores<TenantDBContext>() is still the live
    // authentication path). This is the first cluster whose aggregates share a context with a third-party EF
    // model, so these pin WHY the two coexist rather than merely that they do today.
    //
    // They coexist because the two models are DISJOINT: Identity maps IdentityUser onto AspNetUsers, the
    // framework store maps Stomme's own User table, and nothing navigates between them — the two rows are
    // correlated by USERNAME in application code. A future edit that "unifies" them (a navigation, a shared
    // key, an IdentityUser-derived User) breaks that, and would be an identity cutover rather than a mapping
    // change. These tests are the tripwire for that.
    public class IdentityAndTheFrameworkStoreCoexistTests
    {
        private sealed class IdentityLikeContext(DbContextOptions options, IRelationalDatabase database)
            : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityUserContext<Microsoft.AspNetCore.Identity.IdentityUser>(options)
        {
            protected override void OnModelCreating(ModelBuilder builder)
            {
                base.OnModelCreating(builder);
                database.UpdateIdentityStoreTableNames(builder);
                builder.ConfigureFrameworkStore(database);
            }
        }

        private static IModel BuildModel()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new IdentityLikeContext(builder.Options, database);
            return context.Model;
        }

        // The load-bearing one: the framework store's aggregates and Identity's entities can be built into ONE
        // model — the real TenantDBContext arrangement — without a table or key collision.
        [Fact]
        public void The_identity_model_and_the_framework_store_aggregates_build_into_one_model()
        {
            var model = BuildModel();

            Assert.NotNull(model.FindEntityType(typeof(Microsoft.AspNetCore.Identity.IdentityUser)));
            Assert.NotNull(model.FindEntityType(typeof(UserAggregate)));
            Assert.NotNull(model.FindEntityType(typeof(RoleAggregate)));
            Assert.NotNull(model.FindEntityType(typeof(UserRoleAggregate)));
        }

        // Identity's user and Stomme's user are DIFFERENT TABLES. If they ever collided, the install would
        // fail on the very first migration — but more insidiously, a partial collision (same table, different
        // columns) would corrupt sign-in.
        [Fact]
        public void Identity_keeps_its_own_table_beside_the_framework_stores_user_table()
        {
            var model = BuildModel();

            var identityUser = model.FindEntityType(typeof(Microsoft.AspNetCore.Identity.IdentityUser))!.GetTableName();
            var frameworkUser = model.FindEntityType(typeof(UserAggregate))!.GetTableName();

            Assert.Equal("asp_net_users", identityUser);
            Assert.Equal("user", frameworkUser);
            Assert.NotEqual(identityUser, frameworkUser);
        }

        // No navigation crosses between the two models — the property that makes the coexistence structural
        // rather than coincidental. The correlation is by username, in application code (UserManager,
        // UserRoleRepository.UpdateSecurityStamp).
        [Fact]
        public void No_navigation_crosses_between_the_identity_model_and_the_framework_store()
        {
            var model = BuildModel();
            var frameworkAggregates = new[] { typeof(UserAggregate), typeof(RoleAggregate), typeof(UserRoleAggregate) };

            foreach (var entityType in model.GetEntityTypes())
            {
                foreach (var navigation in entityType.GetNavigations())
                {
                    var crossesIn = frameworkAggregates.Contains(navigation.TargetEntityType.ClrType);
                    var crossesOut = frameworkAggregates.Contains(entityType.ClrType);

                    Assert.False(crossesIn, $"{entityType.ClrType.Name}.{navigation.Name} navigates INTO the framework store");
                    Assert.False(crossesOut, $"{entityType.ClrType.Name}.{navigation.Name} navigates OUT of the framework store");
                }
            }
        }
    }

    // The behaviour of the migrated repositories' stores, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class UserStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The User/Role/UserRole tables exactly as the tenant migrations leave them: the three EntityBuilders'
        // BuildTable plus AddAuditableColumns (and AddDeletableColumns for User), plus the later migrations
        // that added columns — 03010002_AddUserTwoFactor, 06010301_AddTimeZone and 10010002_AddCultureCode.
        // PascalCase — the SQLite provider has no rewriter. Note "LastIpAddress": the migration's spelling,
        // NOT the property's (LastIPAddress).
        private const string SchemaDdl = """
            CREATE TABLE "User" (
                "UserId" INTEGER NOT NULL CONSTRAINT "PK_User" PRIMARY KEY AUTOINCREMENT,
                "Username" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "Email" TEXT NOT NULL,
                "PhotoFileId" INTEGER NULL,
                "LastLoginOn" TEXT NULL,
                "LastIpAddress" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "DeletedBy" TEXT NULL,
                "DeletedOn" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "TwoFactorRequired" INTEGER NOT NULL,
                "TwoFactorCode" TEXT NULL,
                "TwoFactorExpiry" TEXT NULL,
                "TimeZoneId" TEXT NULL,
                "CultureCode" TEXT NULL
            );
            CREATE TABLE "Role" (
                "RoleId" INTEGER NOT NULL CONSTRAINT "PK_Role" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "IsAutoAssigned" INTEGER NOT NULL,
                "IsSystem" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
            );
            CREATE TABLE "UserRole" (
                "UserRoleId" INTEGER NOT NULL CONSTRAINT "PK_UserRole" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL,
                "RoleId" INTEGER NOT NULL,
                "EffectiveDate" TEXT NULL,
                "ExpiryDate" TEXT NULL,
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

        public UserStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = SchemaDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<UserAggregate, int> NewUserStore(DbContext context) =>
            new EfRepository<UserAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private IRepository<RoleAggregate, int> NewRoleStore(DbContext context) =>
            new EfRepository<RoleAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static User NewUser(string username) => new()
        {
            Username = username,
            DisplayName = username,
            Email = $"{username}@example.com",
            LastIPAddress = "127.0.0.1",
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        private static Role NewRole(string name, int? siteId) => new()
        {
            SiteId = siteId,
            Name = name,
            Description = name,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // name drifted — LastIPAddress vs LastIpAddress being the obvious candidate — SQLite would reject the
        // statement rather than quietly pass.
        [Fact]
        public async Task An_added_user_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var aggregate = UserMapper.ToAggregate(NewUser("host"), assignId: false);

            await NewUserStore(context).AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewUserStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("host", loaded.Username);
            Assert.Equal("127.0.0.1", loaded.LastIPAddress);
        }

        // The sign-in read: UserRepository.GetUser(username) is on the authentication path of every login.
        [Fact]
        public async Task A_user_is_found_by_username_through_a_query_session()
        {
            using (var seed = NewContext())
            {
                await NewUserStore(seed).AddAsync(UserMapper.ToAggregate(NewUser("host"), assignId: false));
                await NewUserStore(seed).AddAsync(UserMapper.ToAggregate(NewUser("someone"), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var user = session.Query<UserAggregate>().FirstOrDefault(item => item.Username == "host");

            Assert.NotNull(user);
            Assert.Equal("host", user.Username);
        }

        // THE read the whole Role cluster hangs on, and the one UserRoleRepository's site filter and
        // PermissionRepository's RoleName denormalization are BOTH rebuilt on. A global role (SiteId null) must
        // come back for every site; a role of another site must not. Mutation check: drop `|| item.SiteId ==
        // null` and this fails (the Host role disappears); drop the whole Where and "Site Two Role" appears.
        [Fact]
        public async Task The_roles_of_a_site_include_global_roles_and_exclude_other_sites()
        {
            using (var seed = NewContext())
            {
                var store = NewRoleStore(seed);
                await store.AddAsync(RoleMapper.ToAggregate(NewRole("Site One Role", siteId: 1), assignId: false));
                await store.AddAsync(RoleMapper.ToAggregate(NewRole("Site Two Role", siteId: 2), assignId: false));
                await store.AddAsync(RoleMapper.ToAggregate(NewRole("Host", siteId: null), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var names = session.Query<RoleAggregate>()
                .Where(item => item.SiteId == 1 || item.SiteId == null)
                .ToList()
                .ConvertAll(item => item.Name)
                .OrderBy(name => name)
                .ToList();

            Assert.Equal(["Host", "Site One Role"], names);
        }

        [Fact]
        public async Task An_updated_user_persists_through_the_store()
        {
            int userId;
            using (var seed = NewContext())
            {
                var aggregate = UserMapper.ToAggregate(NewUser("host"), assignId: false);
                await NewUserStore(seed).AddAsync(aggregate);
                userId = aggregate.Id;
            }

            using (var context = NewContext())
            {
                var user = NewUser("host");
                user.UserId = userId;
                user.DisplayName = "Renamed";
                await NewUserStore(context).UpdateAsync(UserMapper.ToAggregate(user, assignId: true));
            }

            using var readContext = NewContext();
            Assert.Equal("Renamed", (await NewUserStore(readContext).GetByIdAsync(userId))!.DisplayName);
        }

        [Fact]
        public async Task A_deleted_user_is_removed_through_the_store()
        {
            int userId;
            using (var seed = NewContext())
            {
                var aggregate = UserMapper.ToAggregate(NewUser("doomed"), assignId: false);
                await NewUserStore(seed).AddAsync(aggregate);
                userId = aggregate.Id;
            }

            using (var context = NewContext())
            {
                var store = NewUserStore(context);
                await store.DeleteAsync((await store.GetByIdAsync(userId))!);
            }

            using var readContext = NewContext();
            Assert.Null(await NewUserStore(readContext).GetByIdAsync(userId));
        }

        // WHY UserRepository.GetUser reads through the query session instead of IUserAccountStore.GetByIdAsync
        // (D19, inherited from Pages, where the identical shape made a PUT return 200 and persist nothing).
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            int userId;
            using (var seed = NewContext())
            {
                var aggregate = UserMapper.ToAggregate(NewUser("host"), assignId: false);
                await NewUserStore(seed).AddAsync(aggregate);
                userId = aggregate.Id;
            }

            using var context = NewContext();
            var store = NewUserStore(context);

            await store.GetByIdAsync(userId); // tracks the aggregate

            var user = NewUser("host");
            user.UserId = userId;

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.UpdateAsync(UserMapper.ToAggregate(user, assignId: true)));
            Assert.Contains("cannot be tracked", conflict.Message);
        }

        // ...and the shape UserRepository actually uses: read through a query session (its own context, so the
        // store's change tracker stays clean), write through the store. Mutation check: point the read at
        // NewUserStore(context).GetByIdAsync and this fails with the identity conflict above.
        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            int userId;
            using (var seed = NewContext())
            {
                var aggregate = UserMapper.ToAggregate(NewUser("host"), assignId: false);
                await NewUserStore(seed).AddAsync(aggregate);
                userId = aggregate.Id;
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            using var context = NewContext();
            var store = NewUserStore(context);

            await using (var session = sessions.OpenQuerySession())
            {
                Assert.NotNull(session.Query<UserAggregate>().FirstOrDefault(item => item.Id == userId));
            }

            var user = NewUser("host");
            user.UserId = userId;
            user.DisplayName = "After";
            await store.UpdateAsync(UserMapper.ToAggregate(user, assignId: true));

            using var readContext = NewContext();
            Assert.Equal("After", (await NewUserStore(readContext).GetByIdAsync(userId))!.DisplayName);
        }

        public void Dispose() => _connection.Dispose();
    }

    // GetUserRoles(siteId) used to filter `item.Role.SiteId == siteId || item.Role.SiteId == null ||
    // siteId == -1` ACROSS THE JOIN, in the database. Severing the Role navigation made the obvious
    // translation an in-memory filter over every UserRole row in the tenant — which on a shared tenant
    // database (many sites, one database) reads every other site's role assignments on every AUTHENTICATED
    // REQUEST. The replacement resolves the site's role ids first and filters `RoleId IN (...)`, which must
    // still be evaluated by the database.
    //
    // These tests pin that the replacement shape translates to SQL. HONEST LIMITATION: they assert the query
    // SHAPE, not the repository's use of it — a future edit that materializes first and filters in memory
    // would not fail here. The discriminating evidence for the repository itself is the PostgreSQL statement
    // log captured against the live install; see src/Stomme.Server/README.md (persistence seam).
    public class TheSiteFilterOnUserRolesTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        private sealed class StoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        public TheSiteFilterOnUserRolesTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        [Fact]
        public void The_role_id_filter_is_translated_to_sql()
        {
            using var context = NewContext();
            var roleIds = new List<int> { 3, 7 };

            var sql = context.Set<UserRoleAggregate>().Where(item => roleIds.Contains(item.RoleId)).ToQueryString();

            // Translated, not client-evaluated: EF emitted a WHERE on the RoleId column. (EF Core throws rather
            // than silently client-evaluating an untranslatable Where, so reaching this assertion at all is
            // most of the proof.)
            Assert.Contains("WHERE", sql);
            Assert.Contains("RoleId", sql);
        }

        [Fact]
        public void An_empty_role_list_still_filters_rather_than_returning_the_whole_table()
        {
            using var context = NewContext();
            var roleIds = new List<int>();

            // A site with no roles must yield no assignments. The dangerous regression is the opposite: a
            // filter that degrades to "no predicate" would return every site's assignments.
            var sql = context.Set<UserRoleAggregate>().Where(item => roleIds.Contains(item.RoleId)).ToQueryString();

            Assert.Contains("WHERE", sql);
        }

        // The site-scoped role lookup that feeds the IN (...) — the other half of the filter, and the one that
        // must keep the `|| SiteId IS NULL` global-role arm.
        [Fact]
        public void The_site_role_lookup_is_translated_to_sql_including_the_global_role_arm()
        {
            using var context = NewContext();
            var siteId = 1;

            var sql = context.Set<RoleAggregate>()
                .Where(item => item.SiteId == siteId || item.SiteId == null)
                .ToQueryString();

            Assert.Contains("WHERE", sql);
            Assert.Contains("SiteId", sql);
            Assert.Contains("IS NULL", sql);
        }

        public void Dispose() => _connection.Dispose();
    }
}
