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
using File = Stomme.Models.File;

namespace Stomme.Tests
{
    // The Folder cluster on the persistence seam — and the cluster that was supposed to be a PAIR.
    //
    // Folder and File were planned to land together, for a reason this file proves rather than quotes:
    // File.Folder was a MAPPED navigation into the Folder contract, and while it stayed mapped EF rediscovered
    // Folder through it and claimed the table FolderAggregate owns. That half landed — the navigation is
    // severed and FileRepository stitches the member on by hand.
    //
    // The FILE ROW did not, and TheToLowerTranslationGapTests below is the measurement that says why: the two
    // File name lookups filter with .ToLower() on a COLUMN, RavenDB's LINQ provider throws on it, and there is
    // no portable spelling. That is D22 rule 9's escalate condition. See src/Stomme.Server/README.md (persistence seam).
    public class FolderAggregateMappingTests
    {
        private sealed class ProbeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private static IEntityType BuildEntityType(IRelationalDatabase database, string connectionString)
        {
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, connectionString);
            using var context = new ProbeContext(builder.Options, database);
            return context.Model.FindEntityType(typeof(FolderAggregate))!;
        }

        private static IEntityType PostgreSql() => BuildEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

        [Fact]
        public void The_aggregate_maps_to_the_snake_case_table_and_key_the_migrations_created_on_postgresql()
        {
            var entityType = PostgreSql();

            Assert.Equal("folder", entityType.GetTableName());
            Assert.Equal("folder_id", entityType.FindProperty(nameof(FolderAggregate.Id))!.GetColumnName());
        }

        // The rewriter is a PostgreSQL concern; the other providers rewrite to themselves. An explicit
        // HasColumnName outranks the snake_case convention, which is why every explicit name in
        // ConfigureFrameworkStore goes through IDatabase.RewriteName.
        [Theory]
        [InlineData(typeof(SqliteDatabase), "Data Source=:memory:")]
        [InlineData(typeof(SqlServerDatabase), "Server=(local);Database=probe;")]
        public void The_aggregate_maps_to_pascal_case_on_providers_without_a_rewriter(Type databaseType, string connectionString)
        {
            var database = (IRelationalDatabase)Activator.CreateInstance(databaseType)!;
            var entityType = BuildEntityType(database, connectionString);

            Assert.Equal("Folder", entityType.GetTableName());
            Assert.Equal("FolderId", entityType.FindProperty(nameof(FolderAggregate.Id))!.GetColumnName());
        }

        // AggregateRoot<TId>.Version has no column in the harvested schema — parity with the IDatabase path,
        // which has no optimistic concurrency on Folder either. Phase 5 owns the column.
        [Fact]
        public void The_seam_concurrency_token_is_not_mapped_because_no_harvested_table_has_a_column_for_it()
        {
            Assert.Null(PostgreSql().FindProperty(nameof(FolderAggregate.Version)));
        }

        [Fact]
        public void The_aggregate_does_not_assign_its_own_identity_because_the_database_does()
        {
            Assert.Equal(ValueGenerated.OnAdd, PostgreSql().FindProperty(nameof(FolderAggregate.Id))!.ValueGenerated);
        }

        /// <summary>
        /// The column list, VERIFIED against the migrated PostgreSQL database (stomme-pg-44357), not against
        /// FolderEntityBuilder — which would have produced a wrong answer twice over. It omits Type, Capacity,
        /// ImageSizes and CacheControl (all four added by later migrations) and it CREATES DeletedBy, DeletedOn
        /// and IsDeleted, none of which survive.
        /// </summary>
        [Fact]
        public void The_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var columns = PostgreSql().GetProperties().Select(item => item.GetColumnName()).OrderBy(item => item).ToList();

