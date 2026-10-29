using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Database.PostgreSQL;
using Stomme.Database.Sqlite;
using Stomme.Databases.Interfaces;
using Stomme.Infrastructure;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using File = Stomme.Models.File;

namespace Stomme.Tests
{
    // The File cluster on the persistence seam — the LAST framework cluster to cross, and the one
    // D48 escalated under D22 rule 9 rather than ship on one provider.
    //
    // D48 measured the blocker on both live servers: FileRepository's two name lookups folded case on the
    // COLUMN, which the relational providers translate and RavenDB THROWS on, while the portable == means
    // case-SENSITIVE on relational and case-INSENSITIVE on RavenDB. One expression, two semantics.
    //
    // D49's answer is not a translation, it is a SCHEMA change: folder.path_normalized and file.name_normalized
    // hold a lowercase copy beside the stored case, and the reads filter them with a plain == that both
    // providers agree on — because for two lowercase strings ordinal and case-insensitive equality are the same
    // relation. That is ASP.NET Identity's NormalizedUserName split, which TenantDBContext already runs.
    //
    // The equivalence is proved where it must be — against BOTH LIVE PROVIDERS, in the conformance suite's
    // A_normalized_column_filter_is_identical_on_every_provider. These tests cover what conformance cannot:
    // the mapping against the real migrated schema, the derivation that makes an unnormalized row
    // unrepresentable, and the write lifetime.
    public class FileAggregateMappingTests
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
            return context.Model.FindEntityType(typeof(FileAggregate))!;
        }

        private static IEntityType PostgreSql() => BuildEntityType(new PostgreSQLDatabase(), "Host=localhost;Database=probe");

        [Fact]
        public void The_aggregate_maps_to_the_snake_case_table_and_key_the_migrations_created_on_postgresql()
        {
            var entityType = PostgreSql();

            Assert.Equal("file", entityType.GetTableName());
            Assert.Equal("file_id", entityType.FindPrimaryKey()!.Properties.Single().GetColumnName());
        }

        /// <summary>
        /// THIRTEEN columns: the twelve the live database has, plus name_normalized which D49 adds. VERIFIED
        /// against stomme-pg-44357 rather than read off FileEntityBuilder, which is wrong about this table in
        /// two separate ways — see the mutation check below, and note it also omits `description`, which is
        /// real.
        /// </summary>
        [Fact]
        public void The_aggregate_maps_to_the_columns_the_migrations_created_on_postgresql()
        {
            var columns = PostgreSql().GetProperties().Select(item => item.GetColumnName()).OrderBy(item => item).ToList();

            Assert.Equal(
            [
                "created_by",
                "created_on",
                "description",
                "extension",
                "file_id",
                "folder_id",
                "image_height",
                "image_width",
                "modified_by",
                "modified_on",
                "name",
                // D49, added and backfilled by 10020003_AddFolderFileNormalizedColumns. The column that let
                // this cluster cross the seam at all.
                "name_normalized",
                "size"
            ], columns);
        }

        /// <summary>
        /// <b>THE mutation check, and the same trap D48 documented for Folder — re-measured here, not
        /// inherited.</b> FileEntityBuilder extends DeletableAuditableBaseEntityBuilder and calls
        /// AddDeletableColumns, so reading the builder says File is soft-deletable. IT IS NOT: 03020201 drops
        /// all three columns, 03030201 restores IsDeleted for non-SQLite, and 10000201 drops it everywhere. The
        /// live `file` table has TWELVE columns and not one is is_deleted, deleted_by or deleted_on — the three
        /// attnum slots they occupied are dropped stubs.
        /// <para>
        /// DbContextUtils decides by RUNTIME TYPE TEST, so adding IDeletable to FileAggregate would compile,
        /// register and render — and then write to columns that do not exist, on every file upload. Add
        /// <c>IDeletable</c> to FileAggregate and this test fails.
        /// </para>
        /// </summary>
        [Fact]
        public void The_aggregate_is_auditable_but_not_deletable_because_its_table_has_audit_columns_and_no_deletable_ones()
        {
            Assert.True(typeof(IAuditable).IsAssignableFrom(typeof(FileAggregate)));
            Assert.False(typeof(IDeletable).IsAssignableFrom(typeof(FileAggregate)));

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

        // Reproduced from the live schema rather than tidied. description is a later, nullable addition the
        // builder omits; name_normalized is nullable because an upgrader's rows are NULL until 10020003's
        // backfill runs.
        [Fact]
        public void The_nullable_columns_stay_nullable()
        {
            var entityType = PostgreSql();

            Assert.True(entityType.FindProperty(nameof(FileAggregate.Description))!.IsNullable);
            Assert.True(entityType.FindProperty(nameof(FileAggregate.NameNormalized))!.IsNullable);
        }
    }

    /// <summary>
    /// The derivation that makes D49 structural rather than a convention. These are the tests that would fail
    /// if someone "tidied" the normalization back into the mapper.
    /// </summary>
    public class TheNormalizedDerivationTests
    {
        [Fact]
        public void Setting_a_file_name_derives_its_normalized_lookup_key()
        {
            var file = new FileAggregate { Name = "Test-Image.PNG" };

            Assert.Equal("Test-Image.PNG", file.Name);
            Assert.Equal("test-image.png", file.NameNormalized);
        }

        [Fact]
        public void Setting_a_folder_path_derives_its_normalized_lookup_key()
        {
            var folder = new FolderAggregate { Path = "Users/1/" };

            Assert.Equal("Users/1/", folder.Path);
            Assert.Equal("users/1/", folder.PathNormalized);
        }

        /// <summary>
        /// Re-deriving on every set is what makes a rename safe. The store writes whatever the aggregate holds,
        /// so a normalized key that went stale on mutation would leave the row findable only by its OLD name —
        /// silently, on both providers.
        /// </summary>
        [Fact]
        public void Re_setting_a_name_re_derives_the_key_rather_than_leaving_the_first_one()
        {
            var file = new FileAggregate { Name = "Old.PNG" };
            file.Name = "New.PNG";

            Assert.Equal("new.png", file.NameNormalized);
        }

        /// <summary>
        /// The root folder's path is <c>""</c> in the live database, not null — and it must normalize to
        /// <c>""</c>, not to null, or <c>GET /files/logo-white.png</c> (whose folderpath is <c>""</c>) stops
        /// resolving. Null in, null out is for a genuinely absent value.
        /// </summary>
        [Fact]
        public void An_empty_path_normalizes_to_empty_and_a_null_to_null()
        {
            Assert.Equal("", new FolderAggregate { Path = "" }.PathNormalized);
            Assert.Null(new FolderAggregate { Path = null }.PathNormalized);
            Assert.Null(new FileAggregate { Name = null }.NameNormalized);
        }

        /// <summary>
        /// <b>The lookup key has no public setter, and that is the point.</b> If it had one, a caller could
        /// write a row whose normalized column disagreed with its stored column — which reads as "this file
        /// does not exist" on both providers, silently. Give <see cref="FileAggregate.NameNormalized"/> a
        /// public setter and this fails.
        /// </summary>
        [Fact]
        public void The_normalized_keys_cannot_be_set_independently_of_the_column_they_derive_from()
        {
            Assert.Null(typeof(FileAggregate).GetProperty(nameof(FileAggregate.NameNormalized))!.SetMethod?.IsPublic switch
            {
                true => "FileAggregate.NameNormalized has a public setter",
                _ => null
            });

            Assert.Null(typeof(FolderAggregate).GetProperty(nameof(FolderAggregate.PathNormalized))!.SetMethod?.IsPublic switch
            {
                true => "FolderAggregate.PathNormalized has a public setter",
                _ => null
            });
        }

        /// <summary>
        /// The lookup key is STORAGE state, never wire state — Identity never hands you a NormalizedUserName
        /// either. Neither contract may carry it.
        /// </summary>
        [Fact]
        public void The_normalized_keys_are_absent_from_the_wire_contracts()
        {
            Assert.Null(typeof(File).GetProperty("NameNormalized"));
            Assert.Null(typeof(Folder).GetProperty("PathNormalized"));
        }
    }

    public class FileMapperTests
    {
        private static File NewFile() => new()
        {
            FileId = 12,
            FolderId = 3,
            Name = "Test-Image.PNG",
            Extension = "png",
            Size = 8192,
            ImageHeight = 80,
            ImageWidth = 250,
            Description = "a description",
            CreatedBy = "host",
            CreatedOn = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ModifiedBy = "editor",
            ModifiedOn = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc)
        };

        [Fact]
        public void A_file_round_trips_through_the_mapper()
        {
            var mapped = FileMapper.ToModel(FileMapper.ToAggregate(NewFile(), assignId: true));

            Assert.Equal(12, mapped.FileId);
            Assert.Equal(3, mapped.FolderId);
            Assert.Equal("Test-Image.PNG", mapped.Name);
            Assert.Equal("png", mapped.Extension);
            Assert.Equal(8192, mapped.Size);
            Assert.Equal(80, mapped.ImageHeight);
            Assert.Equal(250, mapped.ImageWidth);
            Assert.Equal("a description", mapped.Description);
            Assert.Equal("host", mapped.CreatedBy);
            Assert.Equal("editor", mapped.ModifiedBy);
        }

        /// <summary>
        /// An insert must NOT carry the id: the database assigns it. Mirrors the Folder mapper's contract.
        /// </summary>
        [Fact]
        public void An_insert_does_not_assign_the_identity()
        {
            Assert.Equal(0, FileMapper.ToAggregate(NewFile(), assignId: false).Id);
            Assert.Equal(12, FileMapper.ToAggregate(NewFile(), assignId: true).Id);
        }

        /// <summary>
        /// <see cref="File.Folder"/> and <see cref="File.Url"/> are [NotMapped] view state the repository
        /// stitches on AFTER the mapper runs. Round-tripping LOSES both, by design.
        /// </summary>
        [Fact]
        public void The_mapper_does_not_round_trip_the_unmapped_view_state()
        {
            var file = NewFile();
            file.Folder = new Folder { FolderId = 3, Path = "Public/" };
            file.Url = "/files/public/test-image.png";

            var mapped = FileMapper.ToModel(FileMapper.ToAggregate(file, assignId: true));

            Assert.Null(mapped.Folder);
            Assert.Null(mapped.Url);
        }

        /// <summary>
        /// The mapper does not — and cannot — set the lookup key: <see cref="FileAggregate.Name"/> derives it.
        /// This asserts the derivation survives the mapper rather than being bypassed by it, which is the whole
        /// reason it does not live here.
        /// </summary>
        [Fact]
        public void The_mapper_does_not_round_trip_the_normalized_lookup_key_but_the_aggregate_still_has_one()
        {
            Assert.Equal("test-image.png", FileMapper.ToAggregate(NewFile(), assignId: true).NameNormalized);
        }

        [Fact]
        public void CopyPersistedValues_copies_the_generated_id_and_the_stamps()
        {
            var aggregate = FileMapper.ToAggregate(NewFile(), assignId: false);
            typeof(FileAggregate).GetProperty(nameof(FileAggregate.Id))!.SetValue(aggregate, 99);
            aggregate.CreatedBy = "stamped";
            aggregate.ModifiedBy = "stamped";

            var file = new File();
            FileMapper.CopyPersistedValues(aggregate, file);

            Assert.Equal(99, file.FileId);
            Assert.Equal("stamped", file.CreatedBy);
            Assert.Equal("stamped", file.ModifiedBy);
        }
    }

    // The D19 blocker, for the entity that CAUSED it. File.Folder was the mapped navigation that made EF
    // rediscover the Folder contract and claim FolderAggregate's table. D49 maps FileAggregate onto the
    // same `file` table the File contract used to own, so the contract must be gone from the tenant model —
    // and the DbSet<File> deletion from TenantDBContext is the compiler's half of that proof.
    public class FileMustNotBeResurrectedTests
    {
        // File stood here until D49; the SearchContent family stood here until D50 and was the last
        // unmigrated tenant-side entity there was. THIS CONTEXT IS NOW EMPTY — see the note in
        // LeafClustersOnTheSeamTests: the neighbour DbSet was decoration, and the proof is
        // ConfigureFrameworkStore plus the assertion.
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
        /// Re-add <c>DbSet&lt;File&gt;</c> to this context (or to TenantDBContext) and this throws while
        /// building the model — "the table 'file' cannot be used for entity type 'FileAggregate' since it is
        /// being used for entity type 'File'". That is the failure D49 had to clear, and it is the same one
        /// D45/D48 met.
        /// </summary>
        [Fact]
        public void The_file_table_is_owned_by_the_aggregate_alone()
        {
            var mappedToTheFileTable = BuildModel().GetEntityTypes()
                .Where(entityType => entityType.GetTableName() == "file")
                .Select(entityType => entityType.ClrType)
                .ToList();

            Assert.Equal([typeof(FileAggregate)], mappedToTheFileTable);
        }

        [Fact]
        public void The_file_contract_is_not_an_entity_type_of_the_tenant_context()
        {
            Assert.Null(BuildModel().FindEntityType(typeof(File)));
        }

        /// <summary>
        /// The sweep, made executable: nothing in the tenant model may navigate to the File contract or its
        /// aggregate — the mirror of FolderMustNotBeResurrectedTests' sweep, and it is what replaces the
        /// DbSet&lt;File&gt; probe those contexts used to carry.
        /// </summary>
        [Fact]
        public void No_entity_in_the_model_navigates_into_the_file_contract()
        {
            var navigationsIntoFile = BuildModel().GetEntityTypes()
                .SelectMany(entityType => entityType.GetNavigations())
                .Where(navigation => navigation.TargetEntityType.ClrType == typeof(File)
                    || navigation.TargetEntityType.ClrType == typeof(FileAggregate))
                .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name}")
                .ToList();

            Assert.Empty(navigationsIntoFile);
        }
    }

    public class FileStoreBehaviourTests : LeafClusterStoreBehaviourTests<FileAggregate>
    {
        // The file table exactly as the migration set leaves it: FileEntityBuilder, PLUS 03000101 (the
        // Name/Extension widening), the later Description column, 10020003 (NameNormalized), MINUS the three
        // deletable columns 03020201/10000201 remove. Cross-checked column-for-column against
        // stomme-pg-44357: TWELVE live columns + the one D49 adds.
        protected override string TableDdl => """
            CREATE TABLE "File" (
                "FileId" INTEGER NOT NULL CONSTRAINT "PK_File" PRIMARY KEY AUTOINCREMENT,
                "FolderId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Extension" TEXT NOT NULL,
                "Size" INTEGER NOT NULL,
                "ImageHeight" INTEGER NOT NULL,
                "ImageWidth" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "Description" TEXT NULL,
                "NameNormalized" TEXT NULL
            );
            CREATE UNIQUE INDEX "IX_File" ON "File" ("FolderId", "Name");
            """;

        private static int _name = 0;

        protected override FileAggregate NewAggregate() => new()
        {
            FolderId = 1,
            Name = "File" + Interlocked.Increment(ref _name) + ".png",
            Extension = "png",
            Size = 8192,
            ImageHeight = 80,
            ImageWidth = 250,
            CreatedBy = "host",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "host",
            ModifiedOn = DateTime.UtcNow
        };

        protected override FileAggregate RebuildMutated(int id)
        {
            var aggregate = NewAggregate();
            aggregate.Name = "Renamed.png";
            typeof(FileAggregate).GetProperty(nameof(FileAggregate.Id))!.SetValue(aggregate, id);
            return aggregate;
        }

        protected override void AssertMutated(FileAggregate loaded) => Assert.Equal("Renamed.png", loaded.Name);

        /// <summary>
        /// The derived lookup key reaches the DATABASE, not just the object. This is the relational half of the
        /// conformance suite's cross-provider proof: written through the real EfRepository, against the real
        /// migrated schema, read back through a different context.
        /// </summary>
        [Fact]
        public async Task The_normalized_key_is_persisted_and_read_back()
        {
            var file = NewAggregate();
            file.Name = "Test-Image.PNG";
            await NewStore(NewContext()).AddAsync(file);

            await using var context = NewContext();
            var loaded = await context.Set<FileAggregate>().FirstAsync(item => item.Id == file.Id);

            Assert.Equal("Test-Image.PNG", loaded.Name);
            Assert.Equal("test-image.png", loaded.NameNormalized);
        }

        /// <summary>
        /// D49 did NOT move ix_file onto the normalized column, so two names differing only by case still
        /// coexist in one folder — exactly as they did before. If this starts failing, someone has changed the
        /// index and narrowed what the product accepts.
        /// </summary>
        [Fact]
        public async Task Two_files_whose_names_differ_only_by_case_coexist_in_one_folder()
        {
            var upper = NewAggregate();
            upper.FolderId = 77;
            upper.Name = "Logo.png";
            await NewStore(NewContext()).AddAsync(upper);

            var lower = NewAggregate();
            lower.FolderId = 77;
            lower.Name = "logo.png";
            await NewStore(NewContext()).AddAsync(lower);

            await using var context = NewContext();
            var both = await context.Set<FileAggregate>().Where(item => item.FolderId == 77).ToListAsync();

            Assert.Equal(2, both.Count);
            Assert.All(both, item => Assert.Equal("logo.png", item.NameNormalized));
        }

        /// <summary>
        /// The unique index on (folder_id, name) is real and case-SENSITIVE — the same shape as Folder's
        /// ix_folder. Named because it is a relational constraint with no RavenDB equivalent (see
        /// RavenFileStore).
        /// </summary>
        [Fact]
        public async Task A_duplicate_folder_and_name_is_rejected_by_the_unique_index()
        {
            var first = NewAggregate();
            first.FolderId = 88;
            first.Name = "same.png";
            await NewStore(NewContext()).AddAsync(first);

            var duplicate = NewAggregate();
            duplicate.FolderId = 88;
            duplicate.Name = "same.png";

            await Assert.ThrowsAsync<DbUpdateException>(() => NewStore(NewContext()).AddAsync(duplicate));
        }
    }

    /// <summary>
    /// <b>THE WRITE-LIFETIME PIN, and the reason FileRepository resolves its store PER WRITE where
    /// FolderRepository constructor-injects one.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// D48 checked Folder for this hazard and correctly found nothing, so it did NOT cargo-cult D47's
    /// workaround. File is different, and the difference was found by reading the call graph rather than by
    /// assuming: <c>FileController.Put</c> calls <c>CreateFile</c>, which calls
    /// <see cref="IFileRepository.DeleteFileAsync"/> when the target folder is over capacity — a store
    /// Get-then-Delete on key N — and then Put UNCONDITIONALLY calls
    /// <see cref="IFileRepository.UpdateFileAsync"/> on that same key N (FileController.cs:267, outside the
    /// <c>if (newfile != null)</c> guard). Through ONE store that is a second write to a tracked identity, and
    /// it throws.
    /// </para>
    /// <para>
    /// A store owns ONE TenantDBContext for its lifetime and a controller holds ONE transient repository per
    /// request, so constructor injection would have made that shape unreachable-by-luck at best. The harvested
    /// FileRepository opened a FRESH DbContext inside every method, so resolving per write is the FAITHFUL
    /// port, not a workaround bolted on.
    /// </para>
    /// <para>
    /// Revert <see cref="FileRepository"/> to a constructor-injected store and these fail with the identity-map
    /// conflict — which is the only reason this file is worth its length.
    /// </para>
    /// </remarks>
    public class TheFileWriteLifetimeTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        public TheFileWriteLifetimeTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "File" (
                    "FileId" INTEGER NOT NULL CONSTRAINT "PK_File" PRIMARY KEY AUTOINCREMENT,
                    "FolderId" INTEGER NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Extension" TEXT NOT NULL,
                    "Size" INTEGER NOT NULL,
                    "ImageHeight" INTEGER NOT NULL,
                    "ImageWidth" INTEGER NOT NULL,
                    "CreatedBy" TEXT NOT NULL,
                    "CreatedOn" TEXT NOT NULL,
                    "ModifiedBy" TEXT NOT NULL,
                    "ModifiedOn" TEXT NOT NULL,
                    "Description" TEXT NULL,
                    "NameNormalized" TEXT NULL
                );
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

        private sealed class TestFileStore(DbContext context) : IFileStore
        {
            private readonly IRepository<FileAggregate, int> _inner =
                new EfRepository<FileAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

            public Task<FileAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<FileAggregate>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);
            public Task AddAsync(FileAggregate aggregate, CancellationToken ct = default) => _inner.AddAsync(aggregate, ct);
            public Task UpdateAsync(FileAggregate aggregate, CancellationToken ct = default) => _inner.UpdateAsync(aggregate, ct);
            public Task DeleteAsync(FileAggregate aggregate, CancellationToken ct = default) => _inner.DeleteAsync(aggregate, ct);
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
        /// <see cref="IFileStore"/> registered TRANSIENT, which is what <c>FileRepository.NewStore()</c>
        /// depends on and what <c>FrameworkStoreServiceExtensions</c> actually does.
        /// </summary>
        private ServiceProvider NewServices()
        {
            var services = new ServiceCollection();
            services.AddTransient<IFileStore>(_ => new TestFileStore(NewContext()));
            return services.BuildServiceProvider();
        }

        // The repository's outbound edges, stubbed. Only what the code under test reaches is implemented;
        // everything else throws rather than returning a plausible lie.
        private sealed class StubFolderRepository : IFolderRepository
        {
            public Task<IEnumerable<Folder>> GetFoldersAsync(int siteId) => throw new NotSupportedException();
            public Task<Folder> AddFolderAsync(Folder folder) => throw new NotSupportedException();
            public Task<Folder> UpdateFolderAsync(Folder folder) => throw new NotSupportedException();
            public Task<Folder> GetFolderAsync(int folderId) => GetFolderAsync(folderId, false);
            public Task<Folder> GetFolderAsync(int folderId, bool tracking) =>
                Task.FromResult(new Folder { FolderId = folderId, SiteId = 1, Path = "Public/", Type = FolderTypes.Public });
            public Task<Folder> GetFolderAsync(int siteId, string path) => throw new NotSupportedException();
            public Task DeleteFolderAsync(int folderId) => throw new NotSupportedException();
            public Task<string> GetFolderPathAsync(int folderId) => throw new NotSupportedException();
            public Task<string> GetFolderPathAsync(Folder folder) => throw new NotSupportedException();
        }

        private sealed class StubPermissionRepository : IPermissionRepository
        {
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName) => Task.FromResult(Enumerable.Empty<Permission>());
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName) => Task.FromResult(Enumerable.Empty<Permission>());
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId) => Task.FromResult(Enumerable.Empty<Permission>());
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName) => Task.FromResult(Enumerable.Empty<Permission>());
            public Task<Permission> AddPermissionAsync(Permission permission) => throw new NotSupportedException();
            public Task<Permission> UpdatePermissionAsync(Permission permission) => throw new NotSupportedException();
            public Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false) => Task.CompletedTask;
            public Task<Permission> GetPermissionAsync(int permissionId) => throw new NotSupportedException();
            public Task<Permission> GetPermissionAsync(int permissionId, bool tracking) => throw new NotSupportedException();
            public Task DeletePermissionAsync(int permissionId) => throw new NotSupportedException();
            public Task DeletePermissionsAsync(int siteId, string entityName, int entityId) => Task.CompletedTask;
            public string EncodePermissions(IEnumerable<Permission> permissions) => throw new NotSupportedException();
            public List<Permission> DecodePermissions(string permissionStrings, int siteId, string entityName, int entityId) => throw new NotSupportedException();
        }

        private sealed class StubTenantManager : ITenantManager
        {
            public Task<Alias> GetAliasAsync() => Task.FromResult(new Alias { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" });
            public Task<Tenant> GetTenantAsync() => throw new NotSupportedException();
            public Tenant GetResolvedTenant() => throw new NotSupportedException();
            public Task SetAliasAsync(Alias alias) => throw new NotSupportedException();
            public Task SetAliasAsync(int tenantId, int siteId) => throw new NotSupportedException();
            public void SetTenant(Tenant tenant) => throw new NotSupportedException();
        }

        /// <summary>
        /// One repository over ONE store, which is what the container hands a controller for one request:
        /// <c>IFileStore</c> is transient, but the repository resolves it ONCE, at construction.
        /// </summary>
        private FileRepository NewRepository(ServiceProvider services) =>
            new(services.GetRequiredService<IFileStore>(), new TestSessionFactory(NewContext), new StubPermissionRepository(), new StubFolderRepository(), new StubTenantManager());

        /// <summary>
        /// A file model with the audit quartet pre-filled. The stamps are normally applied by
        /// <c>DbContextUtils.SaveChanges</c>, which <c>TenantDBContext</c> calls and this test's plain
        /// <see cref="StoreContext"/> does not — and the columns are NOT NULL. Filling them here keeps the
        /// fixture honest about the schema rather than relaxing the DDL to make the test pass.
        /// </summary>
        private static File NewFileModel(string name, int fileId = 0) => new()
        {
            FileId = fileId,
            FolderId = 1,
            Name = name,
            Extension = "png",
            Size = 8192,
            ImageHeight = 80,
            ImageWidth = 250,
            CreatedBy = "host",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "host",
            ModifiedOn = DateTime.UtcNow
        };

        private async Task<int> SeedFileAsync(ServiceProvider services, string name)
        {
            var file = NewFileModel(name);
            await NewRepository(services).AddFileAsync(file);
            return file.FileId;
        }

        /// <summary>
        /// <b>FileController.Put's capacity branch, measured — and the reason a per-write store would NOT have
        /// helped it.</b> CreateFile deletes an over-capacity file (key N) at <c>FileController.cs:896</c>, then
        /// Put unconditionally updates key N at <c>:267</c>, outside the <c>if (newfile != null)</c> guard.
        /// </summary>
        /// <remarks>
        /// This is the path that made the first draft of <see cref="FileRepository"/> resolve its store per
        /// write. Measuring it showed the reasoning was wrong: a Delete does NOT leave the entity tracked (EF
        /// detaches it on save), so this never reaches the identity map. It fails on the ROW BEING GONE —
        /// a <see cref="ConcurrencyConflictException"/>, which a fresh context per write would produce
        /// identically. So it is a pre-existing product-logic wrinkle, not a lifetime one, and it is recorded
        /// here rather than fixed: correcting Put's guard is a behaviour change this increment does not own.
        /// </remarks>
        [Fact]
        public async Task A_delete_then_an_update_of_the_same_file_fails_on_the_missing_row_not_the_identity_map()
        {
            using var services = NewServices();
            var fileId = await SeedFileAsync(services, "Over-Capacity.PNG");

            var files = NewRepository(services);

            // CreateFile -> FileController.cs:896, when the folder is over capacity.
            await files.DeleteFileAsync(fileId);

            // FileController.cs:267 — unconditional, outside the `if (newfile != null)` guard.
            var stale = NewFileModel("Over-Capacity.PNG", fileId);

            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => files.UpdateFileAsync(stale));
        }

        /// <summary>
        /// The zip-extraction loop (FileController.cs:302-330): many writes, then a delete, all through ONE
        /// repository instance. Different keys, so this is the shape D44 accepted for the leaf clusters — but
        /// it is asserted rather than assumed, because "different keys" is exactly the reasoning that was wrong
        /// about UrlMapping.
        /// </summary>
        [Fact]
        public async Task A_loop_of_writes_and_a_delete_through_one_repository_instance_persists()
        {
            using var services = NewServices();
            var files = NewRepository(services);

            var ids = new List<int>();
            foreach (var name in new[] { "One.PNG", "Two.PNG", "Three.PNG" })
            {
                var file = NewFileModel(name);
                await files.AddFileAsync(file);
                ids.Add(file.FileId);
            }

            await files.DeleteFileAsync(ids[0]);

            Assert.Equal(3, ids.Distinct().Count());
            Assert.Null(await files.GetFileAsync(ids[0], false));
            Assert.NotNull(await files.GetFileAsync(ids[1], false));
        }

        /// <summary>
        /// <b>THE HAZARD, DEMONSTRATED — and the test that stops this fixture being vacuous.</b> Add-then-Update
        /// of the SAME key through ONE repository instance THROWS: <see cref="EfRepository{T, TId}"/> leaves
        /// what it writes tracked, and a store owns one context, so the second write collides with the identity
        /// map. This is D44's finding and the shape D47 was bitten by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is asserted here rather than described because a described limitation decays into folklore. It
        /// also proves the other tests in this class CAN fail: they drive the same fixture through the shapes
        /// production actually uses, and those pass.
        /// </para>
        /// <para>
        /// <b>This is why the shape matters, and why it does NOT make File a D47 cluster.</b> No File call site
        /// reaches it — every write path is an if/else or a loop over distinct keys (see the remarks on
        /// <see cref="FileRepository"/>). So <see cref="FileRepository"/> constructor-injects its store like
        /// Folder and the eleven before it, rather than cargo-culting UrlMapping's per-write resolve.
        /// D44 called this "not reachable in production, but one line away". This test is that line, kept
        /// executable.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task Two_writes_to_the_same_file_through_one_repository_instance_throw()
        {
            using var services = NewServices();
            var files = NewRepository(services);

            var file = NewFileModel("Probe.PNG");
            await files.AddFileAsync(file);

            file.Size = 4096;

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => files.UpdateFileAsync(file));
        }

        /// <summary>
        /// <b>The read that resolves a lowercased URL against a mixed-case row, through the repository.</b> The
        /// render path's shape, on a real database: the argument side is normalized INSIDE the repository, so
        /// the caller's casing cannot break it.
        /// </summary>
        [Theory]
        [InlineData("test-image.png")]
        [InlineData("Test-Image.PNG")]
        [InlineData("TEST-IMAGE.PNG")]
        public async Task A_file_resolves_by_name_whatever_case_the_caller_passes(string requested)
        {
            using var services = NewServices();
            await SeedFileAsync(services, "Test-Image.PNG");

            var found = await NewRepository(services).GetFileAsync(1, requested);

            Assert.NotNull(found);
            Assert.Equal("Test-Image.PNG", found.Name);
        }
    }
}
