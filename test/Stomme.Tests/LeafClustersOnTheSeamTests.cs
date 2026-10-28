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
    // The five LEAF clusters on the persistence seam (D3/D7/D44): Language, Log, Profile, SiteTask,
    // Notification. Lowest-risk of the remaining set — one table each, tenant-side only, no repository edges
    // out and no navigations in either direction — which is exactly why the interesting failures here are the
    // ones that are NOT about topology:
    //
    //   LogAggregate is the first framework-store aggregate that is NOT IAuditable, because the Log table has
    //   no audit columns. NotificationAggregate is the first that is IDeletable rather than IAuditable.
    //   DbContextUtils decides BOTH by runtime type test (`item.Entity is IAuditable` / `is IDeletable`)
    //   against whatever the context is tracking, so getting either wrong compiles, registers, renders, and
    //   silently stops stamping.
    //
    //   Language has no `name` column at all, despite its EntityBuilder creating one — three migrations later
    //   it is gone. Reading the builder and not the database would have mapped a column that is not there.
    //
    // Every DDL below is the table as the migration set leaves it, written BY HAND and cross-checked against a
    // real migrated PostgreSQL database — never EnsureCreated, which would build the schema from the very
    // model under test and could only ever agree with itself.

    /// <summary>
    /// What the aggregates map to. The hazard is the one every cluster before these paid for: these aggregates
    /// are mapped onto a schema this context does not migrate, and an explicit HasColumnName OUTRANKS the
    /// snake_case rewriter — so an un-rewritten explicit name is a column that does not exist, on the one
    /// provider whose identifiers are case-sensitive.
    /// </summary>
    public class LeafClusterMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        internal static IEntityType BuildEntityType(Type aggregate, IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(aggregate)!;
        }

        internal static IEntityType BuildOnPostgreSql(Type aggregate) =>
            BuildEntityType(aggregate, new PostgreSQLDatabase(), "Host=localhost;Database=probe");

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        /// <summary>The five clusters, their tables, and the ONE explicitly-named column each.</summary>
        public static TheoryData<Type, string, string> Clusters() => new()
        {
            { typeof(LanguageAggregate), "language", "language_id" },
            { typeof(LogAggregate), "log", "log_id" },
            { typeof(ProfileAggregate), "profile", "profile_id" },
            { typeof(SiteTaskAggregate), "site_task", "site_task_id" },
            { typeof(NotificationAggregate), "notification", "notification_id" }
        };

        // PostgreSQL is the verification provider and the one where an un-rewritten explicit name actually
        // breaks: its identifiers are case-sensitive, so "LanguageId" != language_id. Every table and key
        // below was read off the migrated stomme-pg-44357 database, not inferred.
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

            Assert.Equal("Language", BuildEntityType(typeof(LanguageAggregate), database, connectionString).GetTableName());
            Assert.Equal("SiteTask", BuildEntityType(typeof(SiteTaskAggregate), database, connectionString).GetTableName());
            Assert.Equal(
                "NotificationId",
                BuildEntityType(typeof(NotificationAggregate), database, connectionString)
                    .GetProperties().Single(p => p.Name == nameof(AggregateRoot<int>.Id)).GetColumnName());
        }

        // Convention-named columns, proving the rewriter still runs around the explicit key.
        //
        // RemoteIPAddress is the one worth its own assertion. The migration named it through IDatabase
        // .RewriteName; the model names it through EFCore.NamingConventions. Those are TWO DIFFERENT
        // implementations, and consecutive capitals are exactly where two snake_case algorithms disagree
        // (remote_ip_address vs remote_ipaddress). They agree — the live database says remote_ip_address — and
        // this pins that they keep agreeing.
        [Fact]
        public void The_log_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var columns = ColumnsByProperty(BuildOnPostgreSql(typeof(LogAggregate)));

            Assert.Equal("site_id", columns[nameof(LogAggregate.SiteId)]);
            Assert.Equal("log_date", columns[nameof(LogAggregate.LogDate)]);
            Assert.Equal("message_template", columns[nameof(LogAggregate.MessageTemplate)]);
            Assert.Equal("remote_ip_address", columns[nameof(LogAggregate.RemoteIPAddress)]);
        }

        [Fact]
        public void The_notification_aggregate_maps_its_later_and_deletable_columns_on_postgresql()
        {
            var columns = ColumnsByProperty(BuildOnPostgreSql(typeof(NotificationAggregate)));

            Assert.Equal("from_display_name", columns[nameof(NotificationAggregate.FromDisplayName)]);
            Assert.Equal("to_email", columns[nameof(NotificationAggregate.ToEmail)]);
            Assert.Equal("send_on", columns[nameof(NotificationAggregate.SendOn)]);
            Assert.Equal("is_read", columns[nameof(NotificationAggregate.IsRead)]);
            Assert.Equal("is_deleted", columns[nameof(NotificationAggregate.IsDeleted)]);
            Assert.Equal("deleted_by", columns[nameof(NotificationAggregate.DeletedBy)]);
        }

        // THE LOG CLUSTER'S DEFINING FACT, pinned so nobody "restores consistency" by adding the audit quartet.
        // The Log table has no audit columns: LogEntityBuilder extends BaseEntityBuilder rather than
        // AuditableBaseEntityBuilder and never calls AddAuditableColumns. Make LogAggregate IAuditable and
        // DbContextUtils would stamp four columns that do not exist — every log write fails, i.e. logging on
        // every request.
        [Fact]
        public void The_log_aggregate_is_not_auditable_because_its_table_has_no_audit_columns()
        {
            Assert.False(typeof(IAuditable).IsAssignableFrom(typeof(LogAggregate)));

            var entityType = BuildOnPostgreSql(typeof(LogAggregate));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.CreatedBy)));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.CreatedOn)));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.ModifiedBy)));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.ModifiedOn)));

            // ...and the seam's duck-typed rule agrees, so the RavenDB audit listener (D26) declines it too —
            // the two providers stamp identically by both stamping nothing.
            Assert.False(AuditStamp.IsAuditable(typeof(LogAggregate)));
        }

        // THE NOTIFICATION CLUSTER'S DEFINING FACT. DbContextUtils stamps DeletedBy/DeletedOn by testing
        // `item.Entity is IDeletable` — a RUNTIME test against the entity the context tracks, which used to be
        // the Notification CONTRACT and is now this aggregate. Drop the interface and everything compiles,
        // every test that does not assert the stamp still passes, and soft deletes silently stop recording who
        // deleted what. There is no compiler error available for this; this assertion is the only guard.
        [Fact]
        public void The_notification_aggregate_is_deletable_but_not_auditable()
        {
            Assert.True(typeof(IDeletable).IsAssignableFrom(typeof(NotificationAggregate)));
            Assert.False(typeof(IAuditable).IsAssignableFrom(typeof(NotificationAggregate)));

            var entityType = BuildOnPostgreSql(typeof(NotificationAggregate));
            Assert.NotNull(entityType.FindProperty(nameof(IDeletable.IsDeleted)));
            Assert.NotNull(entityType.FindProperty(nameof(IDeletable.DeletedBy)));
            // Its CreatedOn is the notification's OWN column, not an audit stamp — so it IS mapped...
            Assert.NotNull(entityType.FindProperty(nameof(Notification.CreatedOn)));
            // ...while the rest of the quartet has no column and must not appear.
            Assert.Null(entityType.FindProperty(nameof(IAuditable.CreatedBy)));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.ModifiedOn)));

            // AuditStamp requires all four, so it correctly declines to stamp this type on RavenDB too.
            Assert.False(AuditStamp.IsAuditable(typeof(NotificationAggregate)));
        }

        [Fact]
        public void The_auditable_leaf_aggregates_are_auditable_on_both_providers()
        {
            foreach (var aggregate in new[] { typeof(LanguageAggregate), typeof(ProfileAggregate), typeof(SiteTaskAggregate) })
            {
                Assert.True(typeof(IAuditable).IsAssignableFrom(aggregate), $"{aggregate.Name} must be IAuditable for DbContextUtils to stamp it.");
                Assert.True(AuditStamp.IsAuditable(aggregate), $"{aggregate.Name} must match AuditStamp's shape for the RavenDB listener to stamp it.");
            }
        }

        // THE LANGUAGE CLUSTER'S DEFINING FACT. LanguageEntityBuilder DOES create a `Name` column, so reading
        // the builder alone says to map it. Three migrations later it is gone for every provider
        // (05020401 dropped it, 06000101 restored it only to match SQLite, 10000201 dropped it everywhere) and
        // the live PostgreSQL database has eight Language columns and no `name`. Mapping it would break every
        // language read on a migrated install.
        [Fact]
        public void The_language_aggregate_has_no_name_column_because_the_migrations_dropped_it()
        {
            var entityType = BuildOnPostgreSql(typeof(LanguageAggregate));

            Assert.Null(entityType.FindProperty("Name"));
            Assert.DoesNotContain("name", entityType.GetProperties().Select(p => p.GetColumnName()));

            // The eight columns the migrated database actually has.
            Assert.Equal(
                ["code", "created_by", "created_on", "is_default", "language_id", "modified_by", "modified_on", "site_id"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        /// <summary>
        /// The tripwire for whoever comes next, per cluster. The contract and the aggregate are two
        /// hand-maintained lists of the same columns: add a property to the contract, forget the aggregate, and
        /// the install still boots — the value just never persists.
        /// </summary>
        public static TheoryData<Type, Type, string> ContractAndAggregate() => new()
        {
            { typeof(Language), typeof(LanguageAggregate), nameof(Language.LanguageId) },
            { typeof(Log), typeof(LogAggregate), nameof(Log.LogId) },
            { typeof(Profile), typeof(ProfileAggregate), nameof(Profile.ProfileId) },
            { typeof(SiteTask), typeof(SiteTaskAggregate), nameof(SiteTask.SiteTaskId) },
            { typeof(Notification), typeof(NotificationAggregate), nameof(Notification.NotificationId) }
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

        // An insert must leave the identity to the database — every one of these tables is an autoincrement
        // column and callers (each cluster's Controller.Post) read the id back off the response.
        [Fact]
        public void An_insert_never_carries_a_client_assigned_identity()
        {
            Assert.Equal(0, LanguageMapper.ToAggregate(new Language { LanguageId = 99 }, assignId: false).Id);
            Assert.Equal(0, LogMapper.ToAggregate(new Log { LogId = 99 }, assignId: false).Id);
            Assert.Equal(0, ProfileMapper.ToAggregate(new Profile { ProfileId = 99 }, assignId: false).Id);
            Assert.Equal(0, SiteTaskMapper.ToAggregate(new SiteTask { SiteTaskId = 99 }, assignId: false).Id);
            Assert.Equal(0, NotificationMapper.ToAggregate(new Notification { NotificationId = 99 }, assignId: false).Id);

            // ...and an update or a delete must carry it, or the write addresses no row.
            Assert.Equal(99, LanguageMapper.ToAggregate(new Language { LanguageId = 99 }, assignId: true).Id);
            Assert.Equal(99, LogMapper.ToAggregate(new Log { LogId = 99 }, assignId: true).Id);
            Assert.Equal(99, ProfileMapper.ToAggregate(new Profile { ProfileId = 99 }, assignId: true).Id);
            Assert.Equal(99, SiteTaskMapper.ToAggregate(new SiteTask { SiteTaskId = 99 }, assignId: true).Id);
            Assert.Equal(99, NotificationMapper.ToAggregate(new Notification { NotificationId = 99 }, assignId: true).Id);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        // Each round-trip below walks the CONTRACT's own persisted properties reflectively, so a property added
        // to the contract and to the aggregate but forgotten in the mapper fails here.

        [Fact]
        public void Every_persisted_language_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Language
            {
                LanguageId = 4,
                SiteId = 1,
                Code = "en-US",
                IsDefault = true,
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            AssertRoundTrips(original, LanguageMapper.ToModel(LanguageMapper.ToAggregate(original, assignId: true)));
        }

        [Fact]
        public void Every_persisted_log_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Log
            {
                LogId = 7,
                SiteId = 1,
                LogDate = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                PageId = 2,
                ModuleId = 3,
                UserId = 4,
                Url = "/page",
                Server = "web-01",
                Instance = "instance-1",
                Category = "Stomme.Tests",
                Feature = "Logging",
                Function = "Other",
                Level = "Error",
                RemoteIPAddress = "127.0.0.1",
                Message = "message",
                MessageTemplate = "template",
                Exception = "boom",
                Properties = "{}"
            };

            AssertRoundTrips(original, LogMapper.ToModel(LogMapper.ToAggregate(original, assignId: true)));
        }

        [Fact]
        public void Every_persisted_profile_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Profile
            {
                ProfileId = 9,
                SiteId = 1,
                Name = "Address",
                Title = "Street Address",
                Description = "Where you live",
                Category = "Contact",
                ViewOrder = 3,
                MaxLength = 200,
                DefaultValue = "",
                IsRequired = true,
                IsPrivate = true,
                Options = "a,b,c",
                Validation = "^.*$",
                Rows = 4,
                Autocomplete = "street-address",
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            AssertRoundTrips(original, ProfileMapper.ToModel(ProfileMapper.ToAggregate(original, assignId: true)));
        }

        [Fact]
        public void Every_persisted_sitetask_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new SiteTask
            {
                SiteTaskId = 11,
                SiteId = 1,
                Name = "Reindex",
                Type = "Stomme.Infrastructure.SomeTask, Stomme.Server",
                Parameters = "{}",
                IsCompleted = true,
                Status = "done",
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "host",
                ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            AssertRoundTrips(original, SiteTaskMapper.ToModel(SiteTaskMapper.ToAggregate(original, assignId: true)));
        }

        // Includes the three IDeletable members, which are ordinary columns on this table. If they stopped
        // round-tripping, a soft-deleted notification would read back as live.
        [Fact]
        public void Every_persisted_notification_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Notification
            {
                NotificationId = 13,
                SiteId = 1,
                FromUserId = 2,
                FromDisplayName = "Sender",
                FromEmail = "from@example.com",
                ToUserId = 3,
                ToDisplayName = "Recipient",
                ToEmail = "to@example.com",
                ParentId = 12,
                Subject = "subject",
                Body = "body",
                CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                IsDelivered = true,
                DeliveredOn = new DateTime(2024, 1, 2, 4, 0, 0, DateTimeKind.Utc),
                SendOn = new DateTime(2024, 1, 2, 3, 30, 0, DateTimeKind.Utc),
                IsRead = true,
                DeletedBy = "host",
                DeletedOn = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                IsDeleted = true
            };

            AssertRoundTrips(original, NotificationMapper.ToModel(NotificationMapper.ToAggregate(original, assignId: true)));
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

        // Language's three [NotMapped] members have no columns, so the mapper DROPS them. Pinned because a
        // future "fix" that round-trips Name would be trying to persist a column that does not exist — see
        // The_language_aggregate_has_no_name_column_because_the_migrations_dropped_it.
        [Fact]
        public void The_mapper_does_not_round_trip_the_unmapped_view_state()
        {
            var original = new Language
            {
                LanguageId = 4,
                SiteId = 1,
                Code = "en-US",
                Name = "English (United States)",
                Version = "10.0.0",
                AliasName = "localhost"
            };

            var round = LanguageMapper.ToModel(LanguageMapper.ToAggregate(original, assignId: true));

            Assert.Null(round.Name);
            Assert.Null(round.Version);
            Assert.Null(round.AliasName);
            // ...but everything with a column does survive.
            Assert.Equal("en-US", round.Code);
            Assert.Equal(4, round.LanguageId);
        }
    }

    // Rule (g)/D19/D22 rule 6: deleting the DbSet is NOT enough — EF rediscovers CLR types through
    // navigations. An entity that navigates into a migrated aggregate's CONTRACT resurrects it as a second
    // entity type on the same table and breaks the model at STARTUP:
    //
    //   "The table 'language' cannot be used for entity type 'LanguageAggregate' since it is being used for
    //    entity type 'Language' and potentially other entity types."
    //
    // The sweep of production code found nothing to sever for any of these five. Site.Languages is the only
    // member of a leaf contract anywhere in the models and it is [NotMapped] — and Site is not an entity type
    // any more anyway (D17). That is the finding; these tests pin it so it stays true.
    public class LeafClustersMustNotBeResurrectedTests
    {
        // The unmigrated neighbours, as TenantDBContext still holds them.
        //
        // PageModule, Visitor and UrlMapping stood here until D47 put all three on the seam. Listing them now
        // would map each table twice — the aggregate via ConfigureFrameworkStore and the contract via the
        // DbSet — and every test in this class would fail with the D19 blocker on a table this class does not
        // test. Folder and File are the neighbours that are still genuinely unmigrated.
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
            { typeof(Language), typeof(LanguageAggregate), "language" },
            { typeof(Log), typeof(LogAggregate), "log" },
            { typeof(Profile), typeof(ProfileAggregate), "profile" },
            { typeof(SiteTask), typeof(SiteTaskAggregate), "site_task" },
            { typeof(Notification), typeof(NotificationAggregate), "notification" }
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

        // The sweep, made executable. Nothing in the tenant model may navigate to one of these contracts — if
        // someone adds such a navigation, EF resurrects it and this fails here rather than at startup.
        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void No_entity_in_the_model_navigates_into_the_leaf_contract(Type contract, Type aggregate, string table)
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

        // The reflective half, and the one that catches what the probe context above cannot: a member of a leaf
        // contract added to an entity TenantDBContext maps but this class never names. It walks
        // TenantDBContext's OWN DbSet declarations, so a new entity is covered the day it is added.
        [Fact]
        public void No_entity_the_tenant_context_maps_carries_a_mapped_member_of_a_leaf_contract()
        {
            var leafContracts = new[] { typeof(Language), typeof(Log), typeof(Profile), typeof(SiteTask), typeof(Notification) };

            var entityTypes = typeof(TenantDBContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .ToList();

            // Guard the guard: if the DbSet discovery ever silently finds nothing, the assertion below would
            // pass vacuously and this whole sweep would be worthless.
            Assert.NotEmpty(entityTypes);

            var mappedLeafMembers = entityTypes
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(property => leafContracts.Any(contract =>
                    property.PropertyType == contract
                    || typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                        && property.PropertyType.IsGenericType
                        && property.PropertyType.GetGenericArguments().Contains(contract)))
                .Where(property => property.GetCustomAttribute<NotMappedAttribute>() is null)
                .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
                .ToList();

            Assert.Empty(mappedLeafMembers);
        }

        // The five DbSets are GONE from TenantDBContext, which is the compiler proof that no caller bypasses
        // the seam back to the context. Asserted reflectively because the compiler cannot fail a build over a
        // DbSet that someone ADDS BACK.
        [Fact]
        public void The_tenant_context_declares_no_dbset_for_a_migrated_leaf_cluster()
        {
            var declared = typeof(TenantDBContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.PropertyType.GetGenericArguments()[0])
                .ToList();

            Assert.NotEmpty(declared);
            Assert.DoesNotContain(typeof(Language), declared);
            Assert.DoesNotContain(typeof(Log), declared);
            Assert.DoesNotContain(typeof(Profile), declared);
            Assert.DoesNotContain(typeof(SiteTask), declared);
            Assert.DoesNotContain(typeof(Notification), declared);
        }

        // Tenant-side only, like Permission (D24) and unlike Setting (D23). Mapping any of them on the master
        // context would invent an entity type over a table the master migration set does not create.
        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void The_master_context_does_not_map_the_leaf_aggregate(Type contract, Type aggregate, string table)
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

    /// <summary>
    /// The behaviour every leaf cluster's store shares, exercised against a real database whose schema is
    /// written by hand to match the tenant migration set. Subclasses supply the DDL, the aggregate, and the
    /// cluster's own discriminating tests.
    /// </summary>
    /// <remarks>
    /// The D19 pair below is the reason this base exists rather than five copies: both tests are about the
    /// SEAM's contract, not about any one cluster, and all five repositories have the read-then-write shape
    /// that walks into it (LanguageRepository.UpdateLanguage un-defaults siblings, ProfileController.Put and
    /// NotificationController.Put read to authorize then write, SiteTaskJob reads the queue then completes
    /// each task). Everything cluster-SPECIFIC stays in the subclass, spelled out.
    /// </remarks>
    public abstract class LeafClusterStoreBehaviourTests<TAggregate> : IDisposable
        where TAggregate : AggregateRoot<int>
    {
        private readonly SqliteConnection _connection;
        protected readonly IRelationalDatabase Database = new SqliteDatabase();

        protected abstract string TableDdl { get; }

        /// <summary>A complete, insertable aggregate — every NOT NULL column populated.</summary>
        protected abstract TAggregate NewAggregate();

        /// <summary>
        /// Rebuilds a DETACHED aggregate carrying <paramref name="id"/> and a CHANGED persisted value, as the
        /// repository's mapper does for an update. The change is what makes the D19 pair below evidence rather
        /// than ceremony: an update that persists nothing still leaves the row present, so a test that only
        /// asserts the row exists proves nothing at all.
        /// </summary>
        protected abstract TAggregate RebuildMutated(int id);

        /// <summary>Asserts the value <see cref="RebuildMutated"/> changed is the one now in the database.</summary>
        protected abstract void AssertMutated(TAggregate loaded);

        protected LeafClusterStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = TableDdl;
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

        protected DbContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, Database);
        }

        protected IRepository<T, int> NewStore<T>(DbContext context) where T : AggregateRoot<int> =>
            new EfRepository<T, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        protected IRepository<TAggregate, int> NewStore(DbContext context) => NewStore<TAggregate>(context);

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_aggregate_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();

            await NewStore(context).AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            Assert.NotNull(await NewStore(readContext).GetByIdAsync(aggregate.Id));
        }

        [Fact]
        public async Task A_deleted_aggregate_is_gone()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var deleteContext = NewContext();
            var deleteStore = NewStore(deleteContext);
            await deleteStore.DeleteAsync((await deleteStore.GetByIdAsync(aggregate.Id))!);

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(aggregate.Id));
        }

        // The purge loops delete aggregates read through a SESSION (a different context), so they arrive at
        // the store DETACHED. Remove() must still delete them.
        [Fact]
        public async Task The_store_deletes_a_detached_aggregate_read_through_another_context()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var detached = readContext.Set<TAggregate>().ToList();
            Assert.NotEmpty(detached);

            using var deleteContext = NewContext();
            var deleteStore = NewStore(deleteContext);
            foreach (var item in detached)
            {
                await deleteStore.DeleteAsync(item);
            }

            using var verifyContext = NewContext();
            Assert.Empty(verifyContext.Set<TAggregate>());
        }

        // THE D19 PAIR, first half. This is the bug that made Pages' PUT return 200 and persist NOTHING, and
        // every one of these five repositories has the shape that walks into it: read, then write the same row
        // back, on ONE repository instance — one store, one context.
        [Fact]
        public async Task The_store_rejects_a_detached_update_after_a_tracked_read()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = NewAggregate();
            await store.AddAsync(aggregate);

            // A tracked read through the STORE — what the repositories' reads must never do.
            var tracked = await store.GetByIdAsync(aggregate.Id);
            Assert.NotNull(tracked);

            var rebuilt = RebuildMutated(aggregate.Id);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(rebuilt));
        }

        // THE D19 PAIR, second half: the shape the repositories actually use — read through a SESSION (its own
        // context), write through the store — persists.
        [Fact]
        public async Task A_session_read_then_a_store_write_persists()
        {
            using var seedContext = NewContext();
            var aggregate = NewAggregate();
            await NewStore(seedContext).AddAsync(aggregate);

            // The read: a separate context, as IQuerySession opens.
            using var sessionContext = NewContext();
            var read = sessionContext.Set<TAggregate>().First(item => item.Id == aggregate.Id);
            Assert.NotNull(read);

            // The write: the store's own context, tracker clean. No identity conflict.
            using var storeContext = NewContext();
            await NewStore(storeContext).UpdateAsync(RebuildMutated(aggregate.Id));

            // ...and it actually PERSISTED the change, which is the half that matters. The row surviving is not
            // evidence — a no-op update leaves it surviving too.
            using var verifyContext = NewContext();
            AssertMutated((await NewStore(verifyContext).GetByIdAsync(aggregate.Id))!);
        }

        public void Dispose() => _connection.Dispose();
    }

    public class LanguageStoreBehaviourTests : LeafClusterStoreBehaviourTests<LanguageAggregate>
    {
        // The Language table as the migrations LEAVE it: LanguageEntityBuilder.BuildTable plus
        // AddAuditableColumns, MINUS the Name column that 10000201_RemoveDeprecatedColumns drops for every
        // provider. Verified against the migrated PostgreSQL database. The foreign key is omitted because this
        // fixture exercises the Language table alone.
        protected override string TableDdl => """
            CREATE TABLE "Language" (
                "LanguageId" INTEGER NOT NULL CONSTRAINT "PK_Language" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "Code" TEXT NOT NULL,
                "IsDefault" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
            );
            """;

        protected override LanguageAggregate NewAggregate() => NewLanguage(1, "en-US", isDefault: true);

        // The mutation is IsDefault true -> false: the exact write LanguageRepository.ClearDefaultAsync makes,
        // and the one the harvested tracked-entity ForEach used to make implicitly.
        protected override LanguageAggregate RebuildMutated(int id)
        {
            var model = LanguageMapper.ToModel(NewAggregate());
            model.LanguageId = id;
            model.IsDefault = false;
            return LanguageMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(LanguageAggregate loaded) => Assert.False(loaded.IsDefault);

        private static LanguageAggregate NewLanguage(int siteId, string code, bool isDefault) =>
            LanguageMapper.ToAggregate(new Language
            {
                SiteId = siteId,
                Code = code,
                IsDefault = isDefault,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            }, assignId: false);

        // Writing an aggregate with no Name against a table with no Name column is the assertion: if the
        // aggregate ever grows one, this insert starts naming a column that does not exist and SQLite rejects
        // it. The other direction — a Name column the aggregate ignores — is what the migrations removed.
        [Fact]
        public async Task A_language_persists_against_a_schema_that_has_no_name_column()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.Equal("en-US", loaded.Code);
            Assert.True(loaded.IsDefault);
        }

        // THE CLUSTER'S REAL HAZARD, and the one the compiler cannot see. The harvested repository enforced
        // "at most one default per site" by mutating TRACKED entities in a ForEach and letting a later
        // SaveChanges pick them up. Through the seam the read comes from a session whose context tracks
        // nothing, so the identical ForEach would persist NOTHING and the site would silently end up with
        // several default languages. This proves the mechanism the repository now relies on instead: a
        // detached aggregate, mutated, written back explicitly through the store.
        [Fact]
        public async Task Un_defaulting_a_sibling_language_requires_an_explicit_store_write()
        {
            using var seedContext = NewContext();
            var existing = NewLanguage(1, "en-US", isDefault: true);
            await NewStore(seedContext).AddAsync(existing);

            // The read the repository does: a session, i.e. its own context. NOTHING here is tracked.
            using var sessionContext = NewContext();
            var detached = sessionContext.Set<LanguageAggregate>()
                .Where(item => item.SiteId == 1)
                .Where(item => item.IsDefault)
                .ToList();

            var target = Assert.Single(detached);
            target.IsDefault = false;

            // Mutating it is NOT enough — this is the harvested idiom, and against a session read it persists
            // nothing at all. Proven by reading the row back before the store write.
            using var unchangedContext = NewContext();
            Assert.True((await NewStore(unchangedContext).GetByIdAsync(existing.Id))!.IsDefault);

            // The explicit write is what actually un-defaults it.
            using var storeContext = NewContext();
            await NewStore(storeContext).UpdateAsync(target);

            using var verifyContext = NewContext();
            Assert.False((await NewStore(verifyContext).GetByIdAsync(existing.Id))!.IsDefault);
        }

        // Languages are scoped by SiteId and several sites share one table on a shared tenant database. If the
        // SiteId arm of the un-defaulting read were lost, adding a default language to one site would strip the
        // default from every other site's languages.
        [Fact]
        public async Task Languages_are_isolated_by_site()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewLanguage(1, "en-US", isDefault: true));
            await store.AddAsync(NewLanguage(2, "fr-FR", isDefault: true));

            using var readContext = NewContext();
            var siteOne = readContext.Set<LanguageAggregate>()
                .Where(item => item.SiteId == 1)
                .Where(item => item.IsDefault)
                .ToList();

            Assert.Equal("en-US", Assert.Single(siteOne).Code);
        }
    }

    public class LogStoreBehaviourTests : LeafClusterStoreBehaviourTests<LogAggregate>
    {
        // The Log table as the migrations leave it: LogEntityBuilder.BuildTable plus 10020001_AddLogInstance
        // and 10020002_AddLogRemoteIPAddress. NOTE the absence of the audit quartet — LogEntityBuilder extends
        // BaseEntityBuilder, not the Auditable one. Verified against the migrated PostgreSQL database.
        protected override string TableDdl => """
            CREATE TABLE "Log" (
                "LogId" INTEGER NOT NULL CONSTRAINT "PK_Log" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NULL,
                "LogDate" TEXT NOT NULL,
                "PageId" INTEGER NULL,
                "ModuleId" INTEGER NULL,
                "UserId" INTEGER NULL,
                "Url" TEXT NOT NULL,
                "Server" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Feature" TEXT NOT NULL,
                "Function" TEXT NOT NULL,
                "Level" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "MessageTemplate" TEXT NOT NULL,
                "Exception" TEXT NULL,
                "Properties" TEXT NULL,
                "Instance" TEXT NULL,
                "RemoteIPAddress" TEXT NULL
            );
            """;

        protected override LogAggregate NewAggregate() => NewLog(1, "Error", DateTime.UtcNow);

        // ILogRepository has no update path at all, so this mutation is hypothetical for the product — it
        // exists to exercise the SEAM's contract, which is what the D19 pair in the base is about. Level is
        // chosen because it is a filter column: if it failed to persist, the viewer's Level filter would
        // silently disagree with the row it is filtering.
        protected override LogAggregate RebuildMutated(int id)
        {
            var model = LogMapper.ToModel(NewAggregate());
            model.LogId = id;
            model.Level = "Critical";
            return LogMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(LogAggregate loaded) => Assert.Equal("Critical", loaded.Level);

        private static LogAggregate NewLog(int? siteId, string level, DateTime logDate) =>
            LogMapper.ToAggregate(new Log
            {
                SiteId = siteId,
                LogDate = logDate,
                Url = "/",
                Server = "web-01",
                Category = "Stomme.Tests",
                Feature = "Logging",
                Function = "Other",
                Level = level,
                Message = "message",
                MessageTemplate = "message"
            }, assignId: false);

        // The Log table has NO audit columns, so the aggregate must not name any. If LogAggregate were made
        // IAuditable "for consistency", this insert would name created_by/created_on/modified_by/modified_on
        // and SQLite would reject it — i.e. logging would fail on every request. This is that assertion,
        // against a real table.
        [Fact]
        public async Task A_log_entry_persists_against_a_schema_with_no_audit_columns()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();

            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("Error", loaded.Level);
            Assert.Equal("message", loaded.Message);
        }

        // The nullable columns are nullable by construction: an installation-level entry has no site, an
        // anonymous request has no user, and a successful entry has no exception. If any were mapped
        // non-nullable, these inserts would fail at runtime only.
        [Fact]
        public async Task A_log_entry_persists_with_no_site_no_user_and_no_exception()
        {
            using var context = NewContext();
            var aggregate = NewLog(null, "Information", DateTime.UtcNow);

            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Null(loaded.SiteId);
            Assert.Null(loaded.UserId);
            Assert.Null(loaded.Exception);
            Assert.Null(loaded.RemoteIPAddress);
        }

        // GetLogs' four branches, as LogRepository composes them. The Take must be applied AFTER the
        // OrderByDescending or the viewer silently stops showing the newest events — see the note there.
        [Fact]
        public async Task The_log_read_filters_by_level_and_function_and_returns_the_newest_first()
        {
            var baseDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewLog(1, "Error", baseDate));
            await store.AddAsync(NewLog(1, "Error", baseDate.AddDays(2)));
            await store.AddAsync(NewLog(1, "Information", baseDate.AddDays(3)));
            await store.AddAsync(NewLog(2, "Error", baseDate.AddDays(4))); // another site

            using var readContext = NewContext();

            // Unfiltered: this site only, newest first, capped.
            var newest = readContext.Set<LogAggregate>()
                .Where(item => item.SiteId == 1)
                .OrderByDescending(item => item.LogDate)
                .Take(1)
                .ToList();
            Assert.Equal(baseDate.AddDays(3), Assert.Single(newest).LogDate);

            // Filtered by level: the Information entry drops out, so the newest Error wins.
            var errors = readContext.Set<LogAggregate>()
                .Where(item => item.SiteId == 1 && item.Level == "Error")
                .OrderByDescending(item => item.LogDate)
                .Take(1)
                .ToList();
            Assert.Equal(baseDate.AddDays(2), Assert.Single(errors).LogDate);
        }

        // Logs are scoped by SiteId, and on a shared tenant database several sites live in one table. The
        // admin event-log viewer is per-site; losing this arm shows one site's operators another site's logs.
        [Fact]
        public async Task Logs_are_isolated_by_site()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewLog(1, "Error", DateTime.UtcNow));
            await store.AddAsync(NewLog(2, "Error", DateTime.UtcNow));

            using var readContext = NewContext();
            Assert.Single(readContext.Set<LogAggregate>().Where(item => item.SiteId == 1));
        }
    }

    public class ProfileStoreBehaviourTests : LeafClusterStoreBehaviourTests<ProfileAggregate>
    {
        // The Profile table as the migrations leave it: ProfileEntityBuilder.BuildTable plus
        // AddAuditableColumns, 02000001 (Options), 04000003 (Validation), 04000601 (Rows) and Autocomplete.
        // Note Rows is NULLABLE against a non-nullable CLR property: harvested and deliberate — 04000601
        // backfills it — and reproduced rather than tidied.
        protected override string TableDdl => """
            CREATE TABLE "Profile" (
                "ProfileId" INTEGER NOT NULL CONSTRAINT "PK_Profile" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "Category" TEXT NOT NULL,
                "ViewOrder" INTEGER NOT NULL,
                "MaxLength" INTEGER NOT NULL,
                "DefaultValue" TEXT NULL,
                "IsRequired" INTEGER NOT NULL,
                "IsPrivate" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "Options" TEXT NULL,
                "Validation" TEXT NULL,
                "Rows" INTEGER NULL,
                "Autocomplete" TEXT NULL
            );
            """;

        protected override ProfileAggregate NewAggregate() => NewProfile(1, "Address");

        // IsRequired false -> true: the write ProfileController.Put makes after reading to authorize, i.e.
        // exactly the read-then-write pair the D19 tests are about.
        protected override ProfileAggregate RebuildMutated(int id)
        {
            var model = ProfileMapper.ToModel(NewAggregate());
            model.ProfileId = id;
            model.IsRequired = true;
            return ProfileMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(ProfileAggregate loaded) => Assert.True(loaded.IsRequired);

        private static ProfileAggregate NewProfile(int? siteId, string name) =>
            ProfileMapper.ToAggregate(new Profile
            {
                SiteId = siteId,
                Name = name,
                Title = name,
                Category = "Contact",
                ViewOrder = 1,
                MaxLength = 50,
                IsRequired = false,
                IsPrivate = false,
                Rows = 1,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow
            }, assignId: false);

        // A GLOBAL profile property has a null SiteId. If SiteId were mapped non-nullable this insert would
        // fail at runtime only — and the global properties are the ones every site's profile form shows.
        [Fact]
        public async Task A_global_profile_property_persists_with_a_null_site()
        {
            using var context = NewContext();
            var aggregate = NewProfile(null, "Global");
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            Assert.Null((await NewStore(readContext).GetByIdAsync(aggregate.Id))!.SiteId);
        }

        // GetProfiles' read: this site's properties AND the global ones, never another site's. The null arm is
        // the half that fails silently — the global properties just stop appearing.
        [Fact]
        public async Task The_profile_read_returns_this_site_and_the_global_properties_only()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewProfile(null, "Global"));
            await store.AddAsync(NewProfile(1, "Mine"));
            await store.AddAsync(NewProfile(2, "Intruder"));

            using var readContext = NewContext();
            var names = readContext.Set<ProfileAggregate>()
                .Where(item => item.SiteId == 1 || item.SiteId == null)
                .ToList()
                .ConvertAll(item => item.Name);

            Assert.Contains("Global", names);
            Assert.Contains("Mine", names);
            Assert.DoesNotContain("Intruder", names);
        }
    }

    public class SiteTaskStoreBehaviourTests : LeafClusterStoreBehaviourTests<SiteTaskAggregate>
    {
        // The SiteTask table as the migrations leave it: SiteTaskEntityBuilder.BuildTable plus
        // AddAuditableColumns. Note IsCompleted is NULLABLE against a non-nullable CLR property: harvested and
        // deliberate, reproduced rather than tidied.
        protected override string TableDdl => """
            CREATE TABLE "SiteTask" (
                "SiteTaskId" INTEGER NOT NULL CONSTRAINT "PK_SiteTask" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "Parameters" TEXT NULL,
                "IsCompleted" INTEGER NULL,
                "Status" TEXT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL
            );
            """;

        protected override SiteTaskAggregate NewAggregate() => NewSiteTask(1, "Reindex", isCompleted: false, DateTime.UtcNow);

        // IsCompleted false -> true: THE write SiteTaskJob makes, and the one that must persist or the job
        // re-runs the same task forever. It is also the exact read-then-write shape D19 is about — the job
        // reads the pending queue, runs each task, then writes it back completed.
        protected override SiteTaskAggregate RebuildMutated(int id)
        {
            var model = SiteTaskMapper.ToModel(NewAggregate());
            model.SiteTaskId = id;
            model.IsCompleted = true;
            return SiteTaskMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(SiteTaskAggregate loaded) => Assert.True(loaded.IsCompleted);

        private static SiteTaskAggregate NewSiteTask(int siteId, string name, bool isCompleted, DateTime createdOn) =>
            SiteTaskMapper.ToAggregate(new SiteTask
            {
                SiteId = siteId,
                Name = name,
                Type = "Stomme.Infrastructure.SomeTask, Stomme.Server",
                IsCompleted = isCompleted,
                CreatedBy = "installer",
                CreatedOn = createdOn,
                ModifiedBy = "installer",
                ModifiedOn = createdOn
            }, assignId: false);

        // The work queue and the purge read the SAME column in OPPOSITE directions: GetSiteTasks wants
        // !IsCompleted, DeleteSiteTasks wants IsCompleted. Invert either and the job re-runs finished work
        // while the purge deletes pending work — and Type is a type name the job reflects over and executes.
        [Fact]
        public async Task The_queue_read_and_the_purge_read_select_opposite_halves_of_iscompleted()
        {
            var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewSiteTask(1, "pending", isCompleted: false, old));
            await store.AddAsync(NewSiteTask(1, "done", isCompleted: true, old));

            using var readContext = NewContext();

            var queue = readContext.Set<SiteTaskAggregate>()
                .Where(item => item.SiteId == 1 && !item.IsCompleted)
                .ToList();
            Assert.Equal("pending", Assert.Single(queue).Name);

            var purgeable = readContext.Set<SiteTaskAggregate>()
                .Where(item => item.SiteId == 1 && item.IsCompleted && item.CreatedOn < old.AddDays(1))
                .ToList();
            Assert.Equal("done", Assert.Single(purgeable).Name);
        }

        // The queue is ordered oldest-first, and CreatedOn is both the audit stamp and the sort key. Added
        // newest-first here so insertion order cannot make this pass.
        [Fact]
        public async Task The_queue_is_ordered_oldest_first()
        {
            using var context = NewContext();
            var store = NewStore(context);
            await store.AddAsync(NewSiteTask(1, "second", false, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            await store.AddAsync(NewSiteTask(1, "first", false, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

            using var readContext = NewContext();
            var names = readContext.Set<SiteTaskAggregate>()
                .Where(item => item.SiteId == 1 && !item.IsCompleted)
                .OrderBy(item => item.CreatedOn)
                .ToList()
                .ConvertAll(item => item.Name);

            Assert.Equal(["first", "second"], names);
        }
    }

    public class NotificationStoreBehaviourTests : LeafClusterStoreBehaviourTests<NotificationAggregate>
    {
        // The Notification table as the migrations leave it: NotificationEntityBuilder.BuildTable plus
        // AddDeletableColumns, 01000100 (FromDisplayName/FromEmail/ToDisplayName), 01000101 (SendOn) and
        // 04000101 (IsRead). NOTE there are no audit columns — the builder extends DeletableBaseEntityBuilder,
        // not the Auditable one. Verified against the migrated PostgreSQL database.
        protected override string TableDdl => """
            CREATE TABLE "Notification" (
                "NotificationId" INTEGER NOT NULL CONSTRAINT "PK_Notification" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "FromUserId" INTEGER NULL,
                "ToUserId" INTEGER NULL,
                "ToEmail" TEXT NOT NULL,
                "ParentId" INTEGER NULL,
                "Subject" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "IsDelivered" INTEGER NOT NULL,
                "DeliveredOn" TEXT NULL,
                "DeletedBy" TEXT NULL,
                "DeletedOn" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "FromDisplayName" TEXT NULL,
                "FromEmail" TEXT NULL,
                "ToDisplayName" TEXT NULL,
                "SendOn" TEXT NULL,
                "IsRead" INTEGER NULL
            );
            """;

        protected override NotificationAggregate NewAggregate() => NewNotification(1, "subject");

        // IsDelivered false -> true: THE write NotificationJob makes after sending, and the one that must
        // persist or the notification is delivered again on every run. Read-then-write, i.e. the D19 shape.
        protected override NotificationAggregate RebuildMutated(int id)
        {
            var model = NotificationMapper.ToModel(NewAggregate());
            model.NotificationId = id;
            model.IsDelivered = true;
            model.DeliveredOn = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc);
            return NotificationMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(NotificationAggregate loaded)
        {
            Assert.True(loaded.IsDelivered);
            Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), loaded.DeliveredOn);
        }

        private static NotificationAggregate NewNotification(int siteId, string subject) =>
            NotificationMapper.ToAggregate(new Notification
            {
                SiteId = siteId,
                ToEmail = "someone@example.com",
                Subject = subject,
                Body = "body",
                CreatedOn = DateTime.UtcNow,
                IsDelivered = false,
                IsDeleted = false,
                IsRead = false
            }, assignId: false);

        // The Notification table has no audit columns, so the aggregate must not name any — the mirror of the
        // Log assertion. If NotificationAggregate were made IAuditable, this insert would name four columns
        // that do not exist.
        [Fact]
        public async Task A_notification_persists_against_a_schema_with_no_audit_columns()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("subject", loaded.Subject);
            Assert.False(loaded.IsDeleted);
        }

        // The soft-delete columns ARE real columns on this table and must round-trip through a real database.
        // If IsDeleted failed to persist, a notification the user deleted would be delivered anyway; if
        // DeletedBy/DeletedOn failed, the audit trail would be gone. (The STAMPING of DeletedBy/DeletedOn is
        // DbContextUtils' job and lives on TenantDBContext, which this fixture deliberately does not use — see
        // the aggregate's remarks. This proves the columns are mapped and writable.)
        [Fact]
        public async Task A_soft_deleted_notification_round_trips_its_deletable_columns()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            var model = NotificationMapper.ToModel(aggregate);
            model.IsDeleted = true;
            model.DeletedBy = "host";
            model.DeletedOn = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);

            using var writeContext = NewContext();
            await NewStore(writeContext).UpdateAsync(NotificationMapper.ToAggregate(model, assignId: true));

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.True(loaded.IsDeleted);
            Assert.Equal("host", loaded.DeletedBy);
            Assert.Equal(new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), loaded.DeletedOn);
        }

        // The delivery queue's predicate against a real database. Each excluded row fails by being SENT: a
        // delivered one twice, a deleted one after the user deleted it, a future one early. The SendOn == null
        // row is the opposite failure — never sent at all.
        [Fact]
        public async Task The_delivery_queue_excludes_delivered_deleted_and_future_notifications()
        {
            var now = DateTime.UtcNow;
            using var context = NewContext();
            var store = NewStore(context);

            var due = NewNotification(1, "due");
            due.SendOn = now.AddHours(-1);
            await store.AddAsync(due);

            var unscheduled = NewNotification(1, "unscheduled");
            unscheduled.SendOn = null;
            await store.AddAsync(unscheduled);

            var delivered = NewNotification(1, "delivered");
            delivered.IsDelivered = true;
            await store.AddAsync(delivered);

            var deleted = NewNotification(1, "deleted");
            deleted.IsDeleted = true;
            await store.AddAsync(deleted);

            var future = NewNotification(1, "future");
            future.SendOn = now.AddDays(1);
            await store.AddAsync(future);

            using var readContext = NewContext();
            var subjects = readContext.Set<NotificationAggregate>()
                .Where(item => item.SiteId == 1)
                .Where(item => item.IsDelivered == false && item.IsDeleted == false)
                .Where(item => item.SendOn == null || item.SendOn < DateTime.UtcNow)
                .ToList()
                .ConvertAll(item => item.Subject);

            Assert.Contains("due", subjects);
            Assert.Contains("unscheduled", subjects);
            Assert.DoesNotContain("delivered", subjects);
            Assert.DoesNotContain("deleted", subjects);
            Assert.DoesNotContain("future", subjects);
        }

        // The purge predicate's PRECEDENCE, against a real database. && binds tighter than ||, so this purges
        // SYSTEM notifications that are either soft-deleted OR delivered-and-old. Lose the FromUserId arm and
        // the nightly job deletes user-to-user notifications.
        [Fact]
        public async Task The_purge_predicate_spares_user_notifications_and_recent_deliveries()
        {
            var purgedate = DateTime.UtcNow.AddDays(-30);
            using var context = NewContext();
            var store = NewStore(context);

            var systemDeleted = NewNotification(1, "system-deleted");
            systemDeleted.IsDeleted = true;
            await store.AddAsync(systemDeleted);

            var oldDelivered = NewNotification(1, "system-old-delivered");
            oldDelivered.IsDelivered = true;
            oldDelivered.DeliveredOn = purgedate.AddDays(-1);
            await store.AddAsync(oldDelivered);

            var recentDelivered = NewNotification(1, "system-recent-delivered");
            recentDelivered.IsDelivered = true;
            recentDelivered.DeliveredOn = DateTime.UtcNow;
            await store.AddAsync(recentDelivered);

            var fromUser = NewNotification(1, "user-to-user");
            fromUser.FromUserId = 42;
            fromUser.IsDeleted = true;
            await store.AddAsync(fromUser);

            using var readContext = NewContext();
            var subjects = readContext.Set<NotificationAggregate>()
                .Where(item => item.SiteId == 1 && item.FromUserId == null
                    && (item.IsDeleted || item.IsDelivered && item.DeliveredOn < purgedate))
                .ToList()
                .ConvertAll(item => item.Subject);

            Assert.Contains("system-deleted", subjects);
            Assert.Contains("system-old-delivered", subjects);
            Assert.DoesNotContain("system-recent-delivered", subjects);
            Assert.DoesNotContain("user-to-user", subjects);
        }
    }
}
