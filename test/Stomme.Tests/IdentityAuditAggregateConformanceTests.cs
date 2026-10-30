#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.IdentityAudit;
using Stomme.Modules.IdentityAudit.Domain;
using Stomme.Modules.IdentityAudit.Infrastructure;
using Stomme.IdentityAudit.Persistence;
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

namespace Stomme.Tests
{
    /// <summary>
    /// The identity audit entry round-trips member for member on EVERY module-aggregate store (EF SQLite, EF
    /// PostgreSQL, EF SQL Server, RavenDB, File) through the module's own startup, and its reads answer identically: the
    /// window, the newest-first order and the take; retention's older-than; another site is never included. The ledger is
    /// append-only, and the retention pass has a floor. PostgreSQL, SQL Server and RavenDB FAIL rather than skip.
    /// </summary>
    public abstract class IdentityAuditAggregateConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, 250, DateTimeKind.Utc);
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
            new Stomme.Modules.IdentityAudit.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> InScopeAsync<T>(Func<IIdentityAuditRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IIdentityAuditRepository>());
        }

        private Task AddAsync(IdentityAuditEntryAggregate entry) => InScopeAsync(async r => { await r.AddAsync(entry); return 0; });

        private static IdentityAuditEntryAggregate Entry(int site, IdentityAuditEvent evt, DateTime at, int? actor = 1, int? subject = 42, string? name = "ada", string? detail = null) =>
            IdentityAuditEntryAggregate.Record(TenantId.Default, site, evt, actor, subject, name, "10.1.2.3", detail, at);

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<IdentityAuditEntryAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<IdentityAuditEntryAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_entry_round_trips_member_for_member_with_its_nullable_ids_and_UTC_kind()
        {
            var entry = Entry(Site, IdentityAuditEvent.RoleGranted, Now, actor: 1, subject: 42, name: "ada", detail: "Administrators");
            var anonymous = Entry(Site, IdentityAuditEvent.SignInFailed, Now, actor: null, subject: null, name: null, detail: "unknown account");
            await AddAsync(entry);
            await AddAsync(anonymous);

            var read = (await InScopeAsync(r => r.GetByIdAsync(entry.Id)))!;
            Assert.Equal((TenantId.Default, Site, IdentityAuditEvent.RoleGranted, (int?)1, (int?)42), (read.TenantId, read.SiteId, read.Event, read.ActorUserId, read.SubjectUserId));
            Assert.Equal(("ada", "10.1.2.3", "Administrators"), (read.SubjectName, read.RemoteAddress, read.Detail));
            Assert.Equal(Now, read.OccurredOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.OccurredOnUtc.Kind);

            var readAnonymous = (await InScopeAsync(r => r.GetByIdAsync(anonymous.Id)))!;
            Assert.Null(readAnonymous.ActorUserId);
            Assert.Null(readAnonymous.SubjectUserId);
            Assert.Equal(string.Empty, readAnonymous.SubjectName);
        }

        [Fact]
        public async Task Recent_is_the_window_newest_first_and_the_take_and_never_another_site()
        {
            foreach (var entry in new[]
            {
                Entry(Site, IdentityAuditEvent.SignIn, Now.AddDays(-40)),          // outside a 30-day window
                Entry(Site, IdentityAuditEvent.SignInFailed, Now.AddDays(-3)),
                Entry(Site, IdentityAuditEvent.LockedOut, Now.AddDays(-2)),
                Entry(Site, IdentityAuditEvent.PasswordReset, Now.AddDays(-1)),
                Entry(OtherSite, IdentityAuditEvent.SignIn, Now.AddDays(-1)),       // another site
            })
            {
                await AddAsync(entry);
            }

            var recent = await InScopeAsync(r => r.ListRecentAsync(Site, Now.AddDays(-30), take: 10));
            Assert.Equal(new[] { IdentityAuditEvent.PasswordReset, IdentityAuditEvent.LockedOut, IdentityAuditEvent.SignInFailed }, recent.Select(e => e.Event));

            var two = await InScopeAsync(r => r.ListRecentAsync(Site, Now.AddDays(-30), take: 2));
            Assert.Equal(new[] { IdentityAuditEvent.PasswordReset, IdentityAuditEvent.LockedOut }, two.Select(e => e.Event));

            var old = await InScopeAsync(r => r.ListOlderThanAsync(Site, Now.AddDays(-30)));
            Assert.Equal(IdentityAuditEvent.SignIn, Assert.Single(old).Event);
        }

        [Fact]
        public async Task The_ledger_is_APPEND_only()
        {
            var entry = Entry(Site, IdentityAuditEvent.SignIn, Now);
            await AddAsync(entry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(async r => { await r.UpdateAsync(entry); return 0; }));
        }

        [Fact]
        public async Task Retention_has_a_FLOOR_a_setting_of_zero_cannot_empty_the_ledger()
        {
            await AddAsync(Entry(Site, IdentityAuditEvent.SignIn, DateTime.UtcNow.AddDays(-5)));     // recent: kept at ANY setting
            await AddAsync(Entry(Site, IdentityAuditEvent.SignIn, DateTime.UtcNow.AddDays(-400)));   // past the default: purged

            await using var scope = _services.CreateAsyncScope();
            var purger = scope.ServiceProvider.GetServices<Stomme.Infrastructure.IRetentionPurger>().OfType<IdentityAuditRetentionPurger>().Single();
            Assert.Equal(1, await purger.PurgeAsync(Site, days: 0));
            Assert.Single(await InScopeAsync(r => r.ListRecentAsync(Site, DateTime.UtcNow.AddDays(-30), 10)));
        }
    }
    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteIdentityAuditAggregateConformanceTests : IdentityAuditAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-idaudit-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresIdentityAuditAggregateConformanceTests : IdentityAuditAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_idaudit_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The identity audit conformance suite FAILS rather than skips.");
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
    /// EF Core on a LIVE SQL Server scratch database (the test server, <see cref="SqlServerTestEndpoint"/>) - the first module-aggregate
    /// suite on SQL Server, added to measure it. Fails, never skips, when SQL Server is down.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerIdentityAuditAggregateConformanceTests : IdentityAuditAggregateConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_idaudit_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The identity audit conformance suite FAILS rather than skips.");
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
    public sealed class RavenIdentityAuditAggregateConformanceTests : IdentityAuditAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-idaudit-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-idaudit-conformance-");
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
    public sealed class FileIdentityAuditAggregateConformanceTests : IdentityAuditAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-idaudit-file-conformance-{Guid.NewGuid():N}");

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
