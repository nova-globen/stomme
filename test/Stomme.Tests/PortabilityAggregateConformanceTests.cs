#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Portability.Persistence;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
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
    /// BL-82 (D107 item 5) - the portability package and audit aggregates round-trip member for member on EVERY
    /// module-aggregate store (EF SQLite, EF PostgreSQL, EF SQL Server, RavenDB, File) through the module's own startup:
    /// <c>TenantId</c> STAMPED, the nullable ints and instants, UTC <c>Kind</c>; each status transition read in one scope
    /// and written in the next persists; and the five filters (site, status, kind by way of subject, subject, expiry)
    /// answer identically. PostgreSQL, SQL Server and RavenDB FAIL rather than skip (D46).
    /// </summary>
    public abstract class PortabilityAggregateConformanceTests : IAsyncLifetime
    {
        private const int Site = 7;
        private const int OtherSite = 8;

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
            new Stomme.Modules.Portability.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> Packages<T>(Func<IPortabilityPackageRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IPortabilityPackageRepository>());
        }

        private async Task<T> Audit<T>(Func<IPortabilityAuditRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IPortabilityAuditRepository>());
        }

        private Task Add(PortabilityPackageAggregate package) => Packages(async r => { await r.AddAsync(package); return 0; });

        private Task Save(PortabilityPackageAggregate package) => Packages(async r => { await r.UpdateAsync(package); return 0; });

        // Whole milliseconds: SQL Server's datetime2 and PostgreSQL's timestamp keep microseconds, not ticks.
        private static DateTime UtcNow() => DateTime.UnixEpoch.AddMilliseconds(Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds));

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<PortabilityPackageAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<PortabilityPackageAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_building_package_round_trips_with_its_nulls_and_its_TenantId_stamped()
        {
            var now = UtcNow();
            var package = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.SubjectData, requestedByUserId: 3, subjectUserId: 42, now);
            await Add(package);

            var read = (await Packages(r => r.FindForSiteAsync(Site, package.Id)))!;
            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal((Site, PortabilityPackageKind.SubjectData, PortabilityPackageStatus.Building, 3, (int?)42), (read.SiteId, read.Kind, read.Status, read.RequestedByUserId, read.SubjectUserId));
            Assert.Equal(now, read.CreatedOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.CreatedOnUtc.Kind);
            Assert.Null(read.ArtifactFileId);
            Assert.Null(read.ReadyOnUtc);
            Assert.Null(read.FailedOnUtc);
            Assert.Null(read.ExpiresOnUtc);
            Assert.Equal((string.Empty, string.Empty, string.Empty, 0), (read.BundleFormat, read.ManifestSummary, read.FailureText, read.EntityCount));

            // Another site's id answers exactly as "no such package".
            Assert.Null(await Packages(r => r.FindForSiteAsync(OtherSite, package.Id)));
        }

        [Fact]
        public async Task Ready_then_Expired_persist_across_scopes()
        {
            var now = UtcNow();
            var package = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now);
            await Add(package);

            var loaded = (await Packages(r => r.FindForSiteAsync(Site, package.Id)))!;
            loaded.MarkReady(artifactFileId: 91, "1.1", entityCount: 4, "[{\"t\":\"page\",\"v\":1,\"n\":4}]", now.AddMinutes(1), now.AddDays(7));
            await Save(loaded);

            var ready = (await Packages(r => r.FindForSiteAsync(Site, package.Id)))!;
            Assert.Equal((PortabilityPackageStatus.Ready, (int?)91, "1.1", 4), (ready.Status, ready.ArtifactFileId, ready.BundleFormat, ready.EntityCount));
            Assert.Equal("[{\"t\":\"page\",\"v\":1,\"n\":4}]", ready.ManifestSummary);
            Assert.Equal(now.AddMinutes(1), ready.ReadyOnUtc);
            Assert.Equal(now.AddDays(7), ready.ExpiresOnUtc);
            Assert.Equal(DateTimeKind.Utc, ready.ExpiresOnUtc!.Value.Kind);
            Assert.Null(ready.SubjectUserId);

            ready.MarkExpired();
            await Save(ready);
            var expired = (await Packages(r => r.FindForSiteAsync(Site, package.Id)))!;
            Assert.Equal(PortabilityPackageStatus.Expired, expired.Status);
            Assert.Null(expired.ArtifactFileId);
        }

        [Fact]
        public async Task A_failure_keeps_its_reason_and_the_import_record_is_created_final()
        {
            var now = UtcNow();
            var failing = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now);
            await Add(failing);
            var loaded = (await Packages(r => r.FindForSiteAsync(Site, failing.Id)))!;
            loaded.MarkFailed("InvalidOperationException", now, now.AddDays(7));
            await Save(loaded);

            var failed = (await Packages(r => r.FindForSiteAsync(Site, failing.Id)))!;
            Assert.Equal((PortabilityPackageStatus.Failed, "InvalidOperationException", (DateTime?)now), (failed.Status, failed.FailureText, failed.FailedOnUtc));

            var import = PortabilityPackageAggregate.RecordImport(TenantId.Default, Site, 3, "1.0", 2, "[]", now);
            await Add(import);
            var imported = (await Packages(r => r.FindForSiteAsync(Site, import.Id)))!;
            Assert.Equal((PortabilityPackageStatus.Imported, "1.0", 2), (imported.Status, imported.BundleFormat, imported.EntityCount));
        }

        [Fact]
        public async Task The_filters_answer_by_site_status_subject_and_expiry()
        {
            var now = UtcNow();
            var queuedOld = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now.AddMinutes(-30));
            var queuedNew = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now.AddMinutes(-5));
            var adas = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.SubjectData, 3, 42, now.AddMinutes(-20));
            var due = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now.AddDays(-10));
            due.MarkReady(1, "1.1", 1, "[]", now.AddDays(-10), now.AddDays(-3));
            var alreadyExpired = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now.AddDays(-10));
            alreadyExpired.MarkReady(2, "1.1", 1, "[]", now.AddDays(-10), now.AddDays(-3));
            alreadyExpired.MarkExpired();
            var notYet = PortabilityPackageAggregate.StartBuild(TenantId.Default, Site, PortabilityPackageKind.ConfigTransfer, 3, null, now.AddDays(-1));
            notYet.MarkReady(3, "1.1", 1, "[]", now.AddDays(-1), now.AddDays(6));
            var elsewhere = PortabilityPackageAggregate.StartBuild(TenantId.Default, OtherSite, PortabilityPackageKind.SubjectData, 3, 42, now);
            foreach (var package in new[] { queuedOld, queuedNew, adas, due, alreadyExpired, notYet, elsewhere })
            {
                await Add(package);
            }

            // The build queue - oldest first.
            Assert.Equal(new[] { queuedOld.Id, adas.Id, queuedNew.Id }, (await Packages(r => r.ListByStatusAsync(Site, PortabilityPackageStatus.Building))).Select(p => p.Id));
            Assert.Equal(adas.Id, Assert.Single(await Packages(r => r.ListForSubjectAsync(Site, 42))).Id);
            Assert.Equal(due.Id, Assert.Single(await Packages(r => r.ListDueForExpiryAsync(Site, now))).Id);
            Assert.Equal(6, (await Packages(r => r.ListForSiteAsync(Site, 50))).Count);
            Assert.Equal(queuedNew.Id, Assert.Single(await Packages(r => r.ListForSiteAsync(Site, 1))).Id);
        }

        [Fact]
        public async Task The_audit_round_trips_windows_by_site_and_is_append_only()
        {
            var now = UtcNow();
            var packageId = Guid.NewGuid();
            var entry = PortabilityAuditEntryAggregate.Record(TenantId.Default, Site, packageId, PortabilityAuditAction.ImportRefused, PortabilityPackageKind.ConfigTransfer, 3, 7, null, now);
            var subjectLine = PortabilityAuditEntryAggregate.Record(TenantId.Default, Site, null, PortabilityAuditAction.BuildRequested, PortabilityPackageKind.SubjectData, 0, 7, 42, now.AddMinutes(-1));
            var old = PortabilityAuditEntryAggregate.Record(TenantId.Default, Site, null, PortabilityAuditAction.Built, PortabilityPackageKind.ConfigTransfer, 1, 7, null, now.AddDays(-400));
            var elsewhere = PortabilityAuditEntryAggregate.Record(TenantId.Default, OtherSite, null, PortabilityAuditAction.Built, PortabilityPackageKind.ConfigTransfer, 1, 7, null, now);
            foreach (var line in new[] { entry, subjectLine, old, elsewhere })
            {
                await Audit(async r => { await r.AddAsync(line); return 0; });
            }

            var read = (await Audit(r => r.GetByIdAsync(entry.Id)))!;
            Assert.Equal((TenantId.Default, Site, (Guid?)packageId, PortabilityAuditAction.ImportRefused, PortabilityPackageKind.ConfigTransfer, 3, 7, (int?)null), (read.TenantId, read.SiteId, read.PackageId, read.Action, read.Kind, read.EntityCount, read.RequestedByUserId, read.SubjectUserId));
            Assert.Equal(now, read.OccurredOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.OccurredOnUtc.Kind);
            Assert.Equal((int?)42, (await Audit(r => r.GetByIdAsync(subjectLine.Id)))!.SubjectUserId);
            Assert.Null((await Audit(r => r.GetByIdAsync(subjectLine.Id)))!.PackageId);

            Assert.Equal(new[] { entry.Id, subjectLine.Id }, (await Audit(r => r.ListRecentAsync(Site, now.AddDays(-30), 10))).Select(e => e.Id));
            Assert.Equal(old.Id, Assert.Single(await Audit(r => r.ListOlderThanAsync(Site, now.AddDays(-30)))).Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Audit(async r => { await r.UpdateAsync(entry); return 0; }));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqlitePortabilityAggregateConformanceTests : PortabilityAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-portability-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresPortabilityAggregateConformanceTests : PortabilityAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_portability_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The portability conformance suite FAILS rather than skips (D46).");
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
    /// suite on SQL Server, as every module-aggregate suite since BL-117. Fails, never skips, when SQL Server is down (D46).
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerPortabilityAggregateConformanceTests : PortabilityAggregateConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_portability_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The portability conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenPortabilityAggregateConformanceTests : PortabilityAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-portability-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-portability-conformance-");
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
    public sealed class FilePortabilityAggregateConformanceTests : PortabilityAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-portability-file-conformance-{Guid.NewGuid():N}");

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
