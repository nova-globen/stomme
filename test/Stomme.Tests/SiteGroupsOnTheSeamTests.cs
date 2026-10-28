using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Threading;
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
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Stomme.Tests
{
    // The SiteGroup PAIR on the persistence seam (D3/D7/D45): SiteGroup and SiteGroupMember.
    //
    // NOT leaf clusters, and that is the whole point of this file. Every cluster D44 moved was a single table
    // with no navigation in or out; these two are joined by SiteGroupMember.SiteGroup, a MAPPED navigation into
    // the SiteGroup contract. That one member decides everything interesting here:
    //
    //   * It made the pair indivisible. Mapping SiteGroupAggregate onto site_group while the navigation stayed
    //     mapped made EF rediscover the SiteGroup CONTRACT through it and claim the same table. That was
    //     OBSERVED before this file was written, not assumed, and SiteGroupsMustNotBeResurrectedTests
    //     reproduces the mechanism so it cannot come back.
    //
    //   * It was NOT dead weight. Twenty-two production reads take member.SiteGroup.Type or .PrimarySiteId —
    //     eighteen in SynchronizationJob, two in ServerSiteService, two in Admin Site/Index.razor. Severing the
    //     navigation and dropping the .Include would have compiled, registered, rendered, and thrown
    //     NullReferenceException on the first synchronization run. SeveringTheSiteGroupNavigationTests is the
    //     guard that would have caught it.
    //
    // Both tables were read off the migrated PostgreSQL database (stomme-pg-44357), not off their
    // EntityBuilders — D44 was bitten by exactly that, and it paid off again here: SiteGroupMember carries a
    // UNIQUE index on (SiteId, SiteGroupId) that SiteGroupMemberEntityBuilder does not create (the migration
    // 10010001_AddSiteGroups adds it separately).
    //
    // Every DDL below is the table as the migration set leaves it, written BY HAND — never EnsureCreated, which
    // would build the schema from the very model under test and could only ever agree with itself.

    /// <summary>
    /// What the two aggregates map to. Same hazard as every cluster before them: an explicit HasColumnName
    /// OUTRANKS the snake_case rewriter, so an un-rewritten explicit name is a column that does not exist on
    /// the one provider whose identifiers are case-sensitive.
    /// </summary>
    public class SiteGroupMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildEntityType(Type aggregate, IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(aggregate)!;
        }

        private static IEntityType BuildOnPostgreSql(Type aggregate) =>
            BuildEntityType(aggregate, new PostgreSQLDatabase(), "Host=localhost;Database=probe");

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        /// <summary>The pair, their tables, and the ONE explicitly-named column each.</summary>
        public static TheoryData<Type, string, string> Clusters() => new()
        {
            { typeof(SiteGroupAggregate), "site_group", "site_group_id" },
            { typeof(SiteGroupMemberAggregate), "site_group_member", "site_group_member_id" }
        };

        // Read off the migrated stomme-pg-44357 database, not inferred.
        [Theory]
        [MemberData(nameof(Clusters))]
        public void Each_aggregate_maps_to_the_snake_case_table_and_key_the_migrations_created_on_postgresql(
            Type aggregate, string table, string keyColumn)
        {
            var entityType = BuildOnPostgreSql(aggregate);

            Assert.Equal(table, entityType.GetTableName());
            Assert.Equal(keyColumn, ColumnsByProperty(entityType)[nameof(AggregateRoot<int>.Id)]);
        }

        [Theory]
        [MemberData(nameof(Clusters))]
        public void The_seam_concurrency_token_is_not_mapped_because_no_harvested_table_has_a_column_for_it(
            Type aggregate, string table, string keyColumn)
        {
            _ = table;
            _ = keyColumn;
            var entityType = BuildOnPostgreSql(aggregate);

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
        }

        // The providers without a rewriter must keep the PascalCase names their migrations created.
        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregates_map_to_pascal_case_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;

            Assert.Equal("SiteGroup", BuildEntityType(typeof(SiteGroupAggregate), database, connectionString).GetTableName());
            Assert.Equal("SiteGroupMember", BuildEntityType(typeof(SiteGroupMemberAggregate), database, connectionString).GetTableName());
            Assert.Equal(
                "SiteGroupMemberId",
                BuildEntityType(typeof(SiteGroupMemberAggregate), database, connectionString)
                    .GetProperties().Single(p => p.Name == nameof(AggregateRoot<int>.Id)).GetColumnName());
        }

        // The nine columns the migrated database actually has. Unlike Language (whose builder creates a `name`
        // column three migrations later dropped), SiteGroup's builder and the live schema agree — which is a
        // finding, not an assumption: it was checked.
        [Fact]
        public void The_sitegroup_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildOnPostgreSql(typeof(SiteGroupAggregate));
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("name", columns[nameof(SiteGroupAggregate.Name)]);
            Assert.Equal("type", columns[nameof(SiteGroupAggregate.Type)]);
            Assert.Equal("primary_site_id", columns[nameof(SiteGroupAggregate.PrimarySiteId)]);
            Assert.Equal("synchronize", columns[nameof(SiteGroupAggregate.Synchronize)]);

            Assert.Equal(
                ["created_by", "created_on", "modified_by", "modified_on", "name", "primary_site_id", "site_group_id", "synchronize", "type"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        // The eight columns the migrated database actually has — and note site_group_id is a plain integer
        // column here, NOT a navigation. See SiteGroupsMustNotBeResurrectedTests for why that matters.
        [Fact]
        public void The_sitegroupmember_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildOnPostgreSql(typeof(SiteGroupMemberAggregate));
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("site_group_id", columns[nameof(SiteGroupMemberAggregate.SiteGroupId)]);
            Assert.Equal("site_id", columns[nameof(SiteGroupMemberAggregate.SiteId)]);
            Assert.Equal("synchronized_on", columns[nameof(SiteGroupMemberAggregate.SynchronizedOn)]);

            Assert.Equal(
                ["created_by", "created_on", "modified_by", "modified_on", "site_group_id", "site_group_member_id", "site_id", "synchronized_on"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        // SynchronizedOn is the pair's only nullable column, and SynchronizationJob leans on the distinction:
        // null means "never synchronized", and it immediately overwrites that with DateTime.MinValue to mean
        // "initializing". Map it non-nullable and the job's first branch changes meaning silently.
        [Fact]
        public void The_member_synchronized_on_column_stays_nullable()
        {
            Assert.True(BuildOnPostgreSql(typeof(SiteGroupMemberAggregate))
                .FindProperty(nameof(SiteGroupMemberAggregate.SynchronizedOn))!.IsNullable);
        }

        // BOTH tables carry the full audit quartet — verified against the live database, where site_group and
        // site_group_member each have created_by/created_on/modified_by/modified_on NOT NULL. DbContextUtils
        // decides whether to stamp them by RUNTIME TYPE TEST (`item.Entity is IAuditable`), so dropping either
        // interface compiles, registers, renders, and silently stops stamping. This assertion is the only
        // guard. (Mutation-checked: removing IAuditable from either aggregate fails this test.)
        [Fact]
        public void Both_sitegroup_aggregates_are_auditable_because_both_tables_have_the_audit_columns()
        {
            foreach (var aggregate in new[] { typeof(SiteGroupAggregate), typeof(SiteGroupMemberAggregate) })
            {
                Assert.True(typeof(IAuditable).IsAssignableFrom(aggregate), $"{aggregate.Name} must be IAuditable for DbContextUtils to stamp it.");
                Assert.True(AuditStamp.IsAuditable(aggregate), $"{aggregate.Name} must match AuditStamp's shape for the RavenDB listener to stamp it.");

                var entityType = BuildOnPostgreSql(aggregate);
                Assert.NotNull(entityType.FindProperty(nameof(IAuditable.CreatedBy)));
                Assert.NotNull(entityType.FindProperty(nameof(IAuditable.CreatedOn)));
                Assert.NotNull(entityType.FindProperty(nameof(IAuditable.ModifiedBy)));
                Assert.NotNull(entityType.FindProperty(nameof(IAuditable.ModifiedOn)));
            }
        }

        // Neither table is soft-deleted — SiteGroupMember rows go away by cascade, SiteGroup rows by DELETE.
        // Pinned because IDeletable is likewise a runtime type test.
        [Fact]
        public void Neither_sitegroup_aggregate_is_deletable()
        {
            Assert.False(typeof(IDeletable).IsAssignableFrom(typeof(SiteGroupAggregate)));
            Assert.False(typeof(IDeletable).IsAssignableFrom(typeof(SiteGroupMemberAggregate)));
        }

        /// <summary>
        /// The tripwire for whoever comes next. The contract and the aggregate are two hand-maintained lists of
        /// the same columns: add a property to the contract, forget the aggregate, and the install still boots
        /// — the value just never persists.
        /// </summary>
        public static TheoryData<Type, Type, string> ContractAndAggregate() => new()
        {
            { typeof(SiteGroup), typeof(SiteGroupAggregate), nameof(SiteGroup.SiteGroupId) },
            { typeof(SiteGroupMember), typeof(SiteGroupMemberAggregate), nameof(SiteGroupMember.SiteGroupMemberId) }
        };

        [Theory]
        [MemberData(nameof(ContractAndAggregate))]
        public void Each_aggregate_persists_exactly_the_columns_the_harvested_entity_did(Type contract, Type aggregate, string idName)
        {
            var persistedOnTheContract = contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // {Entity}Id is the aggregate's Id (AggregateRoot owns the name).
                .Select(name => name == idName ? nameof(AggregateRoot<int>.Id) : name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var persistedOnTheAggregate = BuildOnPostgreSql(aggregate)
                .GetProperties()
                .Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // An insert must leave the identity to the database — both tables are autoincrement and both
        // Controllers' Post read the id back off the response.
        [Fact]
        public void An_insert_never_carries_a_client_assigned_identity()
        {
            Assert.Equal(0, SiteGroupMapper.ToAggregate(new SiteGroup { SiteGroupId = 99 }, assignId: false).Id);
            Assert.Equal(0, SiteGroupMemberMapper.ToAggregate(new SiteGroupMember { SiteGroupMemberId = 99 }, assignId: false).Id);

            // ...and an update or a delete must carry it, or the write addresses no row.
            Assert.Equal(99, SiteGroupMapper.ToAggregate(new SiteGroup { SiteGroupId = 99 }, assignId: true).Id);
            Assert.Equal(99, SiteGroupMemberMapper.ToAggregate(new SiteGroupMember { SiteGroupMemberId = 99 }, assignId: true).Id);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_sitegroup_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new SiteGroup
            {
                SiteGroupId = 4,
                Name = "Localized Sites",
                Type = SiteGroupTypes.Localization,
                PrimarySiteId = 1,
                Synchronize = true,
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            AssertRoundTrips(original, SiteGroupMapper.ToModel(SiteGroupMapper.ToAggregate(original, assignId: true)));
        }

        [Fact]
        public void Every_persisted_sitegroupmember_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new SiteGroupMember
            {
                SiteGroupMemberId = 6,
                SiteGroupId = 4,
                SiteId = 2,
                SynchronizedOn = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc),
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            AssertRoundTrips(original, SiteGroupMemberMapper.ToModel(SiteGroupMemberMapper.ToAggregate(original, assignId: true)));
        }

        // A never-synchronized member. Null is a meaningful value on this column (see
        // The_member_synchronized_on_column_stays_nullable) so the mapper must carry it, not coalesce it.
        [Fact]
        public void A_never_synchronized_member_round_trips_its_null()
        {
            var original = new SiteGroupMember
            {
                SiteGroupMemberId = 6,
                SiteGroupId = 4,
                SiteId = 2,
                SynchronizedOn = null,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            };

            Assert.Null(SiteGroupMemberMapper.ToModel(SiteGroupMemberMapper.ToAggregate(original, assignId: true)).SynchronizedOn);
        }

        private static void AssertRoundTrips<TModel>(TModel original, TModel round)
        {
            foreach (var property in typeof(TModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Where(p => p.CanRead && p.CanWrite))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(round));
            }
        }

        // The severed navigation is [NotMapped] view state, so the mapper DROPS it — the repository puts it
        // back, not this. Pinned because a "helpful" mapper that copied SiteGroup would be re-introducing the
        // very coupling the pair had to break, one layer up.
        [Fact]
        public void The_mapper_does_not_round_trip_the_severed_navigation()
        {
            var original = new SiteGroupMember
            {
                SiteGroupMemberId = 6,
                SiteGroupId = 4,
                SiteId = 2,
                SiteGroup = new SiteGroup { SiteGroupId = 4, Name = "Localized Sites" },
                AliasName = "localhost"
            };

            var round = SiteGroupMemberMapper.ToModel(SiteGroupMemberMapper.ToAggregate(original, assignId: true));

            Assert.Null(round.SiteGroup);
            Assert.Null(round.AliasName);
            // ...but everything with a column does survive.
            Assert.Equal(4, round.SiteGroupId);
            Assert.Equal(2, round.SiteId);
            Assert.Equal(6, round.SiteGroupMemberId);
        }
    }

    // Rule (g)/D19/D22 rule 6: deleting the DbSet is NOT enough — EF rediscovers CLR types through
    // NAVIGATIONS. This pair is the first cluster on the seam where that was not a hypothetical: it was
    // observed, and it is the reason the two had to land in one increment.
    public class SiteGroupsMustNotBeResurrectedTests
    {
        // The unmigrated neighbours, as TenantDBContext still holds them. Note SiteGroup and SiteGroupMember
        // are NOT here any more — that is the state under test.
        //
        // PageModule, Visitor and UrlMapping USED to stand here as neighbours too. D47 put all three on the
        // seam, so listing them now would map each table twice — the aggregate via ConfigureFrameworkStore and
        // the contract via the DbSet — and every test in this class would fail with the D19 blocker on a table
        // this class does not even test. Folder and File are the neighbours that are still genuinely
        // unmigrated, which is the property this fixture needs.
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

        public static TheoryData<Type, Type, string> ContractAggregateAndTable() => new()
        {
            { typeof(SiteGroup), typeof(SiteGroupAggregate), "site_group" },
            { typeof(SiteGroupMember), typeof(SiteGroupMemberAggregate), "site_group_member" }
        };

        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void Each_table_is_owned_by_its_aggregate_alone(Type contract, Type aggregate, string table)
        {
            _ = contract;
            var mappedToTheTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == table)
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([aggregate], mappedToTheTable);
        }

        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void Each_contract_is_not_an_entity_type_of_the_tenant_context(Type contract, Type aggregate, string table)
        {
            _ = aggregate;
            _ = table;
            Assert.Null(BuildModel().FindEntityType(contract));
        }

        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void No_entity_in_the_model_navigates_into_the_sitegroup_contract(Type contract, Type aggregate, string table)
        {
            _ = table;
            var navigationsIn = BuildModel().GetEntityTypes()
                .SelectMany(entityType => entityType.GetNavigations())
                .Where(navigation => navigation.TargetEntityType.ClrType == contract
                    || navigation.TargetEntityType.ClrType == aggregate)
                .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name}")
                .ToList();

            Assert.Empty(navigationsIn);
        }

        // The member aggregate references the group BY ID and must never grow a navigation back. This is the
        // assertion that keeps the pair severed from the seam side: a HasOne(...).WithMany() added to
        // FrameworkStoreModel would re-open the blocker below from the other direction.
        [Fact]
        public void The_member_aggregate_references_its_group_by_id_and_not_by_navigation()
        {
            var entityType = BuildModel().FindEntityType(typeof(SiteGroupMemberAggregate))!;

            Assert.Empty(entityType.GetNavigations());
            Assert.Empty(entityType.GetForeignKeys());
            // ...and the id it references by is an ordinary scalar column.
            Assert.Equal("site_group_id", entityType.FindProperty(nameof(SiteGroupMemberAggregate.SiteGroupId))!.GetColumnName());
        }

        /// <summary>
        /// THE BLOCKER ITSELF, reproduced. This is the failure that was OBSERVED before the navigation was
        /// severed, and the reason SiteGroup could not land without SiteGroupMember.
        /// </summary>
        /// <remarks>
        /// The stand-in below is what <c>SiteGroupMember</c> WAS: an entity with a plain (mapped) reference to
        /// the <see cref="SiteGroup"/> contract. There is deliberately no <c>DbSet&lt;SiteGroup&gt;</c> anywhere
        /// in this context — the navigation ALONE is enough for EF to discover the contract, map it to
        /// <c>site_group</c>, and collide with <see cref="SiteGroupAggregate"/>. That is the whole finding: the
        /// DbSet was never the problem, the navigation was.
        /// </remarks>
        private sealed class ResurrectingContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            public DbSet<MemberWithAMappedNavigation> Member { get; set; }

            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        // Deliberately NOT [NotMapped] — this type exists to prove what the real contract would do without it.
        // `Id` rather than `SiteGroupMemberId` only so EF discovers a key by convention: without one it fails
        // on the missing primary key FIRST and never reaches the collision this test is about.
        public class MemberWithAMappedNavigation
        {
            public int Id { get; set; }
            public int SiteGroupId { get; set; }
            public SiteGroup SiteGroup { get; set; } = null!;
        }

        [Fact]
        public void A_mapped_navigation_into_the_contract_resurrects_it_and_breaks_the_model()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new ResurrectingContext(builder.Options, database);

            var error = Assert.Throws<InvalidOperationException>(() => context.Model.GetEntityTypes().ToList());

            Assert.Contains("site_group", error.Message);
            Assert.Contains(nameof(SiteGroupAggregate), error.Message);
            // ...and it names the CONTRACT as the type it collides with, which is the mechanism: EF found
            // SiteGroup through MemberWithAMappedNavigation.SiteGroup, not through any DbSet.
            Assert.Contains($"entity type '{nameof(SiteGroup)}'", error.Message);
        }

        // ...and the real contract does NOT do that, because its navigation is severed. The other half of the
        // pair above: same shape, same context config, opposite outcome.
        [Fact]
        public void The_real_contract_is_severed_so_the_same_shape_builds_cleanly()
        {
            Assert.NotNull(typeof(SiteGroupMember).GetProperty(nameof(SiteGroupMember.SiteGroup))!
                .GetCustomAttribute<NotMappedAttribute>());

            // The model builds, and site_group belongs to the aggregate alone.
            Assert.NotNull(BuildModel().FindEntityType(typeof(SiteGroupAggregate)));
            Assert.Null(BuildModel().FindEntityType(typeof(SiteGroup)));
        }

        // The reflective sweep: no entity TenantDBContext maps may carry a mapped member of either contract.
        [Fact]
        public void No_entity_the_tenant_context_maps_carries_a_mapped_member_of_a_sitegroup_contract()
        {
            var contracts = new[] { typeof(SiteGroup), typeof(SiteGroupMember) };

            var entityTypes = typeof(TenantDBContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .ToList();

            // Guard the guard: if the DbSet discovery ever silently finds nothing, the assertion below would
            // pass vacuously and this whole sweep would be worthless.
            Assert.NotEmpty(entityTypes);

            var mappedMembers = entityTypes
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(property => contracts.Any(contract =>
                    property.PropertyType == contract
                    || typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                        && property.PropertyType.IsGenericType
                        && property.PropertyType.GetGenericArguments().Contains(contract)))
                .Where(property => property.GetCustomAttribute<NotMappedAttribute>() is null)
                .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
                .ToList();

            Assert.Empty(mappedMembers);
        }

        // Both DbSets are GONE from TenantDBContext — the compiler proof that no caller bypasses the seam back
        // to the context. Asserted reflectively because the compiler cannot fail a build over a DbSet that
        // someone ADDS BACK.
        [Fact]
        public void The_tenant_context_declares_no_dbset_for_either_sitegroup_cluster()
        {
            var declared = typeof(TenantDBContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .ToList();

            Assert.NotEmpty(declared);
            Assert.DoesNotContain(typeof(SiteGroup), declared);
            Assert.DoesNotContain(typeof(SiteGroupMember), declared);
        }

        // Tenant-side only, like Permission (D24) and unlike Setting (D23).
        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void The_master_context_does_not_map_the_sitegroup_aggregate(Type contract, Type aggregate, string table)
        {
            _ = contract;
            _ = table;
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new MasterLikeContext(builder.Options, database);

            Assert.Null(context.Model.FindEntityType(aggregate));
            // ...while the tenant context does. One database, not two.
            Assert.NotNull(BuildModel().FindEntityType(aggregate));
        }
    }

    /// <summary>The SiteGroup store's behaviour against a real database with the migrated schema.</summary>
    public class SiteGroupStoreBehaviourTests : LeafClusterStoreBehaviourTests<SiteGroupAggregate>
    {
        // The SiteGroup table as the migrations LEAVE it: SiteGroupEntityBuilder.BuildTable plus
        // AddAuditableColumns. Verified against the migrated PostgreSQL database — nine columns, and unlike
        // Language nothing later drops any of them.
        protected override string TableDdl => SiteGroupDdl;

        internal const string SiteGroupDdl = """
            CREATE TABLE "SiteGroup" (
                "SiteGroupId" INTEGER NOT NULL CONSTRAINT "PK_SiteGroup" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "PrimarySiteId" INTEGER NOT NULL,
                "Synchronize" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
            );
            """;

        protected override SiteGroupAggregate NewAggregate() => NewSiteGroup(SiteGroupTypes.Synchronization, primarySiteId: 1, synchronize: true);

        // The mutation is Synchronize true -> false: the exact write SynchronizationJob makes as it picks up a
        // group ("update flag to prevent job from processing group again"). If that write silently stopped
        // persisting, the job would re-process every group on every run, forever.
        protected override SiteGroupAggregate RebuildMutated(int id)
        {
            var model = SiteGroupMapper.ToModel(NewAggregate());
            model.SiteGroupId = id;
            model.Synchronize = false;
            return SiteGroupMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(SiteGroupAggregate loaded) => Assert.False(loaded.Synchronize);

        internal static SiteGroupAggregate NewSiteGroup(string type, int primarySiteId, bool synchronize) =>
            SiteGroupMapper.ToAggregate(new SiteGroup
            {
                Name = "Group " + type,
                Type = type,
                PrimarySiteId = primarySiteId,
                Synchronize = synchronize,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            }, assignId: false);
    }

    /// <summary>The SiteGroupMember store's behaviour against a real database with the migrated schema.</summary>
    public class SiteGroupMemberStoreBehaviourTests : LeafClusterStoreBehaviourTests<SiteGroupMemberAggregate>
    {
        // BOTH tables, because the member's foreign key and the pair's unique index are part of what is under
        // test here. The FK and the UNIQUE index come from 10010001_AddSiteGroups — the index is NOT on
        // SiteGroupMemberEntityBuilder and was read off the live database.
        protected override string TableDdl => SiteGroupStoreBehaviourTests.SiteGroupDdl + SiteGroupMemberDdl;

        internal const string SiteGroupMemberDdl = """
            CREATE TABLE "SiteGroupMember" (
                "SiteGroupMemberId" INTEGER NOT NULL CONSTRAINT "PK_SiteGroupMember" PRIMARY KEY AUTOINCREMENT,
                "SiteGroupId" INTEGER NOT NULL,
                "SiteId" INTEGER NOT NULL,
                "SynchronizedOn" TEXT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX "IX_SiteGroupMember" ON "SiteGroupMember" ("SiteId", "SiteGroupId");
            """;

        protected override SiteGroupMemberAggregate NewAggregate() => NewMember(siteGroupId: 1, siteId: 1);

        // The mutation is SynchronizedOn: the exact write SynchronizationJob makes after synchronizing a site
        // ("set synchronized date/time"). If that silently stopped persisting, every site would re-synchronize
        // from scratch on every run.
        protected override SiteGroupMemberAggregate RebuildMutated(int id)
        {
            var model = SiteGroupMemberMapper.ToModel(NewAggregate());
            model.SiteGroupMemberId = id;
            model.SynchronizedOn = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            return SiteGroupMemberMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(SiteGroupMemberAggregate loaded) =>
            Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), loaded.SynchronizedOn);

        internal static SiteGroupMemberAggregate NewMember(int siteGroupId, int siteId) =>
            SiteGroupMemberMapper.ToAggregate(new SiteGroupMember
            {
                SiteGroupId = siteGroupId,
                SiteId = siteId,
                SynchronizedOn = null,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            }, assignId: false);

        // A never-synchronized member persists its null — SynchronizationJob reads null as "never" and the
        // column is the pair's only nullable one.
        [Fact]
        public async Task A_member_persists_a_null_synchronized_on()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            Assert.Null((await NewStore(readContext).GetByIdAsync(aggregate.Id))!.SynchronizedOn);
        }

        // THE LINE THAT DISAPPEARED, proven gone rather than assumed gone. The harvested update said
        //     db.Entry(siteGroupMember.SiteGroup).State = EntityState.Unchanged; // prevent update of linked entity
        // because the member arrived with its group attached and marking the member Modified would otherwise
        // have dragged the group's row into the write. With the navigation severed the tracker has nothing to
        // reach: this test mutates the group the CALLER carries, writes the member, and asserts the group's row
        // is untouched. If someone ever re-adds the navigation, this fails.
        [Fact]
        public async Task Updating_a_member_never_writes_the_group_it_carries()
        {
            using var seedContext = NewContext();
            var group = SiteGroupStoreBehaviourTests.NewSiteGroup(SiteGroupTypes.Synchronization, primarySiteId: 1, synchronize: true);
            await NewStore<SiteGroupAggregate>(seedContext).AddAsync(group);

            var member = NewMember(siteGroupId: group.Id, siteId: 1);
            using var memberContext = NewContext();
            await NewStore(memberContext).AddAsync(member);

            // The caller's model carries the group — exactly as SynchronizationJob's member does, because the
            // repository populated it — and the caller has scribbled on it.
            var model = SiteGroupMemberMapper.ToModel(member);
            model.SynchronizedOn = DateTime.UtcNow;
            model.SiteGroup = SiteGroupMapper.ToModel(group);
            model.SiteGroup.Name = "SCRIBBLED";
            model.SiteGroup.Synchronize = false;

            using var updateContext = NewContext();
            await NewStore(updateContext).UpdateAsync(SiteGroupMemberMapper.ToAggregate(model, assignId: true));

            // The group's row is untouched.
            using var verifyContext = NewContext();
            var reloaded = (await NewStore<SiteGroupAggregate>(verifyContext).GetByIdAsync(group.Id))!;
            Assert.Equal("Group " + SiteGroupTypes.Synchronization, reloaded.Name);
            Assert.True(reloaded.Synchronize);
        }

        // The unique index the EntityBuilder does not create but the migration does. Pinned because it is a
        // real constraint on the relational providers that a reader of the builder alone would not know about —
        // and because it is the one thing about this table that does NOT carry over to RavenDB.
        [Fact]
        public async Task A_site_may_join_a_group_only_once()
        {
            using var context = NewContext();
            await NewStore(context).AddAsync(NewMember(siteGroupId: 1, siteId: 1));

            using var duplicateContext = NewContext();
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => NewStore(duplicateContext).AddAsync(NewMember(siteGroupId: 1, siteId: 1)));
        }
    }

    /// <summary>
    /// THE PAIR-SPECIFIC GUARD: severing <c>SiteGroupMember.SiteGroup</c> lost no data and no behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.Include(item =&gt; item.SiteGroup)</c> that these tests replace was populating a navigation that
    /// twenty-two production reads consume — <c>SynchronizationJob</c> branches its entire synchronize/compare
    /// behaviour on <c>member.SiteGroup.Type</c>, <c>ServerSiteService.GetSitesAsync</c> filters on it, and the
    /// Admin site editor reads <c>member.SiteGroup.PrimarySiteId</c> to decide whether a site is a group's
    /// primary. None of that is visible to the compiler: drop the include, and every one of those reads
    /// dereferences null at RUNTIME, on a background job, on a schedule.
    /// </para>
    /// <para>
    /// So these tests drive the REAL <see cref="SiteGroupMemberRepository"/> — not the store — through each
    /// read shape those callers actually use, and assert the group arrives populated.
    /// </para>
    /// </remarks>
    public class SeveringTheSiteGroupNavigationTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        public SeveringTheSiteGroupNavigationTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = SiteGroupStoreBehaviourTests.SiteGroupDdl + SiteGroupMemberStoreBehaviourTests.SiteGroupMemberDdl;
            command.ExecuteNonQuery();
        }

        private sealed class StoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private sealed class StubTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        private DbContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private sealed class TestSiteGroupStore(DbContext context) : ISiteGroupStore
        {
            private readonly IRepository<SiteGroupAggregate, int> _repository =
                new EfRepository<SiteGroupAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

            public Task<SiteGroupAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _repository.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<SiteGroupAggregate>> GetAllAsync(CancellationToken ct = default) => _repository.GetAllAsync(ct);
            public Task AddAsync(SiteGroupAggregate a, CancellationToken ct = default) => _repository.AddAsync(a, ct);
            public Task UpdateAsync(SiteGroupAggregate a, CancellationToken ct = default) => _repository.UpdateAsync(a, ct);
            public Task DeleteAsync(SiteGroupAggregate a, CancellationToken ct = default) => _repository.DeleteAsync(a, ct);
        }

        private sealed class TestSiteGroupMemberStore(DbContext context) : ISiteGroupMemberStore
        {
            private readonly IRepository<SiteGroupMemberAggregate, int> _repository =
                new EfRepository<SiteGroupMemberAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

            public Task<SiteGroupMemberAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _repository.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<SiteGroupMemberAggregate>> GetAllAsync(CancellationToken ct = default) => _repository.GetAllAsync(ct);
            public Task AddAsync(SiteGroupMemberAggregate a, CancellationToken ct = default) => _repository.AddAsync(a, ct);
            public Task UpdateAsync(SiteGroupMemberAggregate a, CancellationToken ct = default) => _repository.UpdateAsync(a, ct);
            public Task DeleteAsync(SiteGroupMemberAggregate a, CancellationToken ct = default) => _repository.DeleteAsync(a, ct);
        }

        private sealed class TestSessionFactory(Func<DbContext> context) : IFrameworkStoreSessionFactory
        {
            private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = context
            });

            public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);
            public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
        }

        // Each repository gets a FRESH store, as a transient registration hands out per resolve.
        private SiteGroupMemberRepository NewMemberRepository() =>
            new(new TestSiteGroupMemberStore(NewContext()), new TestSessionFactory(NewContext));

        private SiteGroupRepository NewGroupRepository() =>
            new(new TestSiteGroupStore(NewContext()), new TestSessionFactory(NewContext));

        private async Task<(SiteGroup Group, SiteGroupMember Member)> SeedAsync(string type = SiteGroupTypes.Synchronization, int primarySiteId = 1, int siteId = 2)
        {
            var group = await NewGroupRepository().AddSiteGroupAsync(new SiteGroup
            {
                Name = "Group " + type,
                Type = type,
                PrimarySiteId = primarySiteId,
                Synchronize = true,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            });

            var member = await NewMemberRepository().AddSiteGroupMemberAsync(new SiteGroupMember
            {
                SiteGroupId = group.SiteGroupId,
                SiteId = siteId,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            });

            return (group, member);
        }

        // THE READ SynchronizationJob:62 AND ServerSiteService:338 MAKE. Both call the no-arg overload and then
        // immediately read member.SiteGroup.Type. If SiteGroup came back null, the synchronization job would
        // throw on its first iteration.
        [Fact]
        public async Task Reading_every_member_populates_the_group_the_synchronization_job_reads()
        {
            var (group, _) = await SeedAsync(SiteGroupTypes.Synchronization);

            var members = (await NewMemberRepository().GetSiteGroupMembersAsync()).ToList();

            var member = Assert.Single(members);
            Assert.NotNull(member.SiteGroup);
            // The two members SynchronizationJob and ServerSiteService actually dereference.
            Assert.Equal(SiteGroupTypes.Synchronization, member.SiteGroup.Type);
            Assert.Equal(group.PrimarySiteId, member.SiteGroup.PrimarySiteId);
        }

        // THE READ Admin Site/Index.razor:1269 AND :1425 MAKE — filtered by group, then
        // member.SiteGroup.PrimarySiteId at :1297.
        [Fact]
        public async Task Reading_a_groups_members_populates_the_group_the_site_editor_reads()
        {
            var (group, _) = await SeedAsync(SiteGroupTypes.Localization, primarySiteId: 7);

            var members = (await NewMemberRepository().GetSiteGroupMembersAsync(-1, group.SiteGroupId)).ToList();

            var member = Assert.Single(members);
            Assert.NotNull(member.SiteGroup);
            Assert.Equal(7, member.SiteGroup.PrimarySiteId);
        }

        // THE READ Index.razor:1306/:1373 MAKES via SiteGroupMemberService, and the single-member path through
        // the controller.
        [Fact]
        public async Task Reading_one_member_populates_its_group()
        {
            var (group, member) = await SeedAsync(SiteGroupTypes.ChangeDetection);

            var read = await NewMemberRepository().GetSiteGroupMemberAsync(member.SiteGroupMemberId);

            Assert.NotNull(read.SiteGroup);
            Assert.Equal(SiteGroupTypes.ChangeDetection, read.SiteGroup.Type);
            Assert.Equal(group.SiteGroupId, read.SiteGroup.SiteGroupId);
        }

        // ...and the tracking overload, which SiteGroupMemberController.Put calls with false, must populate it
        // too. `tracking` is inert on the seam (see SiteGroupRepository's remarks) — this pins that "inert"
        // means "same result", not "degraded result".
        [Fact]
        public async Task The_tracking_parameter_returns_the_same_populated_member_either_way()
        {
            var (_, member) = await SeedAsync();

            var tracked = await NewMemberRepository().GetSiteGroupMemberAsync(member.SiteGroupMemberId, true);
            var untracked = await NewMemberRepository().GetSiteGroupMemberAsync(member.SiteGroupMemberId, false);

            Assert.NotNull(tracked.SiteGroup);
            Assert.NotNull(untracked.SiteGroup);
            Assert.Equal(tracked.SiteGroup.Type, untracked.SiteGroup.Type);
            Assert.Equal(tracked.SiteGroupMemberId, untracked.SiteGroupMemberId);
        }

        [Fact]
        public async Task The_tracking_parameter_returns_the_same_detached_group_either_way()
        {
            var (group, _) = await SeedAsync();

            var tracked = await NewGroupRepository().GetSiteGroupAsync(group.SiteGroupId, true);
            var untracked = await NewGroupRepository().GetSiteGroupAsync(group.SiteGroupId, false);

            Assert.Equal(tracked.SiteGroupId, untracked.SiteGroupId);
            Assert.Equal(tracked.Name, untracked.Name);
        }

        // Several members of ONE group: the stitch must give each its group, and must not confuse two groups.
        // This is the shape SynchronizationJob's inner loop walks (every member of the group it is processing).
        [Fact]
        public async Task Every_member_of_every_group_gets_its_own_group()
        {
            var (synchronization, _) = await SeedAsync(SiteGroupTypes.Synchronization, primarySiteId: 1, siteId: 2);
            var (localization, _) = await SeedAsync(SiteGroupTypes.Localization, primarySiteId: 3, siteId: 4);

            // A second member of the FIRST group — the case a naive one-group-per-read stitch gets wrong.
            await NewMemberRepository().AddSiteGroupMemberAsync(new SiteGroupMember
            {
                SiteGroupId = synchronization.SiteGroupId,
                SiteId = 5,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            });

            var members = (await NewMemberRepository().GetSiteGroupMembersAsync()).ToList();

            Assert.Equal(3, members.Count);
            Assert.All(members, member => Assert.NotNull(member.SiteGroup));
            // Each member's group is ITS group, not just any group.
            Assert.All(members, member => Assert.Equal(member.SiteGroupId, member.SiteGroup.SiteGroupId));
            Assert.Equal(2, members.Count(member => member.SiteGroup.Type == SiteGroupTypes.Synchronization));
            Assert.Equal(1, members.Count(member => member.SiteGroup.Type == SiteGroupTypes.Localization));
            Assert.Equal(3, members.Single(member => member.SiteGroup.Type == SiteGroupTypes.Localization).SiteGroup.PrimarySiteId);
            _ = localization;
        }

        // An empty read must not query for groups at all, and must certainly not throw. The guard clause in
        // PopulateSiteGroups exists for this; without it the `Contains` over an empty list is a pointless round
        // trip on every miss.
        [Fact]
        public async Task Reading_no_members_returns_an_empty_list_rather_than_failing()
        {
            Assert.Empty(await NewMemberRepository().GetSiteGroupMembersAsync(-1, 4242));
        }

        [Fact]
        public async Task Reading_an_unknown_member_returns_null()
        {
            Assert.Null(await NewMemberRepository().GetSiteGroupMemberAsync(4242));
        }

        // THE DELIBERATE DIVERGENCE from EF's .Include, stated in PopulateSiteGroups' remarks and pinned here.
        // EF's include over a REQUIRED navigation is an INNER JOIN, so a member whose group had vanished would
        // have been dropped from the result entirely; the stitch returns it with a null group instead. The
        // relational FK makes this unreachable in production (ON DELETE CASCADE), but RavenDB has no such
        // constraint — so this documents which of the two behaviours the seam chose.
        [Fact]
        public async Task A_member_whose_group_is_missing_comes_back_with_a_null_group()
        {
            var (_, member) = await SeedAsync();

            // Orphan the member behind the repository's back — what a document store could produce.
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"DELETE FROM ""SiteGroup"";";
                command.ExecuteNonQuery();
            }

            var read = await NewMemberRepository().GetSiteGroupMemberAsync(member.SiteGroupMemberId);

            // The member is RETURNED, not dropped...
            Assert.NotNull(read);
            Assert.Equal(member.SiteGroupMemberId, read.SiteGroupMemberId);
            // ...with a null group rather than a phantom one.
            Assert.Null(read.SiteGroup);
        }

        // THE D19 SHAPE, through the REAL repositories, which is what SynchronizationJob does: read every
        // member, then write each one back, on ONE repository instance. If the read had gone through the store
        // this would throw the identity-conflict InvalidOperationException instead of persisting.
        [Fact]
        public async Task The_synchronization_jobs_read_then_write_shape_persists_on_one_repository_instance()
        {
            var (_, _) = await SeedAsync();

            var repository = NewMemberRepository();
            var members = (await repository.GetSiteGroupMembersAsync()).ToList();

            var synchronizedOn = new DateTime(2024, 8, 9, 10, 11, 12, DateTimeKind.Utc);
            foreach (var member in members)
            {
                member.SynchronizedOn = synchronizedOn;
                await repository.UpdateSiteGroupMemberAsync(member);
            }

            // ...and it actually PERSISTED. The row surviving is not evidence — a no-op update leaves it
            // surviving too.
            var reread = (await NewMemberRepository().GetSiteGroupMembersAsync()).ToList();
            Assert.Equal(synchronizedOn, Assert.Single(reread).SynchronizedOn);
        }

        // The same shape on the GROUP repository — SynchronizationJob reads every group then writes
        // Synchronize=false back on each, on one instance.
        [Fact]
        public async Task The_synchronization_jobs_group_read_then_write_shape_persists_on_one_repository_instance()
        {
            await SeedAsync();

            var repository = NewGroupRepository();
            var groups = (await repository.GetSiteGroupsAsync()).ToList();

            foreach (var group in groups)
            {
                group.Synchronize = false;
                await repository.UpdateSiteGroupAsync(group);
            }

            Assert.False(Assert.Single(await NewGroupRepository().GetSiteGroupsAsync()).Synchronize);
        }

        // Deleting an unknown id returns rather than throwing — the divergence every migrated cluster settled
        // on. The harvested code threw NullReferenceException.
        [Fact]
        public async Task Deleting_an_unknown_group_or_member_returns_silently()
        {
            await NewGroupRepository().DeleteSiteGroupAsync(4242);
            await NewMemberRepository().DeleteSiteGroupMemberAsync(4242);
        }

        [Fact]
        public async Task A_deleted_member_is_gone_and_leaves_its_group_standing()
        {
            var (group, member) = await SeedAsync();

            await NewMemberRepository().DeleteSiteGroupMemberAsync(member.SiteGroupMemberId);

            Assert.Null(await NewMemberRepository().GetSiteGroupMemberAsync(member.SiteGroupMemberId));
            Assert.NotNull(await NewGroupRepository().GetSiteGroupAsync(group.SiteGroupId));
        }

        public void Dispose() => _connection.Dispose();
    }
}
