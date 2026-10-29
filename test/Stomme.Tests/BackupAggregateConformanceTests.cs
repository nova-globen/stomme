#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup.Persistence;
using Stomme.Modules.Backup.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The backup catalogue round-trips member for member on EVERY module-aggregate store (EF SQLite,
    /// EF PostgreSQL, EF SQL Server, RavenDB, File) through the module's own startup: the three value objects as scalars, the
    /// nullable instants, UTC <c>Kind</c>; each transition read in one scope and written in the next persists; the status
    /// filter and the orderings answer identically; a delete removes. PostgreSQL, SQL Server and RavenDB FAIL rather than
    /// skip. the predecessor platform's in-memory repository and query facts land here, on the real stores.
    /// </summary>
    public abstract class BackupAggregateConformanceTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        protected abstract string ExpectedStore { get; }

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            services.AddHttpContextAccessor();
            new Stomme.Modules.Backup.Startup.ServerStartup().ConfigureServices(services);
            _services = services.BuildServiceProvider();
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await DisposeStoreAsync();
        }

        private async Task<T> Snapshots<T>(Func<IBackupSnapshotRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IBackupSnapshotRepository>());
        }

        private async Task<T> Schedules<T>(Func<IBackupScheduleRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IBackupScheduleRepository>());
        }

        private Task Add(BackupSnapshotAggregate snapshot) => Snapshots(async r => { await r.AddAsync(snapshot); return 0; });

        private Task Save(BackupSnapshotAggregate snapshot) => Snapshots(async r => { await r.UpdateAsync(snapshot); return 0; });

        // Whole milliseconds: SQL Server's datetime2 and PostgreSQL's timestamp keep microseconds, not ticks.
        private static DateTime UtcNow() => DateTime.UnixEpoch.AddMilliseconds(Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds));

        private static readonly Checksum Sum = Checksum.Compute(new byte[] { 4, 5, 6 });

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<BackupSnapshotAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<BackupSnapshotAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_empty_catalogue_lists_nothing_and_an_unknown_id_is_null()
        {
            Assert.Empty(await Snapshots(r => r.ListAsync()));
            Assert.Null(await Snapshots(r => r.GetByIdAsync(Guid.NewGuid())));
        }

        [Fact]
        public async Task A_creating_snapshot_round_trips_its_value_objects_and_nulls()
        {
            var now = UtcNow();
            var snapshot = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.PreRestore, "10.2.3", now);
            await Add(snapshot);

            var read = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            Assert.Equal((BackupSnapshotStatus.Creating, snapshot.StorageLocation, RetentionTag.PreRestore, "10.2.3"), (read.Status, read.StorageLocation, read.RetentionTag, read.FrameworkVersion));
            Assert.Equal((now, DateTimeKind.Utc), (read.CreatedOnUtc, read.CreatedOnUtc.Kind));
            Assert.Null(read.CompletedOnUtc);
            Assert.Null(read.StagedOnUtc);
            Assert.Null(read.LastRestoredOnUtc);
            Assert.Null(read.GetChecksum());
            Assert.Equal((string.Empty, string.Empty, 0L), (read.ArchiveSha256, read.FailureReason, read.SizeBytes));
        }

        [Fact]
        public async Task Completed_staged_restoring_and_failed_restore_persist_across_scopes()
        {
            var now = UtcNow();
            var snapshot = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now);
            await Add(snapshot);

            var loaded = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            loaded.MarkCompleted(Sum, 5_000_000_000, now.AddMinutes(2));
            await Save(loaded);
            var available = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            Assert.Equal((BackupSnapshotStatus.Available, Sum, 5_000_000_000L, (DateTime?)now.AddMinutes(2)), (available.Status, available.GetChecksum(), available.SizeBytes, available.CompletedOnUtc));
            Assert.Equal(DateTimeKind.Utc, available.CompletedOnUtc!.Value.Kind);

            available.Stage(Sum, now.AddHours(1));
            await Save(available);
            var staged = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            Assert.Equal((BackupSnapshotStatus.Staged, (DateTime?)now.AddHours(1)), (staged.Status, staged.StagedOnUtc));

            staged.BeginRestore();
            await Save(staged);
            var restoring = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            restoring.FailRestore("InvalidOperationException");
            await Save(restoring);
            var failed = (await Snapshots(r => r.GetByIdAsync(snapshot.Id)))!;
            Assert.Equal((BackupSnapshotStatus.RestoreFailed, "InvalidOperationException", (DateTime?)null), (failed.Status, failed.FailureReason, failed.StagedOnUtc));
        }

        [Fact]
        public async Task The_status_filter_and_the_orderings_answer_and_a_delete_removes()
        {
            var now = UtcNow();
            var oldest = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Scheduled, "10.2.3", now.AddDays(-3));
            oldest.MarkCompleted(Sum, 10, now.AddDays(-3));
            var middle = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Scheduled, "10.2.3", now.AddDays(-2));
            middle.Fail("IOException", now.AddDays(-2));
            var newest = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now.AddDays(-1));
            newest.MarkCompleted(Sum, 10, now.AddDays(-1));
            var creating = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now);
            foreach (var snapshot in new[] { middle, creating, oldest, newest })
            {
                await Add(snapshot);
            }

            Assert.Equal(new[] { creating.Id, newest.Id, middle.Id, oldest.Id }, (await Snapshots(r => r.ListAsync())).Select(s => s.Id));
            Assert.Equal(new[] { oldest.Id, newest.Id }, (await Snapshots(r => r.ListByStatusAsync(BackupSnapshotStatus.Available))).Select(s => s.Id));
            Assert.Equal(middle.Id, Assert.Single(await Snapshots(r => r.ListByStatusAsync(BackupSnapshotStatus.Failed))).Id);
            Assert.Empty(await Snapshots(r => r.ListByStatusAsync(BackupSnapshotStatus.Staged)));

            var doomed = (await Snapshots(r => r.GetByIdAsync(middle.Id)))!;
            await Snapshots(async r => { await r.DeleteAsync(doomed); return 0; });
            Assert.Null(await Snapshots(r => r.GetByIdAsync(middle.Id)));
            Assert.Equal(3, (await Snapshots(r => r.ListAsync())).Count);
        }

        [Fact]
        public async Task A_schedule_round_trips_and_its_last_run_persists()
        {
            var now = UtcNow();
            var schedule = BackupScheduleAggregate.Create(intervalHours: 24, retentionDays: 30, keepLast: 3, enabled: true, now);
            await Schedules(async r => { await r.AddAsync(schedule); return 0; });

            var loaded = (await Schedules(r => r.GetByIdAsync(schedule.Id)))!;
            Assert.Equal((true, 24, 30, 3, now, (DateTime?)null), (loaded.Enabled, loaded.IntervalHours, loaded.RetentionDays, loaded.KeepLast, loaded.CreatedOnUtc, loaded.LastRunOnUtc));
            loaded.MarkRun(now.AddHours(1));
            loaded.Redefine(12, 7, 2, enabled: false);
            await Schedules(async r => { await r.UpdateAsync(loaded); return 0; });

            var read = Assert.Single(await Schedules(r => r.ListAsync()));
            Assert.Equal((false, 12, 7, 2, (DateTime?)now.AddHours(1)), (read.Enabled, read.IntervalHours, read.RetentionDays, read.KeepLast, read.LastRunOnUtc));
            Assert.Equal(DateTimeKind.Utc, read.LastRunOnUtc!.Value.Kind);
        }
    }
    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteBackupAggregateConformanceTests : BackupAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-backup-conformance-{Guid.NewGuid():N}.db")}";

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = _connectionString,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            try
            {
                File.Delete(new SqliteConnectionStringBuilder(_connectionString).DataSource);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>EF Core on a LIVE PostgreSQL scratch database. Fails, never skips, when PostgreSQL is down.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresBackupAggregateConformanceTests : BackupAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_backup_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The backup catalogue conformance suite FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = PostgresTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(PostgresTestEndpoint.ConnectionString(_database)));
            PostgresTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// EF Core on a LIVE SQL Server scratch database (infra UAT, <see cref="SqlServerTestEndpoint"/>) - the first module-aggregate
    /// suite on SQL Server, as every module-aggregate suite. Fails, never skips, when SQL Server is down.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerBackupAggregateConformanceTests : BackupAggregateConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_backup_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The backup catalogue conformance suite FAILS rather than skips (D46).");
            return Task.CompletedTask;
        }

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = SqlServerTestEndpoint.ConnectionString(_database),
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearPool(new Microsoft.Data.SqlClient.SqlConnection(SqlServerTestEndpoint.ConnectionString(_database)));
            SqlServerTestEndpoint.DropDatabase(_database);
            return Task.CompletedTask;
        }
    }

    /// <summary>The generic RavenRepository on a LIVE RavenDB, its own database per test. Fails, never skips.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenBackupAggregateConformanceTests : BackupAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-backup-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-backup-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server",
                })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            // Delete through a FRESH store: the container already disposed the shared one.
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
                // best-effort: the next run's janitor sweep removes a leftover
            }
        }
    }

    /// <summary>The generic FileRepository in a throwaway directory - the provider these aggregates had never met.</summary>
    public sealed class FileBackupAggregateConformanceTests : BackupAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-backup-file-conformance-{Guid.NewGuid():N}");

        protected override void AddStore(IServiceCollection services) =>
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.File.FileDatabase, Stomme.Server",
                    ["ModuleStore:FilePath"] = _root,
                })
                .Build());

        protected override Task DisposeStoreAsync()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }

            return Task.CompletedTask;
        }
    }
}
