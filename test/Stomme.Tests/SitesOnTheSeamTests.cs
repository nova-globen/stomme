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
    // The Sites cluster on the persistence seam (D3/D7/D17). Two things here are easy to get wrong and
    // invisible at a glance:
    //   1. The aggregate is mapped onto a schema this context does NOT migrate — the tenant migration set
    //      owns those columns. Nothing but a live install tells you the mapping drifted, and by then the
    //      installer has already half-written a database. These tests pin the mapping to the harvested
    //      column names instead.
    //   2. `HasColumnName` outranks EFCore.NamingConventions' snake_case rewriter, so an explicitly-named
    //      column is NOT rewritten. Naming Id's column "SiteId" would look right and produce `"SiteId"` on
    //      PostgreSQL, where the real column is `site_id`. That is why the mapping runs every explicit name
    //      through IDatabase.RewriteName, and why the PostgreSQL case below is asserted separately.
    public class SiteAggregateMappingTests
    {
        // Builds the model exactly the way TenantDBContext does: the provider's own UseDatabase (which is
        // what applies UseSnakeCaseNamingConvention on PostgreSQL) plus ConfigureFrameworkStore.
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildSiteEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(SiteAggregate))!;
        }

        private static Dictionary<string, string> ColumnsByProperty(IEntityType entityType) =>
            entityType.GetProperties().ToDictionary(p => p.Name, p => p.GetColumnName());

        // PostgreSQL is the verification provider, and the one where an un-rewritten explicit column name
        // would actually break (its identifiers are case-sensitive: "SiteId" != site_id).
        [Fact]
        public void The_aggregate_maps_to_the_snake_case_columns_the_migrations_created_on_postgresql()
        {
            var entityType = BuildSiteEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("site", entityType.GetTableName());
            // The two explicitly-named columns — the ones that would silently keep their PascalCase.
            Assert.Equal("site_id", columns[nameof(SiteAggregate.Id)]);
            Assert.Equal("version", columns[nameof(SiteAggregate.SiteVersion)]);
            // A convention-named column, to prove the rewriter is still doing its job around the explicit ones.
            Assert.Equal("default_theme_type", columns[nameof(SiteAggregate.DefaultThemeType)]);
            Assert.Equal("enhanced_navigation", columns[nameof(SiteAggregate.EnhancedNavigation)]);
        }

        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe")]
        public void The_aggregate_maps_to_the_pascal_case_columns_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildSiteEntityType(database, connectionString);
            var columns = ColumnsByProperty(entityType);

            Assert.Equal("Site", entityType.GetTableName());
            Assert.Equal("SiteId", columns[nameof(SiteAggregate.Id)]);
            Assert.Equal("Version", columns[nameof(SiteAggregate.SiteVersion)]);
            Assert.Equal("DefaultThemeType", columns[nameof(SiteAggregate.DefaultThemeType)]);
        }

        // AggregateRoot<TId>.Version is the seam's optimistic-concurrency token, and the harvested Site table
        // has no column for it. If it ever gets mapped, it lands on the `Version` column — which holds the
        // ISiteMigration configuration version, a string — and every read of the site blows up.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_the_harvested_schema_has_no_column_for_it()
        {
            var entityType = BuildSiteEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

            Assert.Null(entityType.FindProperty(nameof(AggregateRoot<int>.Version)));
            Assert.Null(entityType.FindProperty(nameof(IHasDomainEvents.DomainEvents)));
            Assert.DoesNotContain(entityType.GetProperties(), p => p.GetColumnName() == "aggregate_version");
        }

        // The tripwire for the next agent. Site (the wire contract) and SiteAggregate (the persisted model)
        // are two hand-maintained lists of the same columns. Add a column to Site, forget the aggregate, and
        // the install still boots — the value just never persists. This fails instead.
        [Fact]
        public void The_aggregate_persists_exactly_the_columns_the_harvested_entity_did()
        {
            var persistedOnTheContract = typeof(Site).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
                .Select(p => p.Name)
                // SiteId is the aggregate's Id; Version is its SiteVersion (AggregateRoot owns the name).
                .Select(name => name switch
                {
                    nameof(Site.SiteId) => nameof(SiteAggregate.Id),
                    nameof(Site.Version) => nameof(SiteAggregate.SiteVersion),
                    _ => name
                })
                .OrderBy(name => name)
                .ToList();

            var entityType = BuildSiteEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");
            var persistedOnTheAggregate = entityType.GetProperties().Select(p => p.Name).OrderBy(name => name).ToList();

            Assert.Equal(persistedOnTheContract, persistedOnTheAggregate);
        }

        // The mapper is the other half of the same hazard: a column can be mapped and still never be copied.
        [Fact]
        public void Every_persisted_value_survives_a_round_trip_through_the_mapper()
        {
            var original = new Site
            {
                SiteId = 7,
                Name = "Contoso",
                TimeZoneId = "Pacific Standard Time",
                CultureCode = "sv-SE",
                LogoFileId = 11,
                FaviconFileId = 12,
                DefaultThemeType = "Theme, Asm",
                DefaultContainerType = "Container, Asm",
                AdminContainerType = "AdminContainer, Asm",
                AdminThemeType = "AdminTheme, Asm",
                PwaIsEnabled = true,
                PwaAppIconFileId = 13,
                PwaSplashIconFileId = 14,
                AllowRegistration = true,
                VisitorTracking = true,
                CaptureBrokenUrls = true,
                SiteGuid = Guid.NewGuid().ToString(),
                RenderMode = "Static",
                Runtime = "Server",
                Prerender = true,
                Hybrid = true,
                EnhancedNavigation = true,
                Version = "1.2.3",
                HeadContent = "<meta />",
                BodyContent = "<script></script>",
                IsDeleted = true,
                DeletedBy = "someone",
                DeletedOn = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                CreatedBy = "creator",
                CreatedOn = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ModifiedBy = "modifier",
                ModifiedOn = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc)
            };

            var roundTripped = SiteMapper.ToModel(SiteMapper.ToAggregate(original, assignId: true));

            // Compare every persisted member reflectively, so a new column cannot be quietly left out.
            foreach (var property in typeof(Site).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetCustomAttribute<NotMappedAttribute>() is null)
                         .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null))
            {
                Assert.Equal(property.GetValue(original), property.GetValue(roundTripped));
            }
        }

        [Fact]
        public void An_insert_leaves_the_identity_to_the_database()
        {
            var aggregate = SiteMapper.ToAggregate(new Site { SiteId = 99, Name = "New" }, assignId: false);

            Assert.Equal(0, aggregate.Id);
        }
    }

    // The behaviour of the migrated repository's store, exercised against a real database whose schema is
    // written by hand to match the tenant migration set — NOT by EnsureCreated, which would build the schema
    // from the very model under test and could only ever agree with itself.
    public class SiteStoreBehaviourTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Site table as the tenant migrations leave it (SiteEntityBuilder + every later AddColumn),
        // in the harvested PascalCase the SQLite provider does not rewrite.
        private const string SiteTableDdl = """
            CREATE TABLE "Site" (
                "SiteId" INTEGER NOT NULL CONSTRAINT "PK_Site" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "LogoFileId" INTEGER NULL,
                "FaviconFileId" INTEGER NULL,
                "DefaultThemeType" TEXT NOT NULL,
                "DefaultContainerType" TEXT NOT NULL,
                "PwaIsEnabled" INTEGER NOT NULL,
                "PwaAppIconFileId" INTEGER NULL,
                "PwaSplashIconFileId" INTEGER NULL,
                "AllowRegistration" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "DeletedBy" TEXT NULL,
                "DeletedOn" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "AdminContainerType" TEXT NULL,
                "AdminThemeType" TEXT NULL,
                "SiteGuid" TEXT NULL,
                "Runtime" TEXT NULL,
                "RenderMode" TEXT NULL,
                "VisitorTracking" INTEGER NULL,
                "CaptureBrokenUrls" INTEGER NULL,
                "Version" TEXT NULL,
                "HeadContent" TEXT NULL,
                "BodyContent" TEXT NULL,
                "Prerender" INTEGER NULL,
                "Hybrid" INTEGER NULL,
                "TimeZoneId" TEXT NULL,
                "EnhancedNavigation" INTEGER NULL,
                "CultureCode" TEXT NULL
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

        public SiteStoreBehaviourTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = SiteTableDdl;
            command.ExecuteNonQuery();
        }

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IRepository<SiteAggregate, int> NewStore(DbContext context) =>
            new EfRepository<SiteAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        private static Site NewSite(string name) => new()
        {
            Name = name,
            DefaultThemeType = "Theme, Asm",
            DefaultContainerType = "Container, Asm",
            SiteGuid = Guid.NewGuid().ToString(),
            Version = "1.0.0",
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        // The whole point: the aggregate reads and writes the columns the migrations actually created. If any
        // explicit name drifted, SQLite would reject the statement rather than quietly pass.
        [Fact]
        public async Task An_added_site_round_trips_through_the_store_against_the_migrated_schema()
        {
            using var context = NewContext();
            var store = NewStore(context);
            var aggregate = SiteMapper.ToAggregate(NewSite("Default Site"), assignId: false);

            await store.AddAsync(aggregate);

            Assert.True(aggregate.Id > 0); // the database assigned the identity

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;
            Assert.Equal("Default Site", loaded.Name);
            Assert.Equal("1.0.0", loaded.SiteVersion); // the string Version column, not the seam's token
        }

        // Reads go through IQuerySession.Query<T> (D7: the aggregate repository has no IQueryable). This is
        // the exact call SiteRepository.GetSites makes, ordering in the database.
        [Fact]
        public async Task Sites_are_read_and_ordered_through_a_query_session()
        {
            using (var seed = NewContext())
            {
                var store = NewStore(seed);
                await store.AddAsync(SiteMapper.ToAggregate(NewSite("Zebra"), assignId: false));
                await store.AddAsync(SiteMapper.ToAggregate(NewSite("Alpha"), assignId: false));
            }

            var sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>
            {
                [StoreNames.Main] = NewContext
            });

            await using var session = sessions.OpenQuerySession();
            var names = session.Query<SiteAggregate>().OrderBy(item => item.Name).ToList().ConvertAll(item => item.Name);

            Assert.Equal(new[] { "Alpha", "Zebra" }, names);
        }

        [Fact]
        public async Task An_updated_site_persists_through_the_store()
        {
            int siteId;
            using (var seed = NewContext())
            {
                var aggregate = SiteMapper.ToAggregate(NewSite("Before"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                siteId = aggregate.Id;
            }

            // SiteRepository updates a DETACHED aggregate rebuilt from the wire contract — the same shape the
            // harvested repository used (db.Entry(site).State = Modified), so every column is written.
            using (var context = NewContext())
            {
                var site = NewSite("After");
                site.SiteId = siteId;
                site.HeadContent = "<meta name=\"probe\" />";
                await NewStore(context).UpdateAsync(SiteMapper.ToAggregate(site, assignId: true));
            }

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(siteId))!;
            Assert.Equal("After", loaded.Name);
            Assert.Equal("<meta name=\"probe\" />", loaded.HeadContent);
        }

        [Fact]
        public async Task A_deleted_site_is_removed_through_the_store()
        {
            int siteId;
            using (var seed = NewContext())
            {
                var aggregate = SiteMapper.ToAggregate(NewSite("Doomed"), assignId: false);
                await NewStore(seed).AddAsync(aggregate);
                siteId = aggregate.Id;
            }

            using (var context = NewContext())
            {
                var store = NewStore(context);
                var aggregate = (await store.GetByIdAsync(siteId))!;
                await store.DeleteAsync(aggregate);
            }

            using var readContext = NewContext();
            Assert.Null(await NewStore(readContext).GetByIdAsync(siteId));
        }

        public void Dispose() => _connection.Dispose();
    }
}
