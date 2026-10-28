#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.DataExport.Persistence;
using Stomme.Persistence.RavenDb;
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

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// BL-85 (D108) - both export aggregates round-trip member for member on EVERY module-aggregate provider - EF
    /// (SQLite), EF (PostgreSQL), RavenDB and File - through the module's own <c>ServerStartup</c> and closed
    /// repositories, and the repositories' <c>FindAsync</c> set answers identically on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DateTime Kind is asserted EXPLICITLY (refutation 13).</b> A relational provider materialises a scalar
    /// DateTime as Unspecified while RavenDB and File hand back UTC, and <see cref="DateTime"/> equality ignores
    /// the kind - so a suite that only compared instants would be green on a divergence. The aggregates re-stamp
    /// on read; this suite is what notices if they stop.
    /// </para>
    /// <para>
    /// Placed in <c>Stomme.Tests</c> beside the other module-aggregate suites: the conformance project has no
    /// File-store module fixture (the BL-78 F11 precedent). PostgreSQL and RavenDB FAIL rather than skip (D46).
    /// </para>
    /// </remarks>
    public abstract class DataExportAggregateConformanceTests : IAsyncLifetime
    {
        // Millisecond precision: PostgreSQL keeps microseconds, so finer ticks would not survive every provider.
        private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, 250, DateTimeKind.Utc);
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

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
            new Stomme.Modules.DataExport.Startup.ServerStartup().ConfigureServices(services);
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

        // Every call in its own DI scope: a read must come from the STORE, never from an identity map that still
        // holds the instance that was written.
        private async Task<T> InScopeAsync<TRepository, T>(Func<TRepository, Task<T>> action)
            where TRepository : notnull
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<TRepository>());
        }

        private Task SaveAsync(ExportJobAggregate job) =>
            InScopeAsync<IExportJobRepository, int>(async r => { await r.AddAsync(job); return 0; });

        private Task SaveAsync(ExportAuditEntryAggregate entry) =>
            InScopeAsync<IExportAuditRepository, int>(async r => { await r.AddAsync(entry); return 0; });

        private static ExportJobAggregate Queued(int siteId = 7, int userId = 42, ExportFormat format = ExportFormat.Xlsx, DateTime? at = null) =>
            ExportJobAggregate.Request(
                Guid.NewGuid(), TenantId.Default, siteId, userId, "stomme.log", format,
                "{\"from\":\"2026-09-01\",\"note\":\"quote \\\" and 'apostrophe'\"}", "{\"includeHeader\":true}", at ?? Now);

        private static ExportJobAggregate Ready(int siteId = 7, DateTime? at = null)
        {
            var start = at ?? Now;
            var job = Queued(siteId, at: start);
            job.Claim(start.AddSeconds(1), Lease);
            job.Heartbeat(start.AddSeconds(2).AddMilliseconds(5), Lease);
            job.Complete(fileId: 99, rowCount: 1_000_000, sizeBytes: 3_000_000_000L, start.AddSeconds(3), TimeSpan.FromDays(7));
            return job;
        }

        // ---- anti-vacuity ----------------------------------------------------------------------------------

        [Fact]
        public async Task The_store_under_test_is_the_named_provider_with_both_aggregates_mapped()
        {
            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<ExportJobAggregate, Guid>>();

            if (repository.GetType().Name.StartsWith("EfRepository", StringComparison.Ordinal))
            {
                var context = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>();
                Assert.Contains(ExpectedStore, context.Database.ProviderName, StringComparison.OrdinalIgnoreCase);

                var job = context.Model.FindEntityType(typeof(ExportJobAggregate));
                var audit = context.Model.FindEntityType(typeof(ExportAuditEntryAggregate));
                Assert.NotNull(job);
                Assert.NotNull(audit);

                foreach (var column in new[] { "RequestedOnUtc", "LeaseExpiresOnUtc", "HeartbeatOnUtc", "ExpiresOnUtc", "SourceKey", "Status", "FailureCode" })
                {
                    Assert.NotNull(job!.FindProperty(column));
                }

                // METHOD accessors must be absent from the schema: as properties they would be stored fields on
                // the document providers with no EF column (D108 item 4).
                Assert.Null(job!.FindProperty("ContentType"));
                Assert.Null(job.FindProperty("FileExtension"));

                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<ExportJobAggregate>()));
                Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(context.Set<ExportAuditEntryAggregate>()));
            }
            else
            {
                Assert.Contains(ExpectedStore, repository.GetType().Name, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_computed_member_is_a_PROPERTY_on_either_aggregate()
        {
            // Both document providers serialise get-only properties. Every public property must therefore have a
            // setter (private is fine) - a get-only one is a computed member wearing a property's clothes.
            foreach (var type in new[] { typeof(ExportJobAggregate), typeof(ExportAuditEntryAggregate) })
            {
                var computed = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(p => p.SetMethod is null)
                    .Select(p => p.Name)
                    .ToList();

                Assert.True(computed.Count == 0, $"{type.Name} has get-only properties that document stores would persist: {string.Join(", ", computed)}");
            }
        }

        // ---- round-trips -----------------------------------------------------------------------------------

        [Fact]
        public async Task A_queued_job_round_trips_member_for_member()
        {
            var written = Queued();
            await SaveAsync(written);

            var read = await InScopeAsync<IExportJobRepository, ExportJobAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(read);
            AssertSameJob(written, read!);
            Assert.Null(read!.StartedOnUtc);
            Assert.Null(read.LeaseExpiresOnUtc);
            Assert.Null(read.FileId);
            Assert.Null(read.FailureCode);
        }

        [Fact]
        public async Task A_ready_job_with_every_member_set_round_trips_and_a_size_past_int_survives()
        {
            var written = Ready();
            await SaveAsync(written);

            var read = await InScopeAsync<IExportJobRepository, ExportJobAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(read);
            AssertSameJob(written, read!);
            // 3,000,000,000 does not fit an int: a provider that narrowed the column would show here.
            Assert.Equal(3_000_000_000L, read!.SizeBytes);
            Assert.Equal(1_000_000L, read.RowCount);
        }

        [Fact]
        public async Task A_failed_job_keeps_its_CODE_and_every_instant_reads_back_as_UTC()
        {
            var written = Queued();
            written.Claim(Now.AddSeconds(1), Lease);
            written.Fail(ExportFailureCode.ArtifactTooLarge, Now.AddSeconds(2));
            await SaveAsync(written);

            var read = await InScopeAsync<IExportJobRepository, ExportJobAggregate?>(r => r.GetByIdAsync(written.Id));

            Assert.NotNull(read);
            AssertSameJob(written, read!);
            Assert.Equal(ExportFailureCode.ArtifactTooLarge, read!.FailureCode);
            Assert.Equal(ExportJobStatus.Failed, read.Status);
        }

        [Fact]
        public async Task An_update_persists_a_state_transition()
        {
            var job = Queued();
            await SaveAsync(job);

            await InScopeAsync<IExportJobRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(job.Id))!;
                loaded.Claim(Now.AddSeconds(5), Lease);
                await r.UpdateAsync(loaded);
                return 0;
            });

            var read = await InScopeAsync<IExportJobRepository, ExportJobAggregate?>(r => r.GetByIdAsync(job.Id));
            Assert.Equal(ExportJobStatus.Rendering, read!.Status);
            Assert.Equal(1, read.Attempts);
            AssertUtc(Now.AddSeconds(5) + Lease, read.LeaseExpiresOnUtc);
        }

        [Fact]
        public async Task An_audit_entry_round_trips_with_and_without_an_actor()
        {
            var jobId = Guid.NewGuid();
            var byUser = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, ExportAuditAction.Requested, Now);
            var bySystem = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, null, ExportAuditAction.Failed, Now.AddSeconds(1), ExportFailureCode.RenderError);
            await SaveAsync(byUser);
            await SaveAsync(bySystem);

            var entries = await InScopeAsync<IExportAuditRepository, IReadOnlyList<ExportAuditEntryAggregate>>(r => r.ListByJobAsync(7, jobId));

            Assert.Equal(2, entries.Count);
            AssertSameEntry(byUser, entries.Single(e => e.Id == byUser.Id));
            AssertSameEntry(bySystem, entries.Single(e => e.Id == bySystem.Id));
        }

        // ---- the FindAsync set, identical on every provider ---------------------------------------------------

        [Fact]
        public async Task The_job_queries_answer_by_site_requester_status_lease_and_expiry()
        {
            var queuedA = Queued(siteId: 7, userId: 42);
            var queuedB = Queued(siteId: 7, userId: 43);
            var otherSite = Queued(siteId: 8, userId: 42);

            var leaseLapsed = Queued(siteId: 7);
            leaseLapsed.Claim(Now.AddHours(-1), Lease);                 // lease ended 50 minutes ago

            var leaseLive = Queued(siteId: 7);
            leaseLive.Claim(Now.AddMinutes(-1), Lease);                 // lease ends in 9 minutes

            var due = Ready(siteId: 7, at: Now.AddDays(-8));            // expired a day ago
            var fresh = Ready(siteId: 7, at: Now.AddDays(-1));          // six days left
            var otherSiteDue = Ready(siteId: 8, at: Now.AddDays(-8));

            foreach (var job in new[] { queuedA, queuedB, otherSite, leaseLapsed, leaseLive, due, fresh, otherSiteDue })
            {
                await SaveAsync(job);
            }

            await InScopeAsync<IExportJobRepository, int>(async r =>
            {
                Assert.Equal(6, (await r.ListBySiteAsync(7)).Count);
                Assert.Equal(2, (await r.ListBySiteAsync(8)).Count);
                Assert.Empty(await r.ListBySiteAsync(9));

                Assert.Equal(Ids(queuedB), Ids(await r.ListByRequesterAsync(7, 43)));
                Assert.Equal(Ids(queuedA, queuedB), Ids(await r.ListByStatusAsync(7, ExportJobStatus.Queued)));
                Assert.Equal(Ids(leaseLapsed, leaseLive), Ids(await r.ListByStatusAsync(7, ExportJobStatus.Rendering)));

                // A NULL lease (every Queued and Ready row) must not satisfy "<= now" on any provider.
                Assert.Equal(Ids(leaseLapsed), Ids(await r.ListLeaseExpiredAsync(7, Now)));
                Assert.Equal(Ids(due), Ids(await r.ListDueForExpiryAsync(7, Now)));
                Assert.Equal(Ids(otherSiteDue), Ids(await r.ListDueForExpiryAsync(8, Now)));

                // The boundary is inclusive, as IsLeaseExpired is.
                Assert.Equal(Ids(leaseLapsed, leaseLive), Ids(await r.ListLeaseExpiredAsync(7, leaseLive.LeaseExpiresOnUtc!.Value)));
                return 0;
            });
        }

        [Fact]
        public async Task A_by_id_read_gives_ONE_answer_for_a_missing_export_and_another_sites_export()
        {
            var job = Queued(siteId: 7);
            await SaveAsync(job);

            await InScopeAsync<IExportJobRepository, int>(async r =>
            {
                Assert.NotNull(await r.GetForSiteAsync(job.Id, 7));
                Assert.Null(await r.GetForSiteAsync(job.Id, 8));
                Assert.Null(await r.GetForSiteAsync(Guid.NewGuid(), 7));
                return 0;
            });
        }

        [Fact]
        public async Task The_audit_queries_answer_by_site_job_and_age()
        {
            var jobId = Guid.NewGuid();
            var old = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, ExportAuditAction.Requested, Now.AddDays(-91));
            var recent = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, jobId, 42, ExportAuditAction.Downloaded, Now.AddDays(-1));
            var otherJob = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 7, Guid.NewGuid(), null, ExportAuditAction.Expired, Now.AddDays(-91));
            var otherSite = ExportAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, 8, jobId, 42, ExportAuditAction.Requested, Now.AddDays(-91));

            foreach (var entry in new[] { old, recent, otherJob, otherSite })
            {
                await SaveAsync(entry);
            }

            await InScopeAsync<IExportAuditRepository, int>(async r =>
            {
                Assert.Equal(3, (await r.ListBySiteAsync(7)).Count);
                // The SAME job id under another site is not this site's history.
                Assert.Equal(new[] { old.Id, recent.Id }.OrderBy(i => i), (await r.ListByJobAsync(7, jobId)).Select(e => e.Id).OrderBy(i => i));
                Assert.Equal(new[] { old.Id, otherJob.Id }.OrderBy(i => i), (await r.ListOlderThanAsync(7, Now.AddDays(-90))).Select(e => e.Id).OrderBy(i => i));
                return 0;
            });
        }

        [Fact]
        public async Task A_non_UTC_instant_is_refused_by_the_queries_rather_than_silently_shifted()
        {
            await InScopeAsync<IExportJobRepository, int>(async r =>
            {
                await Assert.ThrowsAsync<ArgumentException>(() => r.ListLeaseExpiredAsync(7, DateTime.Now));
                await Assert.ThrowsAsync<ArgumentException>(() => r.ListDueForExpiryAsync(7, new DateTime(2026, 9, 20)));
                return 0;
            });
        }

        // ---- assertions ------------------------------------------------------------------------------------

        private static IEnumerable<Guid> Ids(params ExportJobAggregate[] jobs) => jobs.Select(j => j.Id).OrderBy(i => i);

        private static IEnumerable<Guid> Ids(IReadOnlyList<ExportJobAggregate> jobs) => jobs.Select(j => j.Id).OrderBy(i => i);

        private static void AssertUtc(DateTime expected, DateTime actual)
        {
            Assert.Equal(expected, actual);
            Assert.Equal(DateTimeKind.Utc, actual.Kind);
        }

        private static void AssertUtc(DateTime? expected, DateTime? actual)
        {
            Assert.Equal(expected, actual);
            if (actual is { } instant)
            {
                Assert.Equal(DateTimeKind.Utc, instant.Kind);
            }
        }

        private static void AssertSameJob(ExportJobAggregate expected, ExportJobAggregate actual)
        {
            Assert.NotSame(expected, actual);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.RequesterUserId, actual.RequesterUserId);
            Assert.Equal(expected.SourceKey, actual.SourceKey);
            Assert.Equal(expected.ArgumentsJson, actual.ArgumentsJson);
            Assert.Equal(expected.OptionsJson, actual.OptionsJson);
            Assert.Equal(expected.Format, actual.Format);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Attempts, actual.Attempts);
            Assert.Equal(expected.RowCount, actual.RowCount);
            Assert.Equal(expected.SizeBytes, actual.SizeBytes);
            Assert.Equal(expected.FileId, actual.FileId);
            Assert.Equal(expected.FailureCode, actual.FailureCode);
            AssertUtc(expected.RequestedOnUtc, actual.RequestedOnUtc);
            AssertUtc(expected.StartedOnUtc, actual.StartedOnUtc);
            AssertUtc(expected.CompletedOnUtc, actual.CompletedOnUtc);
            AssertUtc(expected.ExpiresOnUtc, actual.ExpiresOnUtc);
            AssertUtc(expected.LeaseExpiresOnUtc, actual.LeaseExpiresOnUtc);
            AssertUtc(expected.HeartbeatOnUtc, actual.HeartbeatOnUtc);
            Assert.Equal(expected.ContentType(), actual.ContentType());
            Assert.Equal(expected.FileExtension(), actual.FileExtension());
        }

        private static void AssertSameEntry(ExportAuditEntryAggregate expected, ExportAuditEntryAggregate actual)
        {
            Assert.NotSame(expected, actual);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.JobId, actual.JobId);
            Assert.Equal(expected.ActorUserId, actual.ActorUserId);
            Assert.Equal(expected.Action, actual.Action);
            Assert.Equal(expected.FailureCode, actual.FailureCode);
            AssertUtc(expected.OccurredOnUtc, actual.OccurredOnUtc);
        }
    }

    /// <summary>EF Core on a throwaway SQLite file, composed through the module startup.</summary>
    public sealed class SqliteDataExportAggregateConformanceTests : DataExportAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-dataexport-conformance-{Guid.NewGuid():N}.db")};Pooling=True";

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
    public sealed class PostgresDataExportAggregateConformanceTests : DataExportAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_dataexport_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The data-export aggregate conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenDataExportAggregateConformanceTests : DataExportAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-dataexport-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-dataexport-conformance-");
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

    /// <summary>The generic FileRepository in a throwaway directory - the third provider.</summary>
    public sealed class FileDataExportAggregateConformanceTests : DataExportAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-dataexport-file-conformance-{Guid.NewGuid():N}");

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
