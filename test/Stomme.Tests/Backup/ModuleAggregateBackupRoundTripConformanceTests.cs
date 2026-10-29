#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Modules.Portability.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// <c>ModuleAggregateBackupRoundTripConformanceTests</c> - the participant every storage-blind
    /// aggregate gets from <c>AddModuleAggregatePersistence</c>, driven through the real archive on EF SQLite, EF PostgreSQL,
    /// RavenDB and File, with the HOST's tenant context and two tenants: a snapshot is taken; a row is then ADDED, one
    /// DELETED and one MUTATED; the restore puts back exactly what every scope saw at the snapshot - the added row GONE, the
    /// deleted one back, the mutation reverted. A module-scoped aggregate (Guestbook - partitioned per tenant on File only)
    /// and a tenant-OWNED one (Portability packages) both ride it. PostgreSQL and RavenDB FAIL rather than skip.
    /// </summary>
    public abstract class ModuleAggregateBackupRoundTripConformanceTests : IAsyncLifetime
    {
        private const string Passphrase = "round trip passphrase";
        private static readonly Guid TenantOne = FrameworkTenantId.ToTenantId(1).Value;
        private static readonly Guid TenantTwo = FrameworkTenantId.ToTenantId(2).Value;
        private static readonly BackupScope Installation = new("10.2.3", new[] { 1, 2 });

        private ServiceProvider _services = default!;

        protected abstract string ExpectedStore { get; }

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
            AddStore(services);
            services.AddHttpContextAccessor();

            // The host's tenancy, not the module fallback: the ambient scope decides, and the File store partitions by it.
            services.AddScoped<SiteState>();
            services.AddScoped<IAliasAccessor, AliasAccessor>();
            services.AddScoped<ITenantContext, StommeTenantContext>();
            services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();

            new Stomme.Modules.Guestbook.Startup.ServerStartup().ConfigureServices(services);
            new Stomme.Modules.Portability.Startup.ServerStartup().ConfigureServices(services);
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

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            var name = await In(null, sp =>
            {
                var inner = sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>();
                return Task.FromResult(inner is Stomme.Persistence.EfCore.EfRepository<GuestbookEntryAggregate, Guid>
                    ? sp.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? string.Empty
                    : inner.GetType().Name);
            });
            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_restore_puts_back_exactly_what_every_scope_saw_at_the_snapshot()
        {
            var g1 = GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Ada", "first");
            var g2 = GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Grace", "second");
            var g3 = GuestbookEntryAggregate.Sign(TenantId.From(TenantTwo), 6, "Barbara", "third");
            var now = DateTime.UnixEpoch.AddMilliseconds(Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds));
            var p1 = PortabilityPackageAggregate.StartBuild(FrameworkTenantId.ToTenantId(1), 1, PortabilityPackageKind.ConfigTransfer, 1, null, now);
            var p2 = PortabilityPackageAggregate.StartBuild(FrameworkTenantId.ToTenantId(2), 3, PortabilityPackageKind.ConfigTransfer, 1, null, now);
            await In(TenantOne, async sp => { await Add(sp, g1); await Add(sp, g2); await Add(sp, p1); });
            await In(TenantTwo, async sp => { await Add(sp, g3); await Add(sp, p2); });

            var before = await VisibleAsync();
            var archive = await SnapshotAsync();

            // After the snapshot: one added, one deleted, one mutated - in each tenant.
            await In(TenantOne, async sp =>
            {
                await Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Late", "after the snapshot"));
                await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().DeleteAsync(g2);
            });
            await In(TenantTwo, async sp =>
            {
                var packages = sp.GetRequiredService<IRepository<PortabilityPackageAggregate, Guid>>();
                var loaded = (await packages.GetByIdAsync(p2.Id))!;
                loaded.MarkFailed("InvalidOperationException", now, now.AddDays(7));
                await packages.UpdateAsync(loaded);
                await Add(sp, PortabilityPackageAggregate.StartBuild(FrameworkTenantId.ToTenantId(2), 3, PortabilityPackageKind.ConfigTransfer, 1, null, now));
            });
            Assert.NotEqual(before, await VisibleAsync());

            await RestoreAsync(archive, Installation);

            Assert.Equal(before, await VisibleAsync());
            var restored = await In(TenantTwo, sp => sp.GetRequiredService<IRepository<PortabilityPackageAggregate, Guid>>().GetByIdAsync(p2.Id));
            Assert.Equal(PortabilityPackageStatus.Building, restored!.Status);
            Assert.Equal(string.Empty, restored.FailureText);
        }

        [Fact]
        public void Every_storage_blind_aggregate_has_a_participant_and_the_catalogue_has_none()
        {
            var names = Participants().Select(participant => participant.Name).ToList();
            Assert.Contains("module-aggregate:" + typeof(GuestbookEntryAggregate).FullName, names);
            Assert.Contains("module-aggregate:" + typeof(PortabilityPackageAggregate).FullName, names);
            Assert.DoesNotContain(names, name => name.Contains("Backup", StringComparison.Ordinal));
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task A_part_filed_under_a_tenant_outside_the_installation_is_refused_before_anything_is_written()
        {
            var now = DateTime.UtcNow;
            await In(TenantTwo, sp => Add(sp, PortabilityPackageAggregate.StartBuild(FrameworkTenantId.ToTenantId(2), 3, PortabilityPackageKind.ConfigTransfer, 1, null, now)));
            var archive = await SnapshotAsync();
            await In(TenantOne, sp => Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Kept", "must survive a refused restore")));
            var before = await VisibleAsync();

            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            var onlyTenantOne = new BackupScope("10.2.3", new[] { 1 });
            var packages = Participants().Single(p => p.Name.EndsWith(nameof(PortabilityPackageAggregate), StringComparison.Ordinal));
            var verdict = await packages.ValidateAsync(new ArchivePartReader(reader, reader.Manifest.Parts.Single(part => part.Name == packages.Name)), onlyTenantOne, CancellationToken.None);
            Assert.False(verdict.IsValid);
            Assert.Contains(verdict.Problems, problem => problem.Contains("not in the snapshot", StringComparison.Ordinal));
            Assert.Equal(before, await VisibleAsync());
        }

        [Fact]
        public async Task A_part_with_a_repeated_row_or_a_row_filed_under_another_tenant_is_refused_by_validation()
        {
            var now = DateTime.UtcNow;
            await In(TenantOne, sp => Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Ada", "one")));
            await In(TenantTwo, sp => Add(sp, PortabilityPackageAggregate.StartBuild(FrameworkTenantId.ToTenantId(2), 3, PortabilityPackageKind.ConfigTransfer, 1, null, now)));
            var archive = await SnapshotAsync();

            var guestbook = await PartBytesAsync(archive, typeof(GuestbookEntryAggregate));
            var repeated = await ValidateAsync(typeof(GuestbookEntryAggregate), guestbook.Concat(guestbook).ToArray());
            Assert.Contains(repeated.Problems, problem => problem.Contains("repeats id", StringComparison.Ordinal));

            var misfiled = await ValidateAsync(typeof(PortabilityPackageAggregate), await RefileAsync(await PartBytesAsync(archive, typeof(PortabilityPackageAggregate)), TenantOne));
            Assert.Contains(misfiled.Problems, problem => problem.Contains("other than the one it is filed under", StringComparison.Ordinal));
        }

        /// <summary>A replace that throws half-way on a RELATIONAL store leaves the rows as they were: one transaction.</summary>
        protected async Task A_replace_that_fails_midway_leaves_the_rows_as_they_were_core()
        {
            await In(TenantOne, async sp => { await Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Ada", "one")); await Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Grace", "two")); });
            var archive = await SnapshotAsync();
            await In(TenantOne, sp => Add(sp, GuestbookEntryAggregate.Sign(TenantId.From(TenantOne), 5, "Late", "after the snapshot")));
            var before = await VisibleAsync();

            // The part twice over: the second copy of the first row collides on its key after every row was deleted.
            var guestbook = await PartBytesAsync(archive, typeof(GuestbookEntryAggregate));
            var participant = Participant(typeof(GuestbookEntryAggregate));
            var (reader, part) = await OnePartArchiveAsync(participant, guestbook.Concat(guestbook).ToArray());
            using (reader)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => participant.ReplaceAsync(part, Installation, CancellationToken.None));
            }

            Assert.Equal(before, await VisibleAsync());
        }

        private IBackupParticipant Participant(Type aggregate) =>
            Participants().Single(participant => participant.Name == "module-aggregate:" + aggregate.FullName);

        private static async Task<byte[]> PartBytesAsync(byte[] archive, Type aggregate)
        {
            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            await using var part = reader.OpenPart("module-aggregate:" + aggregate.FullName);
            using var copy = new MemoryStream();
            await part.CopyToAsync(copy);
            return copy.ToArray();
        }

        private static async Task<byte[]> RefileAsync(byte[] part, Guid tenant)
        {
            using var output = new MemoryStream();
            await foreach (var (_, json) in Stomme.Infrastructure.Backup.BackupRecords.ReadAsync(new MemoryStream(part), CancellationToken.None))
            {
                await Stomme.Infrastructure.Backup.BackupRecords.WriteAsync(output, tenant, json, CancellationToken.None);
            }

            return output.ToArray();
        }

        private async Task<BackupPartValidation> ValidateAsync(Type aggregate, byte[] part)
        {
            var participant = Participant(aggregate);
            var (reader, archivePart) = await OnePartArchiveAsync(participant, part);
            using (reader)
            {
                return await participant.ValidateAsync(archivePart, Installation, CancellationToken.None);
            }
        }

        private static async Task<(BackupArchiveReader Reader, ArchivePartReader Part)> OnePartArchiveAsync(IBackupParticipant participant, byte[] content)
        {
            var output = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(output, Passphrase))
            {
                await using (var part = writer.OpenPart(participant.Name, participant.SchemaVersion, "test"))
                {
                    await part.WriteAsync(content);
                }

                await writer.CompleteAsync(Installation.FrameworkVersion, Installation.TenantIds, DateTime.UtcNow);
            }

            output.Position = 0;
            var reader = await BackupArchiveReader.OpenAsync(output, Passphrase);
            return (reader, new ArchivePartReader(reader, reader.Manifest.Parts.Single()));
        }
        private IReadOnlyList<IBackupParticipant> Participants()
        {
            var scope = _services.CreateScope();
            return scope.ServiceProvider.GetServices<IBackupParticipant>().ToList();
        }

        private async Task<byte[]> SnapshotAsync()
        {
            using var output = new MemoryStream();
            await using (var writer = await BackupArchiveWriter.CreateAsync(output, Passphrase))
            {
                foreach (var participant in Participants().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    await using var part = writer.OpenPart(participant.Name, participant.SchemaVersion, "test");
                    await participant.ExportAsync(new ArchivePartWriter(participant.Name, part), Installation, CancellationToken.None);
                }

                await writer.CompleteAsync(Installation.FrameworkVersion, Installation.TenantIds, DateTime.UtcNow);
            }

            return output.ToArray();
        }

        private async Task RestoreAsync(byte[] archive, BackupScope scope)
        {
            using var reader = await BackupArchiveReader.OpenAsync(new MemoryStream(archive), Passphrase);
            var participants = Participants();
            foreach (var participant in participants)
            {
                var verdict = await participant.ValidateAsync(new ArchivePartReader(reader, reader.Manifest.Parts.Single(part => part.Name == participant.Name)), scope, CancellationToken.None);
                Assert.True(verdict.IsValid, string.Join("; ", verdict.Problems));
            }

            foreach (var participant in participants.OrderBy(p => p.RestoreOrder).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                await participant.ReplaceAsync(new ArchivePartReader(reader, reader.Manifest.Parts.Single(part => part.Name == participant.Name)), scope, CancellationToken.None);
            }
        }

        /// <summary>What each scope sees - system, tenant one, tenant two - as sorted "type:id:detail" lines.</summary>
        private async Task<string> VisibleAsync()
        {
            var lines = new List<string>();
            foreach (var (label, tenant) in new[] { ("system", (Guid?)null), ("one", TenantOne), ("two", TenantTwo) })
            {
                await In(tenant, async sp =>
                {
                    lines.AddRange((await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().GetAllAsync())
                        .Select(g => $"{label} guestbook {g.Id} {g.Author} {g.Message}"));
                    lines.AddRange((await sp.GetRequiredService<IRepository<PortabilityPackageAggregate, Guid>>().GetAllAsync())
                        .Select(p => $"{label} package {p.Id} {p.TenantId.Value} {p.Status} {p.FailureText}"));
                });
            }

            lines.Sort(StringComparer.Ordinal);
            return string.Join(Environment.NewLine, lines);
        }

        private async Task In(Guid? tenant, Func<IServiceProvider, Task> action)
        {
            using var ambient = _services.GetRequiredService<ITenantScopeFactory>().BeginScope(tenant);
            await using var scope = _services.CreateAsyncScope();
            await action(scope.ServiceProvider);
        }

        private async Task<T> In<T>(Guid? tenant, Func<IServiceProvider, Task<T>> action)
        {
            using var ambient = _services.GetRequiredService<ITenantScopeFactory>().BeginScope(tenant);
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        private static Task Add<T>(IServiceProvider provider, T aggregate) where T : AggregateRoot<Guid> =>
            provider.GetRequiredService<IRepository<T, Guid>>().AddAsync(aggregate);
    }
    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteModuleAggregateBackupRoundTripConformanceTests : ModuleAggregateBackupRoundTripConformanceTests
    {
        [Fact]
        public Task A_replace_that_fails_midway_leaves_the_rows_as_they_were() => A_replace_that_fails_midway_leaves_the_rows_as_they_were_core();

        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-backup-roundtrip-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresModuleAggregateBackupRoundTripConformanceTests : ModuleAggregateBackupRoundTripConformanceTests
    {
        [Fact]
        public Task A_replace_that_fails_midway_leaves_the_rows_as_they_were() => A_replace_that_fails_midway_leaves_the_rows_as_they_were_core();

        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_backup_roundtrip_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The backup round-trip suite FAILS rather than skips (D46).");
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

    /// <summary>The generic RavenRepository on a LIVE RavenDB, its own database per test. Fails, never skips.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenModuleAggregateBackupRoundTripConformanceTests : ModuleAggregateBackupRoundTripConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-backup-roundtrip-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-backup-roundtrip-");
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
    public sealed class FileModuleAggregateBackupRoundTripConformanceTests : ModuleAggregateBackupRoundTripConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-backup-roundtrip-file-{Guid.NewGuid():N}");

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