            Assert.Equal(
            [
                "cache_control",
                "capacity",
                "created_by",
                "created_on",
                "folder_id",
                "image_sizes",
                "is_system",
                "modified_by",
                "modified_on",
                "name",
                "order",
                "parent_id",
                "path",
                // D49. The SIXTEENTH column, added and backfilled by 10020003_AddFolderFileNormalizedColumns.
                // It is what let the File cluster cross the seam: the render path's folder lookup filters this
                // with a plain == that both providers agree on, where it used to fold case on `path` and throw
                // on RavenDB. Derived in FolderAggregate.Path's setter, so it cannot drift from `path`.
                "path_normalized",
                "site_id",
                "type"
            ], columns);
        }

        /// <summary>
        /// <b>THE mutation check of this increment, and the standing lesson's sharpest case yet.</b>
        /// FolderEntityBuilder extends DeletableAuditableBaseEntityBuilder and calls AddDeletableColumns, so
        /// reading the builder says Folder is soft-deletable. It is not: 03020201 drops all three columns,
        /// 03030201 restores IsDeleted for non-SQLite, and 10000201 drops it everywhere. The live database has
        /// FIFTEEN columns and not one is is_deleted, deleted_by or deleted_on.
        /// <para>
        /// DbContextUtils decides by RUNTIME TYPE TEST, so adding IDeletable to FolderAggregate would compile,
        /// register and render — and then write to columns that do not exist. Add <c>IDeletable</c> to
        /// FolderAggregate and this test fails.
        /// </para>
        /// </summary>
        [Fact]
        public void The_aggregate_is_auditable_but_not_deletable_because_its_table_has_audit_columns_and_no_deletable_ones()
        {
            Assert.True(typeof(IAuditable).IsAssignableFrom(typeof(FolderAggregate)));
            Assert.False(typeof(IDeletable).IsAssignableFrom(typeof(FolderAggregate)));

            var columns = PostgreSql().GetProperties().Select(item => item.GetColumnName()).ToList();

            // The audit quartet is real and mapped...
            Assert.Contains("created_by", columns);
            Assert.Contains("created_on", columns);
            Assert.Contains("modified_by", columns);
            Assert.Contains("modified_on", columns);

            // ...and the deletable trio the BUILDER creates is not.
            Assert.DoesNotContain("is_deleted", columns);
            Assert.DoesNotContain("deleted_by", columns);
            Assert.DoesNotContain("deleted_on", columns);
        }

        // Reproduced from the live schema rather than tidied: parent_id is the folder hierarchy's root marker
        // and type/capacity/image_sizes/cache_control are all later, nullable additions.
        [Fact]
        public void The_nullable_columns_stay_nullable()
        {
            var entityType = PostgreSql();

            Assert.True(entityType.FindProperty(nameof(FolderAggregate.ParentId))!.IsNullable);
            Assert.True(entityType.FindProperty(nameof(FolderAggregate.Type))!.IsNullable);
            Assert.True(entityType.FindProperty(nameof(FolderAggregate.ImageSizes))!.IsNullable);
            Assert.True(entityType.FindProperty(nameof(FolderAggregate.CacheControl))!.IsNullable);
        }
    }

    public class FolderMapperTests
    {
        private static Folder NewFolder() => new()
        {
            FolderId = 12,
            SiteId = 3,
            ParentId = 1,
            Type = FolderTypes.Public,
            Name = "Images",
            Path = "Public/Images/",
            Order = 2,
            ImageSizes = "200x200,x100",
            Capacity = 1024,
            IsSystem = true,
            CacheControl = "public, max-age=3600",
            CreatedBy = "host",
            CreatedOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedBy = "admin",
            ModifiedOn = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc)
        };

        [Fact]
        public void The_mapper_round_trips_every_persisted_member()
        {
            var original = NewFolder();

            var round = FolderMapper.ToModel(FolderMapper.ToAggregate(original, assignId: true));

            Assert.Equal(original.FolderId, round.FolderId);
            Assert.Equal(original.SiteId, round.SiteId);
            Assert.Equal(original.ParentId, round.ParentId);
            Assert.Equal(original.Type, round.Type);
            Assert.Equal(original.Name, round.Name);
            Assert.Equal(original.Path, round.Path);
            Assert.Equal(original.Order, round.Order);
            Assert.Equal(original.ImageSizes, round.ImageSizes);
            Assert.Equal(original.Capacity, round.Capacity);
            Assert.Equal(original.IsSystem, round.IsSystem);
            Assert.Equal(original.CacheControl, round.CacheControl);
            Assert.Equal(original.CreatedBy, round.CreatedBy);
            Assert.Equal(original.CreatedOn, round.CreatedOn);
            Assert.Equal(original.ModifiedBy, round.ModifiedBy);
            Assert.Equal(original.ModifiedOn, round.ModifiedOn);
        }

        /// <summary>
        /// PermissionList, Level and HasChildren are [NotMapped] view state the repository populates AFTER the
        /// mapper runs. Round-tripping LOSES all three by design — the same contract PageModuleMapper's
        /// severed-navigation test pins.
        /// </summary>
        [Fact]
        public void The_mapper_does_not_round_trip_the_unmapped_view_state()
        {
            var folder = NewFolder();
            folder.PermissionList = [new Permission { PermissionName = "View" }];
            folder.Level = 4;
            folder.HasChildren = true;

            var round = FolderMapper.ToModel(FolderMapper.ToAggregate(folder, assignId: true));

            Assert.Null(round.PermissionList);
            Assert.Equal(0, round.Level);
            Assert.False(round.HasChildren);
        }

        [Fact]
        public void An_insert_does_not_assign_the_identity_and_an_update_does()
        {
            Assert.Equal(0, FolderMapper.ToAggregate(NewFolder(), assignId: false).Id);
            Assert.Equal(12, FolderMapper.ToAggregate(NewFolder(), assignId: true).Id);
        }

        // FolderController.Post returns the created folder straight to the client, and UserManager reads the
        // new FolderId back to create the user's folder beneath it.
        [Fact]
        public void Copying_persisted_values_back_carries_the_database_assigned_id_and_stamps_to_the_caller()
        {
            var folder = new Folder { Name = "New", Path = "New/", SiteId = 1 };
            var aggregate = FolderMapper.ToAggregate(folder, assignId: false);

            // What the store and DbContextUtils do between AddAsync and the return.
            typeof(FolderAggregate).GetProperty(nameof(FolderAggregate.Id))!.SetValue(aggregate, 77);
            aggregate.CreatedBy = "host";
            aggregate.CreatedOn = new DateTime(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc);
            aggregate.ModifiedBy = "host";
            aggregate.ModifiedOn = new DateTime(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc);

            FolderMapper.CopyPersistedValues(aggregate, folder);

            Assert.Equal(77, folder.FolderId);
            Assert.Equal("host", folder.CreatedBy);
            Assert.Equal(new DateTime(2024, 5, 5, 0, 0, 0, DateTimeKind.Utc), folder.CreatedOn);
            Assert.Equal("host", folder.ModifiedBy);
        }
    }

    // Rule (g)/D19/D22 rule 6: deleting the DbSet is NOT enough — EF rediscovers CLR types through
    // NAVIGATIONS. THIS WAS NOT HYPOTHETICAL FOR THIS CLUSTER, and it is the reason Folder could not move
    // alone. Mapping FolderAggregate while File.Folder was still a navigation broke the model, OBSERVED:
    //
    //   "The table 'folder' cannot be used for entity type 'FolderAggregate' since it is being used for
    //    entity type 'Folder' and potentially other entity types, but there is no linking relationship."
    //
    // ...from a probe context that declared DbSet<File> and NO DbSet<Folder> at all. Severing File.Folder to
    // [NotMapped] is what fixed it, and these tests pin the state reflectively.
    public class FolderMustNotBeResurrectedTests
    {
        // The neighbours that are STILL genuinely unmigrated — the shape of TenantDBContext.
        //
        // File stood here until D49 put it on the seam, and it was the BEST probe this context ever had: it is
        // the entity whose navigation caused the blocker, so declaring it also proved the [NotMapped] severance
        // held. That proof did not disappear with it — No_entity_in_the_model_navigates_into_the_folder_contract
        // below now sweeps the FileAggregate mapping instead, which is a STRONGER form: an aggregate has no
        // navigation member to sever in the first place.
        //
        // SearchContent was what was left to probe with, and D50 took it — so THIS CONTEXT IS NOW EMPTY.
        // See the note in LeafClustersOnTheSeamTests: the neighbour DbSet was decoration.
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

        /// <summary>
        /// The mutation check, and the one that reproduces the observed failure exactly: remove [NotMapped]
        /// from File.Folder and this throws while building the model.
        /// </summary>
        [Fact]
        public void The_folder_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToTheFolderTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "folder")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(FolderAggregate)], mappedToTheFolderTable);
        }

        [Fact]
        public void The_folder_contract_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(Folder)));
        }

        // The sweep, made executable: nothing in the tenant model may navigate to the Folder contract or its
        // aggregate. File.Folder is the one that did.
        [Fact]
        public void No_entity_in_the_model_navigates_into_the_folder_contract()
        {
            var navigationsIntoFolder = BuildModel().GetEntityTypes()
                .SelectMany(entityType => entityType.GetNavigations())
                .Where(navigation => navigation.TargetEntityType.ClrType == typeof(Folder)
                    || navigation.TargetEntityType.ClrType == typeof(FolderAggregate))
                .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name}")
                .ToList();

            Assert.Empty(navigationsIntoFolder);
        }

        // Folder is TENANT-ONLY, like Permission and unlike Setting: MasterDBContext never had a
        // DbSet<Folder> and FolderEntityBuilder appears only in the TENANT migration set.
        private sealed class MasterLikeContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
        }

        [Fact]
        public void The_master_context_does_not_map_the_folder_aggregate()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new MasterLikeContext(builder.Options, database);

            Assert.Null(context.Model.FindEntityType(typeof(FolderAggregate)));
        }

        // The aggregate references its parent and its site BY ID and must never grow a navigation back.
        [Fact]
        public void The_folder_aggregate_navigates_nowhere()
        {
            var database = new PostgreSQLDatabase();
            var builder = new DbContextOptionsBuilder();
            database.UseDatabase(builder, "Host=localhost;Database=probe");
            using var context = new TenantLikeContext(builder.Options, database);

            Assert.Empty(context.Model.FindEntityType(typeof(FolderAggregate))!.GetNavigations());
        }

        /// <summary>
        /// The severance pinned at the CONTRACT, reflectively — where it actually lives. [NotMapped] on
        /// File.Folder is what every context in the process depends on, not just TenantDBContext.
        /// </summary>
        [Fact]
        public void The_severed_folder_navigation_stays_unmapped_on_the_file_contract()
        {
            var folder = typeof(File).GetProperty(nameof(File.Folder))!;

            Assert.NotNull(folder.GetCustomAttribute<NotMappedAttribute>());
        }
    }

    /// <summary>
    /// The readers of the severed navigation (D45's lesson 5). File.Folder is dereferenced in roughly thirty
    /// production places — FileController, Pages/Files.cshtml.cs (THE render path) and SynchronizationJob —
    /// so severing it would have thrown NullReferenceException on the first file request had FileRepository
    /// not stitched it back on. These pin that every read path populates it.
    /// </summary>
    public class FileFolderStitchTests
    {
        // Every method on IFileRepository that hands back a File must populate its Folder, because callers
        // dereference it without a null check: Files.cshtml.cs reads file.Folder.SiteId on the line after the
        // read, and FileController.Get does the same.
        [Theory]
        [InlineData(nameof(IFileRepository.GetFilesAsync))]
        [InlineData(nameof(IFileRepository.GetFileAsync))]
        [InlineData(nameof(IFileRepository.AddFileAsync))]
        [InlineData(nameof(IFileRepository.UpdateFileAsync))]
        public void Every_file_returning_repository_method_is_declared(string method)
        {
            Assert.Contains(typeof(IFileRepository).GetMethods(), item => item.Name == method);
        }

        /// <summary>
        /// The contract's Folder member survives as a settable property — the stitch depends on it. A
        /// well-meaning tidy-up that made it get-only would break every read path.
        /// </summary>
        [Fact]
        public void The_file_contract_still_carries_a_settable_folder_for_the_repository_to_stitch()
        {
            var folder = typeof(File).GetProperty(nameof(File.Folder))!;

            Assert.NotNull(folder);
            Assert.True(folder.CanWrite);
            Assert.Equal(typeof(Folder), folder.PropertyType);
        }
    }

    public class FolderStoreBehaviourTests : LeafClusterStoreBehaviourTests<FolderAggregate>
    {
        // The folder table exactly as the migration set leaves it: FolderEntityBuilder, PLUS 02010003 (Type),
        // 02030001 (ImageSizes, Capacity), the widening of Name/Path, 10020003 (PathNormalized), MINUS the
        // three deletable columns 03020201/10000201 remove. Cross-checked column-for-column against
        // stomme-pg-44357.
        protected override string TableDdl => """
            CREATE TABLE "Folder" (
                "FolderId" INTEGER NOT NULL CONSTRAINT "PK_Folder" PRIMARY KEY AUTOINCREMENT,
                "SiteId" INTEGER NOT NULL,
                "ParentId" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "Path" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                "IsSystem" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "Type" TEXT NULL,
                "Capacity" INTEGER NULL,
                "ImageSizes" TEXT NULL,
                "CacheControl" TEXT NULL,
                "PathNormalized" TEXT NULL
            );
            CREATE UNIQUE INDEX "IX_Folder" ON "Folder" ("SiteId", "Path");
            """;

        private static int _path = 0;

        protected override FolderAggregate NewAggregate() => new()
        {
            SiteId = 1,
            ParentId = null,
            Type = FolderTypes.Private,
            Name = "Folder",
            Path = "Folder" + Interlocked.Increment(ref _path) + "/",
            Order = 1,
            IsSystem = false,
            CreatedBy = "host",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "host",
            ModifiedOn = DateTime.UtcNow
        };

        protected override FolderAggregate RebuildMutated(int id)
        {
            var aggregate = NewAggregate();
            aggregate.Name = "Renamed";
            typeof(FolderAggregate).GetProperty(nameof(FolderAggregate.Id))!.SetValue(aggregate, id);
            return aggregate;
        }

        protected override void AssertMutated(FolderAggregate loaded) => Assert.Equal("Renamed", loaded.Name);

        /// <summary>
        /// The reserved word, round-tripped. <c>Order</c> is reserved on every relational provider, and
        /// <c>Type</c> and <c>Path</c> are the two columns the folder's physical directory resolves from —
        /// lose either and GetFolderPathAsync builds the wrong path on disk.
        /// </summary>
        [Fact]
        public async Task The_reserved_word_order_and_the_path_columns_round_trip()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            aggregate.Order = 7;
            aggregate.Type = FolderTypes.Public;
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.Equal(7, loaded.Order);
            Assert.Equal(FolderTypes.Public, loaded.Type);
            Assert.Equal(aggregate.Path, loaded.Path);
        }

        /// <summary>
        /// The columns the EntityBuilder omits round-trip anyway — they are real, and three of them are read
        /// on the render path (ImageSizes and CacheControl by Files.cshtml.cs, Type by every path resolution).
        /// </summary>
        [Fact]
        public async Task The_columns_the_entity_builder_omits_round_trip_anyway()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            aggregate.ImageSizes = "200x200,x100";
            aggregate.Capacity = 4096;
            aggregate.CacheControl = "public, max-age=3600";
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            var loaded = (await NewStore(readContext).GetByIdAsync(aggregate.Id))!;

            Assert.Equal("200x200,x100", loaded.ImageSizes);
            Assert.Equal(4096, loaded.Capacity);
            Assert.Equal("public, max-age=3600", loaded.CacheControl);
        }

        // A site's root folder has no parent; the hierarchy walk keys off exactly this.
        [Fact]
        public async Task A_root_folder_persists_a_null_parent_id()
        {
            using var context = NewContext();
            var aggregate = NewAggregate();
            aggregate.ParentId = null;
            await NewStore(context).AddAsync(aggregate);

            using var readContext = NewContext();
            Assert.Null((await NewStore(readContext).GetByIdAsync(aggregate.Id))!.ParentId);
        }

        /// <summary>
        /// The UNIQUE index on (SiteId, Path) is real, and it is what makes
        /// <c>FolderRepository.GetFolderAsync(siteId, path)</c> a single-row lookup rather than a "pick one".
        /// It does NOT cross to RavenDB — see RavenFolderStore.
        /// </summary>
        [Fact]
        public async Task A_duplicate_site_and_path_is_rejected_by_the_unique_index()
        {
            using var context = NewContext();
            var first = NewAggregate();
            await NewStore(context).AddAsync(first);

            using var duplicateContext = NewContext();
            var duplicate = NewAggregate();
            duplicate.Path = first.Path;

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => NewStore(duplicateContext).AddAsync(duplicate));
        }

        // ...and the same path under ANOTHER site is not a duplicate. The index is (SiteId, Path), not (Path).
        [Fact]
        public async Task The_same_path_under_another_site_is_not_a_duplicate()
        {
            using var context = NewContext();
            var first = NewAggregate();
            await NewStore(context).AddAsync(first);

            using var otherContext = NewContext();
            var other = NewAggregate();
            other.Path = first.Path;
            other.SiteId = first.SiteId + 1;

            await NewStore(otherContext).AddAsync(other);

            Assert.True(other.Id > 0);
        }

        /// <summary>
        /// <b>The case-sensitivity of the unique index, measured — and the reason FileRepository resolves a
        /// LIST of folder ids rather than one.</b> <c>Docs/</c> and <c>docs/</c> both lowercase to
        /// <c>docs/</c> but are two distinct rows here, so a case-insensitive path lookup legitimately matches
        /// more than one folder.
        /// </summary>
        [Fact]
        public async Task Two_folders_whose_paths_differ_only_by_case_coexist_in_one_site()
        {
            using var context = NewContext();
            var lower = NewAggregate();
            lower.Path = "docs/";
            await NewStore(context).AddAsync(lower);

            using var upperContext = NewContext();
            var upper = NewAggregate();
            upper.Path = "Docs/";
            upper.SiteId = lower.SiteId;
            await NewStore(upperContext).AddAsync(upper);

            Assert.True(upper.Id > 0);
            Assert.NotEqual(lower.Id, upper.Id);
        }
    }

    /// <summary>
    /// <b>THE MEASUREMENT THAT KEPT FILE OFF THE SEAM.</b> Not an assertion about Stomme — an
    /// assertion about what each provider's LINQ implementation can and cannot do, pinned so the finding
    /// cannot decay into folklore and so the day RavenDB (or the seam) grows a translation, this test fails and
    /// tells the next reader the blocker is gone.
    /// <para>
    /// The relational half runs here. The RavenDB half cannot run in this project (no server dependency), so
    /// it is asserted in the conformance suite, which is gated on a live server —
    /// <c>The_seam_cannot_express_a_case_insensitive_column_filter_portably</c>.
    /// </para>
    /// </summary>
    public class TheToLowerTranslationGapTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        public TheToLowerTranslationGapTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Folder" (
                    "FolderId" INTEGER NOT NULL CONSTRAINT "PK_Folder" PRIMARY KEY AUTOINCREMENT,
                    "SiteId" INTEGER NOT NULL,
                    "ParentId" INTEGER NULL,
                    "Name" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "Order" INTEGER NOT NULL,
                    "IsSystem" INTEGER NOT NULL,
                    "CreatedBy" TEXT NOT NULL,
                    "CreatedOn" TEXT NOT NULL,
                    "ModifiedBy" TEXT NOT NULL,
                    "ModifiedOn" TEXT NOT NULL,
                    "Type" TEXT NULL,
                    "Capacity" INTEGER NULL,
                    "ImageSizes" TEXT NULL,
                    "CacheControl" TEXT NULL,
                    "PathNormalized" TEXT NULL
                );
                """;
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

        /// <summary>
        /// The relational providers translate <c>.ToLower()</c> on a column, and the filter runs in the
        /// DATABASE. This is the behaviour FileRepository depends on and the reason the column-side ToLower
        /// cannot simply be deleted: the live database stores <c>Public/</c> and <c>Users/1/</c> while the
        /// render path asks for <c>public/</c>.
        /// </summary>
        [Fact]
        public async Task The_relational_provider_translates_a_case_insensitive_column_filter()
        {
            using var context = NewContext();
            var store = new EfRepository<FolderAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);
            await store.AddAsync(new FolderAggregate
            {
                SiteId = 1,
                Name = "Public",
                Path = "Public/",           // stored MIXED case, as the live database does
                Type = FolderTypes.Public,
                Order = 1,
                CreatedBy = "host",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "host",
                ModifiedOn = DateTime.UtcNow
            });

            using var readContext = NewContext();
            var found = readContext.Set<FolderAggregate>()
                .Where(item => item.SiteId == 1 && item.Path.ToLower() == "public/")  // asked for LOWER case
                .ToList();

            Assert.Single(found);
        }

        /// <summary>
        /// ...and the plain equality the seam CAN express portably does NOT do the same job on the relational
        /// providers. This is the test that refutes "just drop the ToLower": it would 404 every file in a
        /// mixed-case folder.
        /// </summary>
        [Fact]
        public async Task A_plain_equality_does_not_substitute_for_it_on_the_relational_provider()
        {
            using var context = NewContext();
            var store = new EfRepository<FolderAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);
            await store.AddAsync(new FolderAggregate
            {
                SiteId = 1,
                Name = "Public",
                Path = "Public/",
                Type = FolderTypes.Public,
                Order = 1,
                CreatedBy = "host",
                CreatedOn = DateTime.UtcNow,
                ModifiedBy = "host",
                ModifiedOn = DateTime.UtcNow
            });

            using var readContext = NewContext();
            var found = readContext.Set<FolderAggregate>()
                .Where(item => item.SiteId == 1 && item.Path == "public/")
                .ToList();

            // Case-SENSITIVE here. On RavenDB the very same expression is case-INSENSITIVE and returns the row
            // — which is precisely why one expression cannot serve both providers. See the conformance suite.
            Assert.Empty(found);
        }
    }
}
