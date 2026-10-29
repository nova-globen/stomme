using System;
using System.Collections.Generic;
using System.Linq;
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
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    // Visitor, UrlMapping and PageModule on the persistence seam.
    //
    // WHAT MAKES THESE THREE A GROUP: not one of them needed a navigation severed FOR it. An earlier round had
    // already cut Visitor.User; another cut PageModule.Page and PageModule.Module while deliberately
    // leaving the PageModule ROW in TenantDBContext; UrlMapping never had a navigation at all. So where an earlier
    // pair was indivisible because a live navigation joined them, this increment is three independent ROW
    // MOVES. The [NotMapped] attributes those earlier decisions added are exactly what made it that cheap.
    //
    // That did NOT make the move free, and the same blocker was observed here too — from an angle no previous
    // cluster hit. Mapping PageModuleAggregate broke FIVE OTHER CLUSTERS' test fixtures at once:
    //
    //   "The table 'page_module' cannot be used for entity type 'PageModuleAggregate' since it is being used
    //    for entity type 'PageModule' and potentially other entity types, but there is no linking
    //    relationship."
    //
    // ...because every one of those fixtures declared DbSet<PageModule> as its stand-in "unmigrated
    // neighbour". The contract does not have to be in TenantDBContext to collide — it only has to be in SOME
    // context that also calls ConfigureFrameworkStore. Those fixtures now use Folder/File, which are still
    // genuinely unmigrated, and their PageModule assertions were retargeted onto the aggregate.
    //
    // ALL THREE TABLES WERE READ OFF THE MIGRATED PostgreSQL DATABASE (stomme-pg-44357, host port 45432),
    // never off their EntityBuilders. That is the standing lesson and it paid off for all three at once —
    // every one of the three builders is wrong:
    //
    //   * VisitorEntityBuilder omits Referrer and Url entirely. 03000105_AddVisitorReferrer adds BOTH (despite
    //     its name), and 03010001 then widens Url alone to 2048.
    //   * UrlMappingEntityBuilder omits Referrer entirely (10000301_FixUrlMappingReferrer adds it; its
    //     predecessor 10000203 is an empty stub) and is wrong about two widths: Url is 750, not 500, and
    //     MappedUrl is 2048, not 500. The UNIQUE index on (site_id, url) is likewise the migration's, not the
    //     builder's — the standing lesson, repeating.
    //   * PageModuleEntityBuilder omits EffectiveDate, ExpiryDate, Header and Footer.
    //
    // Every DDL below is the table as the migration set leaves it, written BY HAND — never EnsureCreated,
    // which would build the schema from the very model under test and could only ever agree with itself.

    /// <summary>
    /// What the three aggregates map to. Same hazard as every cluster before them: an explicit HasColumnName
    /// OUTRANKS the snake_case rewriter, so an un-rewritten explicit name is a column that does not exist on
    /// the one provider whose identifiers are case-sensitive.
    /// </summary>
    public class VisitorUrlMappingAndPageModuleMappingTests
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

        /// <summary>The three, their tables, and the ONE explicitly-named column each.</summary>
        public static TheoryData<Type, string, string> Clusters() => new()
        {
            { typeof(VisitorAggregate), "visitor", "visitor_id" },
            { typeof(UrlMappingAggregate), "url_mapping", "url_mapping_id" },
            { typeof(PageModuleAggregate), "page_module", "page_module_id" }
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

        [Theory]
        [MemberData(nameof(Clusters))]
        public void No_aggregate_assigns_its_own_identity_because_the_database_does(
            Type aggregate, string table, string keyColumn)
        {
            _ = table;
            _ = keyColumn;
            var key = BuildOnPostgreSql(aggregate).FindProperty(nameof(AggregateRoot<int>.Id))!;

            Assert.Equal(ValueGenerated.OnAdd, key.ValueGenerated);
        }

        // The providers without a rewriter must keep the PascalCase names their migrations created.
        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregates_map_to_pascal_case_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;

            Assert.Equal("Visitor", BuildEntityType(typeof(VisitorAggregate), database, connectionString).GetTableName());
            Assert.Equal("UrlMapping", BuildEntityType(typeof(UrlMappingAggregate), database, connectionString).GetTableName());
            Assert.Equal("PageModule", BuildEntityType(typeof(PageModuleAggregate), database, connectionString).GetTableName());

            Assert.Equal(
                "PageModuleId",
                ColumnsByProperty(BuildEntityType(typeof(PageModuleAggregate), database, connectionString))[nameof(AggregateRoot<int>.Id)]);
        }

        // THE ELEVEN COLUMNS THE LIVE DATABASE ACTUALLY HAS. VisitorEntityBuilder builds only NINE — it omits
        // referrer and url, which 03000105_AddVisitorReferrer adds. Trusting the builder would have produced an
        // aggregate that silently dropped the last URL and referrer of every visitor.
        [Fact]
        public void The_visitor_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildOnPostgreSql(typeof(VisitorAggregate));
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("site_id", columns[nameof(VisitorAggregate.SiteId)]);
            Assert.Equal("user_id", columns[nameof(VisitorAggregate.UserId)]);
            Assert.Equal("visits", columns[nameof(VisitorAggregate.Visits)]);
            Assert.Equal("ip_address", columns[nameof(VisitorAggregate.IPAddress)]);
            Assert.Equal("user_agent", columns[nameof(VisitorAggregate.UserAgent)]);
            Assert.Equal("language", columns[nameof(VisitorAggregate.Language)]);
            Assert.Equal("url", columns[nameof(VisitorAggregate.Url)]);
            Assert.Equal("referrer", columns[nameof(VisitorAggregate.Referrer)]);
            Assert.Equal("created_on", columns[nameof(VisitorAggregate.CreatedOn)]);
            Assert.Equal("visited_on", columns[nameof(VisitorAggregate.VisitedOn)]);

            Assert.Equal(
                ["created_on", "ip_address", "language", "referrer", "site_id", "url", "user_agent", "user_id", "visited_on", "visitor_id", "visits"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        // THE NINE COLUMNS THE LIVE DATABASE ACTUALLY HAS. UrlMappingEntityBuilder builds only SEVEN
        // — it omits referrer (which 10000301_FixUrlMappingReferrer adds) and knows nothing of url_normalized
        // (which 10020004_AddUrlMappingNormalizedColumn adds, the case-insensitive lookup key). The aggregate
        // maps url_normalized by convention (public getter, private setter derived from Url's setter), exactly
        // as FileAggregate maps name_normalized.
        [Fact]
        public void The_urlmapping_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildOnPostgreSql(typeof(UrlMappingAggregate));
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("site_id", columns[nameof(UrlMappingAggregate.SiteId)]);
            Assert.Equal("url", columns[nameof(UrlMappingAggregate.Url)]);
            Assert.Equal("url_normalized", columns[nameof(UrlMappingAggregate.UrlNormalized)]);
            Assert.Equal("mapped_url", columns[nameof(UrlMappingAggregate.MappedUrl)]);
            Assert.Equal("requests", columns[nameof(UrlMappingAggregate.Requests)]);
            Assert.Equal("referrer", columns[nameof(UrlMappingAggregate.Referrer)]);
            Assert.Equal("created_on", columns[nameof(UrlMappingAggregate.CreatedOn)]);
            Assert.Equal("requested_on", columns[nameof(UrlMappingAggregate.RequestedOn)]);

            Assert.Equal(
                ["created_on", "mapped_url", "referrer", "requested_on", "requests", "site_id", "url", "url_mapping_id", "url_normalized"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        // THE EIGHTEEN COLUMNS THE LIVE DATABASE ACTUALLY HAS. PageModuleEntityBuilder omits effective_date,
        // expiry_date, header and footer. Note `order` — a reserved word in PostgreSQL, left to convention
        // exactly as the harvested DbSet left it, so the provider quotes it the same way it always has.
        [Fact]
        public void The_pagemodule_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildOnPostgreSql(typeof(PageModuleAggregate));
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("page_id", columns[nameof(PageModuleAggregate.PageId)]);
            Assert.Equal("module_id", columns[nameof(PageModuleAggregate.ModuleId)]);
            Assert.Equal("title", columns[nameof(PageModuleAggregate.Title)]);
            Assert.Equal("pane", columns[nameof(PageModuleAggregate.Pane)]);
            Assert.Equal("order", columns[nameof(PageModuleAggregate.Order)]);
            Assert.Equal("container_type", columns[nameof(PageModuleAggregate.ContainerType)]);
            Assert.Equal("effective_date", columns[nameof(PageModuleAggregate.EffectiveDate)]);
            Assert.Equal("expiry_date", columns[nameof(PageModuleAggregate.ExpiryDate)]);
            Assert.Equal("header", columns[nameof(PageModuleAggregate.Header)]);
            Assert.Equal("footer", columns[nameof(PageModuleAggregate.Footer)]);

            Assert.Equal(
                ["container_type", "created_by", "created_on", "deleted_by", "deleted_on", "effective_date",
                 "expiry_date", "footer", "header", "is_deleted", "modified_by", "modified_on", "module_id",
                 "order", "page_id", "page_module_id", "pane", "title"],
                entityType.GetProperties().Select(p => p.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal));
        }

        // LESSON 2, THE ONE THE COMPILER CANNOT SEE. DbContextUtils decides whether to stamp by RUNTIME TYPE
        // TEST (`item.Entity is IAuditable` / `is IDeletable`) against the entity the context tracks. Before
        // the move that entity was the PageModule CONTRACT, which implements both; now it is the aggregate. Drop
        // either interface and everything compiles, registers, renders, and silently stops stamping.
        //
        // PageModule is the FIRST aggregate on the seam to need BOTH — NotificationAggregate is IDeletable but
        // not IAuditable, and every other one is IAuditable but not IDeletable.
        //
        // Mutation-checked BOTH ways: removing IAuditable from PageModuleAggregate fails the audit half;
        // removing IDeletable fails the deletable half.
        [Fact]
        public void The_pagemodule_aggregate_is_both_auditable_and_deletable_because_its_table_has_both_column_sets()
        {
            Assert.True(typeof(IAuditable).IsAssignableFrom(typeof(PageModuleAggregate)),
                "PageModuleAggregate must be IAuditable for DbContextUtils to stamp created_by/modified_by.");
            Assert.True(AuditStamp.IsAuditable(typeof(PageModuleAggregate)),
                "PageModuleAggregate must match AuditStamp's shape for the RavenDB listener to stamp it.");
            Assert.True(typeof(IDeletable).IsAssignableFrom(typeof(PageModuleAggregate)),
                "PageModuleAggregate must be IDeletable for DbContextUtils to stamp deleted_by/deleted_on.");

            var entityType = BuildOnPostgreSql(typeof(PageModuleAggregate));
            Assert.NotNull(entityType.FindProperty(nameof(IAuditable.CreatedBy)));
            Assert.NotNull(entityType.FindProperty(nameof(IAuditable.CreatedOn)));
            Assert.NotNull(entityType.FindProperty(nameof(IAuditable.ModifiedBy)));
            Assert.NotNull(entityType.FindProperty(nameof(IAuditable.ModifiedOn)));
            Assert.NotNull(entityType.FindProperty(nameof(IDeletable.DeletedBy)));
            Assert.NotNull(entityType.FindProperty(nameof(IDeletable.DeletedOn)));
            Assert.NotNull(entityType.FindProperty(nameof(IDeletable.IsDeleted)));
        }

        // THE OTHER HALF OF LESSON 2, and the direction that is easy to get wrong by pattern-matching: Visitor
        // and UrlMapping are NEITHER. Their tables have no created_by/modified_by and no deleted_* at all —
        // VERIFIED on the live database, where visitor has eleven columns and url_mapping eight, none of them
        // audit or soft-delete. Declaring either interface would make DbContextUtils stamp a column that does
        // not exist. Their CreatedOn is their OWN column, and AuditStamp.IsAuditable correctly declines both
        // because it requires all four members.
        //
        // Mutation-checked: adding IAuditable to either aggregate fails this test.
        [Theory]
        [InlineData(typeof(VisitorAggregate))]
        [InlineData(typeof(UrlMappingAggregate))]
        public void Neither_visitor_nor_urlmapping_is_auditable_or_deletable_because_their_tables_have_no_such_columns(Type aggregate)
        {
            Assert.False(typeof(IAuditable).IsAssignableFrom(aggregate),
                $"{aggregate.Name}'s table has no audit columns — IAuditable would stamp columns that do not exist.");
            Assert.False(AuditStamp.IsAuditable(aggregate),
                $"{aggregate.Name} must not match AuditStamp's shape, or the RavenDB listener would stamp it.");
            Assert.False(typeof(IDeletable).IsAssignableFrom(aggregate),
                $"{aggregate.Name}'s table has no soft-delete columns.");

            var entityType = BuildOnPostgreSql(aggregate);
            Assert.Null(entityType.FindProperty(nameof(IAuditable.CreatedBy)));
            Assert.Null(entityType.FindProperty(nameof(IAuditable.ModifiedBy)));
            Assert.Null(entityType.FindProperty(nameof(IDeletable.IsDeleted)));

            // ...but CreatedOn IS there, as the cluster's own column. The distinction is the point: a
            // CreatedOn column is not evidence of auditability.
            Assert.NotNull(entityType.FindProperty("CreatedOn"));
        }

        // The nullable columns, pinned because each nullability is behaviour. visitor.user_id null is an
        // ANONYMOUS visitor — VisitorRepository only populates the severed User for the non-null ones, and
        // Admin > Visitors renders User.DisplayName guarded by `UserId != null`. Map it non-nullable and every
        // anonymous visit fails to materialize.
        [Fact]
        public void The_nullable_columns_stay_nullable()
        {
            var visitor = BuildOnPostgreSql(typeof(VisitorAggregate));
            Assert.True(visitor.FindProperty(nameof(VisitorAggregate.UserId))!.IsNullable);

            var pageModule = BuildOnPostgreSql(typeof(PageModuleAggregate));
            Assert.True(pageModule.FindProperty(nameof(PageModuleAggregate.EffectiveDate))!.IsNullable);
            Assert.True(pageModule.FindProperty(nameof(PageModuleAggregate.ExpiryDate))!.IsNullable);
            Assert.True(pageModule.FindProperty(nameof(PageModuleAggregate.DeletedOn))!.IsNullable);
        }
    }

    /// <summary>
    /// The mappers, and specifically what they deliberately do NOT round-trip: the view state the repositories
    /// populate from other aggregates.
    /// </summary>
    public class VisitorUrlMappingAndPageModuleMapperTests
    {
        // Visitor.User is [NotMapped] view state severed from the model — VisitorRepository.GetVisitors re-populates it
        // from the User aggregate AFTER the mapper has run. Round-tripping LOSES it by design; UserId is the
        // persisted reference. If this ever starts passing with a User attached, the navigation is back.
        [Fact]
        public void The_mapper_does_not_round_trip_the_unmapped_user()
        {
            var visitor = new Visitor
            {
                VisitorId = 12,
                SiteId = 3,
                UserId = 9,
                Visits = 2,
                IPAddress = "127.0.0.1",
                UserAgent = "agent",
                Language = "en",
                Url = "/home",
                Referrer = "https://example.com",
                CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                VisitedOn = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                User = new User { UserId = 9, DisplayName = "Someone" }
            };

            var round = VisitorMapper.ToModel(VisitorMapper.ToAggregate(visitor, assignId: true));

            Assert.Null(round.User);          // the severed navigation does not survive the mapper...
            Assert.Equal(9, round.UserId);    // ...but the id it was replaced by does.
            Assert.Equal(12, round.VisitorId);
            Assert.Equal("/home", round.Url);
            Assert.Equal("https://example.com", round.Referrer);
            Assert.Equal(2, round.Visits);
        }

        // PageModule.Page and PageModule.Module are [NotMapped] view state severed.
        [Fact]
        public void The_mapper_does_not_round_trip_the_unmapped_navigations()
        {
            var pageModule = new PageModule
            {
                PageModuleId = 5,
                PageId = 6,
                ModuleId = 7,
                Title = "Title",
                Pane = "Content",
                Order = 3,
                ContainerType = "Container",
                Header = "<b>h</b>",
                Footer = "<i>f</i>",
                CreatedBy = "installer",
                CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedBy = "installer",
                ModifiedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsDeleted = false,
                Page = new Page { PageId = 6, Name = "Home" },
                Module = new Module { ModuleId = 7 }
            };

            var round = PageModuleMapper.ToModel(PageModuleMapper.ToAggregate(pageModule, assignId: true));

            Assert.Null(round.Page);          // the severed navigations do not survive the mapper...
            Assert.Null(round.Module);
            Assert.Equal(6, round.PageId);    // ...but the ids they were replaced by do.
            Assert.Equal(7, round.ModuleId);
            Assert.Equal(5, round.PageModuleId);
            Assert.Equal(3, round.Order);
            Assert.Equal("<b>h</b>", round.Header);
            Assert.Equal("<i>f</i>", round.Footer);
        }

        // UrlMapping is the plainest contract in the framework store — no [NotMapped] members at all, so the
        // mapper is total. Pinned so that a [NotMapped] member added later is noticed rather than silently
        // dropped.
        [Fact]
        public void The_urlmapping_mapper_round_trips_every_member()
        {
            var urlMapping = new UrlMapping
            {
                UrlMappingId = 4,
                SiteId = 1,
                Url = "old/path",
                MappedUrl = "new/path",
                Requests = 11,
                Referrer = "https://example.com",
                CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                RequestedOn = new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc)
            };

            var round = UrlMappingMapper.ToModel(UrlMappingMapper.ToAggregate(urlMapping, assignId: true));

            Assert.Equal(4, round.UrlMappingId);
            Assert.Equal(1, round.SiteId);
            Assert.Equal("old/path", round.Url);
            Assert.Equal("new/path", round.MappedUrl);
            Assert.Equal(11, round.Requests);
            Assert.Equal("https://example.com", round.Referrer);
            Assert.Equal(new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc), round.RequestedOn);
        }

        // An insert must NOT carry a client-assigned identity — the database assigns it. The mapper's assignId
        // flag is what separates the two, and getting it backwards on an insert is how a blind upsert starts.
        [Fact]
        public void An_insert_does_not_assign_the_identity_and_an_update_does()
        {
            Assert.Equal(0, VisitorMapper.ToAggregate(new Visitor { VisitorId = 12 }, assignId: false).Id);
            Assert.Equal(12, VisitorMapper.ToAggregate(new Visitor { VisitorId = 12 }, assignId: true).Id);

            Assert.Equal(0, UrlMappingMapper.ToAggregate(new UrlMapping { UrlMappingId = 4 }, assignId: false).Id);
            Assert.Equal(4, UrlMappingMapper.ToAggregate(new UrlMapping { UrlMappingId = 4 }, assignId: true).Id);

            Assert.Equal(0, PageModuleMapper.ToAggregate(new PageModule { PageModuleId = 5 }, assignId: false).Id);
            Assert.Equal(5, PageModuleMapper.ToAggregate(new PageModule { PageModuleId = 5 }, assignId: true).Id);
        }

        // THE RENDER-PATH COPY-BACK. App.razor does `visitor = await AddVisitorAsync(visitor)` and reads
        // visitor.VisitorId straight into the response cookie. Lose the copy-back and every visitor gets a
        // cookie of id 0, is never correlated again, and the Visitor table grows a row per request — with no
        // exception anywhere.
        [Fact]
        public void Copying_persisted_values_back_carries_the_database_assigned_id_to_the_caller()
        {
            // The caller's model, as App.razor builds it: no id, because the database has not assigned one.
            var visitor = new Visitor { SiteId = 1 };
            // The aggregate as it comes back from the store, carrying the id the database assigned.
            var aggregate = VisitorMapper.ToAggregate(new Visitor { VisitorId = 77, SiteId = 1 }, assignId: true);

            VisitorMapper.CopyPersistedValues(aggregate, visitor);

            Assert.Equal(77, visitor.VisitorId);
        }

        // PageModule's copy-back carries the audit AND soft-delete stamps DbContextUtils applied, not just the
        // id — ModuleController returns the created instance straight to the client.
        [Fact]
        public void Copying_persisted_values_back_carries_the_stamps_the_context_applied()
        {
            // The caller's model, as ModuleController builds it.
            var pageModule = new PageModule { PageId = 1, ModuleId = 2 };
            // The aggregate as it comes back from the store: the database assigned the id and DbContextUtils
            // stamped the rest.
            var aggregate = PageModuleMapper.ToAggregate(
                new PageModule
                {
                    PageModuleId = 88,
                    PageId = 1,
                    ModuleId = 2,
                    CreatedBy = "host",
                    ModifiedBy = "host",
                    IsDeleted = true,
                    DeletedBy = "host"
                },
                assignId: true);

            PageModuleMapper.CopyPersistedValues(aggregate, pageModule);

            Assert.Equal(88, pageModule.PageModuleId);
            Assert.Equal("host", pageModule.CreatedBy);
            Assert.Equal("host", pageModule.ModifiedBy);
            Assert.True(pageModule.IsDeleted);
            Assert.Equal("host", pageModule.DeletedBy);
        }
    }

    // Deleting the DbSet is NOT enough — EF rediscovers CLR types through
    // NAVIGATIONS, and this file's own increment proved the blocker reaches beyond TenantDBContext into any
    // context that maps the contract alongside the framework store. These pin the state reflectively, so a
    // contract quietly re-mapped anywhere fails here.
    public class VisitorUrlMappingAndPageModuleMustNotBeResurrectedTests
    {
        // The neighbours that are STILL genuinely unmigrated — the shape of TenantDBContext.
        private sealed class TenantLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            // Folder, File and the SearchContent family each stood here until it moved to the seam —
            // declaring any of them now would map its table twice and break the model.
            //
            // THIS CONTEXT IS NOW EMPTY, and that is the END OF THE PATTERN rather than a gap. The neighbour
            // DbSet was never what the probe rested on: measured both ways, removing it leaves the assertions
            // green, and re-adding a migrated contract still fails them with EF's "the table '...' cannot be
            // used for entity type" — so the proof comes from ConfigureFrameworkStore plus the assertion,
            // exactly as PermissionMustNotBeResurrectedTests has shown. MigrationHistory is NOT a
            // substitute: it is [Keyless] on its own __EFMigrationsHistory table, which no aggregate maps, so
            // it could never collide and would prove nothing. The reflective sweep below is what carries the
            // protection now, and it needed no edit as those clusters moved.

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
            { typeof(Visitor), typeof(VisitorAggregate), "visitor" },
            { typeof(UrlMapping), typeof(UrlMappingAggregate), "url_mapping" },
            { typeof(PageModule), typeof(PageModuleAggregate), "page_module" }
        };

        // Mutation check: re-add `public DbSet<PageModule> PageModule { get; set; }` to the context above and
        // this fails — the model throws while building. That is not hypothetical: it is
        // exactly what five other clusters' fixtures did, and exactly how this increment's blocker surfaced.
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

        // The master context must NOT map any of these three. Unlike Setting they are tenant-side only:
        // MasterDBContext never held a DbSet for any of them and the master migration set never created the
        // tables.
        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void The_master_context_does_not_map_the_aggregate(Type contract, Type aggregate, string table)
        {
            _ = contract;
            _ = table;
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new MasterLikeContext(builder.Options, database);

            Assert.Null(context.Model.FindEntityType(aggregate));
        }

        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        // NONE of the three aggregates may carry a navigation. This is the strongest form of the guarantee:
        // the contracts relied on [NotMapped] to hide theirs, but the aggregates have no such
        // member to hide. Reflective, so a navigation added to any of them fails here without a test edit.
        [Theory]
        [MemberData(nameof(ContractAggregateAndTable))]
        public void No_aggregate_navigates_anywhere(Type contract, Type aggregate, string table)
        {
            _ = contract;
            _ = table;
            Assert.Empty(BuildModel().FindEntityType(aggregate)!.GetNavigations());
        }

        // The severed members stay [NotMapped] ON THE CONTRACT too. Belt and braces now that the contract is
        // not an entity type anywhere — but the contract is SHARED with the client, and the attribute is what
        // stops a future context mapping it back.
        [Fact]
        public void The_severed_members_stay_unmapped_on_the_wire_contracts()
        {
            AssertNotMapped(typeof(Visitor), nameof(Visitor.User));
            AssertNotMapped(typeof(PageModule), nameof(PageModule.Page));
            AssertNotMapped(typeof(PageModule), nameof(PageModule.Module));

            static void AssertNotMapped(Type contract, string member) =>
                Assert.True(
                    contract.GetProperty(member)!
                        .IsDefined(typeof(System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute), inherit: false),
                    $"{contract.Name}.{member} must stay [NotMapped].");
        }
    }

    /// <summary>The Visitor store's behaviour against a real database with the migrated schema.</summary>
    public class VisitorStoreBehaviourTests : LeafClusterStoreBehaviourTests<VisitorAggregate>
    {
        // The Visitor table exactly as the tenant migrations leave it: VisitorEntityBuilder.BuildTable, PLUS
        // the Referrer and Url columns 03000105 adds and 03010001 widens. NOT what the builder alone builds —
        // that omits both, and this DDL is the live PostgreSQL schema transcribed.
        protected override string TableDdl => VisitorDdl;

        internal const string VisitorDdl = """
            CREATE TABLE "Visitor" (
                "VisitorId" INTEGER NOT NULL CONSTRAINT "PK_Visitor" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "UserId" INTEGER NULL,
                "Visits" INTEGER NOT NULL,
                "IPAddress" TEXT NOT NULL,
                "UserAgent" TEXT NOT NULL,
                "Language" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "VisitedOn" TEXT NOT NULL,
                "Referrer" TEXT NULL,
                "Url" TEXT NULL
            );
            """;

        protected override VisitorAggregate NewAggregate() => NewVisitor(siteId: 1, ipAddress: "127.0.0.1");

        // The mutation is Visits + VisitedOn: the exact write App.razor makes on a returning visitor past its
        // cookie expiry. If that silently stopped persisting, every visitor would read as a first-time visit
        // forever and the analytics would be quietly wrong.
        protected override VisitorAggregate RebuildMutated(int id)
        {
            var model = VisitorMapper.ToModel(NewAggregate());
            model.VisitorId = id;
            model.Visits = 42;
            model.VisitedOn = new DateTime(2024, 9, 9, 9, 9, 9, DateTimeKind.Utc);
            return VisitorMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(VisitorAggregate loaded) => Assert.Equal(42, loaded.Visits);

        internal static VisitorAggregate NewVisitor(int siteId, string ipAddress) =>
            VisitorMapper.ToAggregate(new Visitor
            {
                SiteId = siteId,
                UserId = null,
                Visits = 1,
                IPAddress = ipAddress,
                UserAgent = "agent",
                Language = "en-US",
                Url = "/",
                Referrer = "",
                CreatedOn = DateTime.UtcNow,
                VisitedOn = DateTime.UtcNow
            }, assignId: false);

        // An anonymous visitor persists its null UserId. user_id is the cluster's only nullable reference and
        // VisitorRepository branches on it: only non-null ones get their User stitched on.
        [Fact]
        public async Task An_anonymous_visitor_persists_a_null_user_id()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            Assert.Null((await NewStore(readContext).GetByIdAsync(aggregate.Id))!.UserId);
        }

        // The columns VisitorEntityBuilder forgot. If the aggregate had been built from the builder rather
        // than the live database, Url and Referrer would simply be absent and every visitor's last URL would
        // vanish with no error at all.
        [Fact]
        public async Task The_url_and_referrer_the_entity_builder_omits_round_trip_anyway()
        {
            using var context = NewContext();
            var model = VisitorMapper.ToModel(NewAggregate());
            model.Url = "/some/deep/path?q=1";
            model.Referrer = "https://referrer.example";
            var aggregate = VisitorMapper.ToAggregate(model, assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("/some/deep/path?q=1", loaded.Url);
            Assert.Equal("https://referrer.example", loaded.Referrer);
        }
    }

    /// <summary>The UrlMapping store's behaviour against a real database with the migrated schema.</summary>
    public class UrlMappingStoreBehaviourTests : LeafClusterStoreBehaviourTests<UrlMappingAggregate>
    {
        // The UrlMapping table as the migrations LEAVE it — including the Referrer column the builder omits
        // and the UNIQUE index on (SiteId, Url) that 03010001 creates and the builder does not. The index is
        // here deliberately: it is the concurrency control UrlMappingRepository's try/catch depends on.
        protected override string TableDdl => UrlMappingDdl;

        internal const string UrlMappingDdl = """
            CREATE TABLE "UrlMapping" (
                "UrlMappingId" INTEGER NOT NULL CONSTRAINT "PK_UrlMapping" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "Url" TEXT NOT NULL,
                "MappedUrl" TEXT NOT NULL,
                "Requests" INTEGER NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "RequestedOn" TEXT NOT NULL,
                "Referrer" TEXT NULL,
                "UrlNormalized" TEXT NULL
            );
            CREATE UNIQUE INDEX "IX_UrlMapping" ON "UrlMapping" ("SiteId", "Url");
            """;

        protected override UrlMappingAggregate NewAggregate() => NewUrlMapping(siteId: 1, url: "broken/link", mappedUrl: "");

        // The mutation is Requests + RequestedOn: the exact write GetUrlMapping makes on every request that
        // resolves through a known URL.
        protected override UrlMappingAggregate RebuildMutated(int id)
        {
            var model = UrlMappingMapper.ToModel(NewAggregate());
            model.UrlMappingId = id;
            model.Requests = 99;
            model.RequestedOn = new DateTime(2024, 9, 9, 9, 9, 9, DateTimeKind.Utc);
            return UrlMappingMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(UrlMappingAggregate loaded) => Assert.Equal(99, loaded.Requests);

        internal static UrlMappingAggregate NewUrlMapping(int siteId, string url, string mappedUrl) =>
            UrlMappingMapper.ToAggregate(new UrlMapping
            {
                SiteId = siteId,
                Url = url,
                MappedUrl = mappedUrl,
                Requests = 1,
                Referrer = null,
                CreatedOn = DateTime.UtcNow,
                RequestedOn = DateTime.UtcNow
            }, assignId: false);

        // THE UNIQUE INDEX IS BEHAVIOUR, not decoration — and this test is why it is in the DDL above.
        // UrlMappingRepository.GetUrlMapping wraps its insert in a bare try/catch commented "ignore duplicate
        // key exception which can be caused by a race condition": the index IS the race control for two
        // simultaneous requests to the same broken URL. This proves it still raises on the relational
        // provider. It does NOT hold on RavenDB — see UrlMappingAggregate and src/Stomme.Persistence.RavenDb/README.md.
        [Fact]
        public async Task A_duplicate_site_and_url_is_rejected_by_the_unique_index_the_repository_catches()
        {
            using var context = NewContext();
            await NewStore(context).AddAsync(NewUrlMapping(siteId: 1, url: "same/url", mappedUrl: ""));

            using var duplicateContext = NewContext();
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => NewStore(duplicateContext).AddAsync(NewUrlMapping(siteId: 1, url: "same/url", mappedUrl: "")));
        }

        // ...and the same url under a DIFFERENT site is fine. The index is (SiteId, Url), not (Url) — one
        // site's broken link must not block another's.
        [Fact]
        public async Task The_same_url_under_another_site_is_not_a_duplicate()
        {
            using var context = NewContext();
            await NewStore(context).AddAsync(NewUrlMapping(siteId: 1, url: "same/url", mappedUrl: ""));

            using var otherContext = NewContext();
            var other = NewUrlMapping(siteId: 2, url: "same/url", mappedUrl: "");
            await NewStore(otherContext).AddAsync(other);

            Assert.True(other.Id > 0);
        }
    }

    /// <summary>
    /// THE REGRESSION GUARD FOR THE ONE REAL BUG THIS INCREMENT SHIPPED AND THEN FIXED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happened.</b> The first cut of <see cref="UrlMappingRepository"/> took its
    /// <see cref="IUrlMappingStore"/> by constructor injection, exactly like the eleven clusters before it.
    /// That compiled, passed 452 unit tests, passed the conformance suite on BOTH providers, and passed the
    /// PostgreSQL render guard — while <b>renaming a page threw InvalidOperationException</b>. It was found
    /// only by driving the real production host.
    /// </para>
    /// <para>
    /// <b>Why.</b> A store owns ONE <see cref="TenantDBContext"/> for its lifetime, and a controller holds ONE
    /// transient repository for a whole request. UrlMapping is the first cluster whose "read" WRITES —
    /// <c>GetUrlMappingAsync(siteId, url, referrer)</c> records the request — so
    /// <c>PageController.UpdatePage</c>'s Get-then-Update writes the SAME ROW TWICE through ONE context, and
    /// the second attach hits the identity map. The harvested code opened a fresh DbContext inside every
    /// method, so it never could.
    /// </para>
    /// <para>
    /// These tests drive the REPOSITORY (not the store) over a real SQLite database with the migrated schema,
    /// in the exact shapes production uses. Revert <see cref="UrlMappingRepository"/> to a constructor-injected
    /// store and both fail with the identity-map conflict.
    /// </para>
    /// </remarks>
    public class TheUrlMappingWriteLifetimeTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        public TheUrlMappingWriteLifetimeTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = UrlMappingStoreBehaviourTests.UrlMappingDdl;
            command.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();

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

        private sealed class TestUrlMappingStore(DbContext context) : IUrlMappingStore
        {
            private readonly IRepository<UrlMappingAggregate, int> _inner =
                new EfRepository<UrlMappingAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

            public Task<UrlMappingAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<UrlMappingAggregate>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);
            public Task AddAsync(UrlMappingAggregate aggregate, CancellationToken ct = default) => _inner.AddAsync(aggregate, ct);
            public Task UpdateAsync(UrlMappingAggregate aggregate, CancellationToken ct = default) => _inner.UpdateAsync(aggregate, ct);
            public Task DeleteAsync(UrlMappingAggregate aggregate, CancellationToken ct = default) => _inner.DeleteAsync(aggregate, ct);
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

        /// <summary>
        /// The DI graph the repository actually gets: <see cref="IUrlMappingStore"/> registered TRANSIENT, so
        /// each resolve is a fresh store over a fresh context — which is what
        /// <c>UrlMappingRepository.NewStore()</c> depends on.
        /// </summary>
        private ServiceProvider NewServices()
        {
            var services = new ServiceCollection();
            services.AddTransient<IUrlMappingStore>(_ => new TestUrlMappingStore(NewContext()));
            return services.BuildServiceProvider();
        }

        // The repository's ONE outbound edge, stubbed. Only GetSiteAsync is reachable from the code under
        // test — CaptureBrokenUrls is what gates the capture arm — so everything else throws rather than
        // returning a plausible lie.
        private sealed class StubSiteRepository(bool captureBrokenUrls) : ISiteRepository
        {
            public Task<IEnumerable<Site>> GetSitesAsync() => throw new NotSupportedException();
            public Task<Site> AddSiteAsync(Site site) => throw new NotSupportedException();
            public Task<Site> UpdateSiteAsync(Site site) => throw new NotSupportedException();
            public Task<Site> GetSiteAsync(int siteId) => Task.FromResult(new Site { SiteId = siteId, CaptureBrokenUrls = captureBrokenUrls });
            public Task<Site> GetSiteAsync(int siteId, bool tracking) => GetSiteAsync(siteId);
            public Task DeleteSiteAsync(int siteId) => throw new NotSupportedException();
            public Task<string> ProcessSiteMigrationsAsync(Alias alias, Site site) => throw new NotSupportedException();
            public Task<string> ProcessPageTemplatesAsync(Alias alias, Site site, string version) => throw new NotSupportedException();
            public Task ProcessAdminPageTemplatesAsync(Alias alias, Site site) => throw new NotSupportedException();
            public Task CreatePagesAsync(Site site, List<PageTemplate> pageTemplates, Alias alias) => throw new NotSupportedException();
        }

        private UrlMappingRepository NewRepository(ServiceProvider services, bool captureBrokenUrls = true) =>
            new(services, new TestSessionFactory(NewContext), new StubSiteRepository(captureBrokenUrls));

        /// <summary>
        /// <b>PageController.UpdatePage, byte for byte.</b> One repository instance — as a controller holds for
        /// one request — capturing a url and then redirecting it. This is the shape that threw.
        /// </summary>
        [Fact]
        public async Task A_page_rename_captures_a_url_and_then_redirects_it_through_one_repository_instance()
        {
            using var services = NewServices();
            var urlMappings = NewRepository(services);

            // PageController.cs:426 — NOT a pure read: it captures the url, which WRITES.
            var urlMapping = await urlMappings.GetUrlMappingAsync(1, "old/path");
            Assert.NotNull(urlMapping);

            // PageController.cs:429-430 — the SECOND write of the SAME row, same instance.
            urlMapping.MappedUrl = "new/path";
            await urlMappings.UpdateUrlMappingAsync(urlMapping);

            var reread = await urlMappings.GetUrlMappingAsync(urlMapping.UrlMappingId, false);
            Assert.Equal("new/path", reread.MappedUrl);
        }

        /// <summary>
        /// Two requests to the same broken url through one repository instance: the first CAPTURES (insert),
        /// the second INCREMENTS (update). The same identity-map conflict from the other direction, and the
        /// shape App.razor's 404 handler hits whenever one instance serves two lookups.
        /// </summary>
        [Fact]
        public async Task Capturing_a_url_and_then_recording_a_second_request_to_it_persists_both()
        {
            using var services = NewServices();
            var urlMappings = NewRepository(services);

            var first = await urlMappings.GetUrlMappingAsync(1, "/broken", "https://ref.example");
            Assert.Equal("broken", first.Url); // leading slash stripped
            Assert.Equal(1, first.Requests);

            var second = await urlMappings.GetUrlMappingAsync(1, "/broken", "");
            Assert.Equal(first.UrlMappingId, second.UrlMappingId);
            Assert.Equal(2, second.Requests); // the increment persisted

            var third = await urlMappings.GetUrlMappingAsync(1, "/broken", "");
            Assert.Equal(3, third.Requests);
        }
    }

    /// <summary>The PageModule store's behaviour against a real database with the migrated schema.</summary>
    public class PageModuleStoreBehaviourTests : LeafClusterStoreBehaviourTests<PageModuleAggregate>
    {
        // The PageModule table as the migrations LEAVE it: PageModuleEntityBuilder.BuildTable plus the
        // auditable and deletable column sets, PLUS EffectiveDate/ExpiryDate/Header/Footer, which the builder
        // omits and later migrations add. Transcribed from the live PostgreSQL schema. No FK to Page or Module
        // here: those tables are not in this fixture, and the aggregate references them by id only.
        protected override string TableDdl => PageModuleDdl;

        internal const string PageModuleDdl = """
            CREATE TABLE "PageModule" (
                "PageModuleId" INTEGER NOT NULL CONSTRAINT "PK_PageModule" PRIMARY KEY AUTOINCREMENT,
                "PageId" INTEGER NOT NULL,
                "ModuleId" INTEGER NOT NULL,
                "Title" TEXT NOT NULL,
                "Pane" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                "ContainerType" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "DeletedBy" TEXT NULL,
                "DeletedOn" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "EffectiveDate" TEXT NULL,
                "ExpiryDate" TEXT NULL,
                "Header" TEXT NULL,
                "Footer" TEXT NULL
            );
            """;

        protected override PageModuleAggregate NewAggregate() => NewPageModule(pageId: 1, moduleId: 1, pane: "Content", order: 1);

        // The mutation is Pane + Order: the exact write the page editor makes when a module is dragged to a
        // new pane. If that silently stopped persisting, the admin would move a module, see it move, reload,
        // and find it back where it was.
        protected override PageModuleAggregate RebuildMutated(int id)
        {
            var model = PageModuleMapper.ToModel(NewAggregate());
            model.PageModuleId = id;
            model.Pane = "Sidebar";
            model.Order = 9;
            return PageModuleMapper.ToAggregate(model, assignId: true);
        }

        protected override void AssertMutated(PageModuleAggregate loaded)
        {
            Assert.Equal("Sidebar", loaded.Pane);
            Assert.Equal(9, loaded.Order);
        }

        internal static PageModuleAggregate NewPageModule(int pageId, int moduleId, string pane, int order) =>
            PageModuleMapper.ToAggregate(new PageModule
            {
                PageId = pageId,
                ModuleId = moduleId,
                Title = "Module Title",
                Pane = pane,
                Order = order,
                ContainerType = "Stomme.Themes.Controls.Container.DefaultContainer, Stomme.Client",
                EffectiveDate = null,
                ExpiryDate = null,
                Header = null,
                Footer = null,
                CreatedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "installer",
                ModifiedOn = DateTime.UtcNow,
                IsDeleted = false
            }, assignId: false);

        // `Order` is a RESERVED WORD in every supported provider. The harvested DbSet left the column to
        // convention and so does the aggregate, so the provider quotes it exactly as it always has — but a
        // round trip is the only thing that actually proves the emitted SQL parses.
        [Fact]
        public async Task The_reserved_word_order_column_round_trips()
        {
            using var context = NewContext();
            var aggregate = NewPageModule(pageId: 1, moduleId: 1, pane: "Content", order: 7);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            Assert.Equal(7, (await NewStore(readContext).GetByIdAsync(aggregate.Id))!.Order);
        }

        // The four columns PageModuleEntityBuilder omits. Header and Footer are `text`, not varchar — the only
        // unbounded columns in this cluster.
        [Fact]
        public async Task The_columns_the_entity_builder_omits_round_trip_anyway()
        {
            using var context = NewContext();
            var model = PageModuleMapper.ToModel(NewAggregate());
            model.EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            model.ExpiryDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            model.Header = "<header>";
            model.Footer = "<footer>";
            var aggregate = PageModuleMapper.ToAggregate(model, assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), loaded.EffectiveDate);
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), loaded.ExpiryDate);
            Assert.Equal("<header>", loaded.Header);
            Assert.Equal("<footer>", loaded.Footer);
        }

        // A soft-deleted instance persists IsDeleted. PageController's page delete flips a page's modules
        // through this transition and the RecycleBin reads them back by it.
        [Fact]
        public async Task A_soft_deleted_page_module_persists_its_deleted_state()
        {
            using var context = NewContext();
            var model = PageModuleMapper.ToModel(NewAggregate());
            model.IsDeleted = true;
            model.DeletedBy = "host";
            model.DeletedOn = new DateTime(2024, 4, 4, 0, 0, 0, DateTimeKind.Utc);
            var aggregate = PageModuleMapper.ToAggregate(model, assignId: false);
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.True(loaded.IsDeleted);
            Assert.Equal("host", loaded.DeletedBy);
        }
    }
}
