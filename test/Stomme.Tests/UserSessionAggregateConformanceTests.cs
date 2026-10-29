#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Sessions.Domain;
using Stomme.Modules.Sessions.Infrastructure;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Sessions.Persistence;
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
    /// A server-side session round-trips member for member on EVERY module-aggregate store (EF SQLite, EF
    /// PostgreSQL, EF SQL Server, RavenDB, File) through the module's own startup, a revoke read in one scope and written
    /// in the next persists, and the reads answer identically: a user's active sessions, a site's, and retention's ended
    /// ones - never another site's, never an ended one where an active one is asked for. PostgreSQL, SQL Server and
    /// RavenDB FAIL rather than skip.
    /// </summary>
    public abstract class UserSessionAggregateConformanceTests : IAsyncLifetime
    {
        private const int Site = 7;
        private const int OtherSite = 8;
        private const int Ada = 42;
        private const int Bob = 43;

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
            new Stomme.Modules.Sessions.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> InScopeAsync<T>(Func<IUserSessionRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IUserSessionRepository>());
        }

        private Task AddAsync(UserSessionAggregate session) => InScopeAsync(async r => { await r.AddAsync(session); return 0; });

        // Whole milliseconds: SQL Server's datetime2 and PostgreSQL's timestamp keep microseconds, not ticks.
        private static DateTime UtcNow() => DateTime.UnixEpoch.AddMilliseconds(Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds));

        private static UserSessionAggregate Session(int site, int user, DateTime openedUtc, DateTime expiresUtc) =>
            UserSessionAggregate.Open(TenantId.Default, site, user, openedUtc, expiresUtc, "10.1.2.3", "Mozilla/5.0 (conformance)");

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<UserSessionAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<UserSessionAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_session_round_trips_member_for_member_with_UTC_kind_and_no_revocation()
        {
            var now = UtcNow();
            var session = Session(Site, Ada, now, now.AddDays(14));
            await AddAsync(session);

            var read = (await InScopeAsync(r => r.FindSessionAsync(session.Id)))!;
            Assert.Equal((TenantId.Default, Site, Ada), (read.TenantId, read.SiteId, read.UserId));
            Assert.Equal((now, now, now.AddDays(14)), (read.CreatedOnUtc, read.LastSeenOnUtc, read.ExpiresOnUtc));
            Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (read.CreatedOnUtc.Kind, read.ExpiresOnUtc.Kind));
            Assert.Equal(("10.1.2.3", "Mozilla/5.0 (conformance)", string.Empty), (read.RemoteAddress, read.UserAgent, read.RevokeReason));
            Assert.Null(read.RevokedOnUtc);
            Assert.Null(read.RevokedByUserId);
            Assert.True(read.IsActiveAt(now));
        }

        [Fact]
        public async Task A_revoke_read_in_one_scope_and_written_in_the_next_persists_and_ends_the_session()
        {
            var now = UtcNow();
            var session = Session(Site, Ada, now.AddHours(-1), now.AddDays(14));
            await AddAsync(session);

            // Read in one scope, revoked and written in ANOTHER - how the registry is used across requests.
            var loaded = (await InScopeAsync(r => r.FindSessionAsync(session.Id)))!;
            Assert.True(loaded.Revoke(now, Bob, "revoked by an administrator"));
            await InScopeAsync(async r => { await r.UpdateAsync(loaded); return 0; });

            var read = (await InScopeAsync(r => r.FindSessionAsync(session.Id)))!;
            Assert.Equal(now, read.RevokedOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.RevokedOnUtc!.Value.Kind);
            Assert.Equal((int?)Bob, read.RevokedByUserId);
            Assert.Equal("revoked by an administrator", read.RevokeReason);
            Assert.Equal(now, read.ExpiresOnUtc);   // "ended" is one field
            Assert.False(read.IsActiveAt(now));
            Assert.Empty(await InScopeAsync(r => r.ListActiveForUserAsync(Site, Ada, now)));

            // A second revoke is a no-op that keeps the FIRST reason.
            Assert.False(read.Revoke(now.AddMinutes(1), Ada, "signed out"));
            Assert.Equal("revoked by an administrator", read.RevokeReason);
        }

        [Fact]
        public async Task Active_lists_are_this_user_and_this_site_only_and_never_an_ended_session()
        {
            var now = UtcNow();
            var older = Session(Site, Ada, now.AddHours(-3), now.AddDays(14));
            var newer = Session(Site, Ada, now.AddHours(-1), now.AddDays(14));
            var expired = Session(Site, Ada, now.AddDays(-20), now.AddDays(-6));
            var revoked = Session(Site, Ada, now.AddHours(-2), now.AddDays(14));
            revoked.Revoke(now.AddMinutes(-5), Ada, "signed out");
            var bobs = Session(Site, Bob, now.AddHours(-4), now.AddDays(14));
            var elsewhere = Session(OtherSite, Ada, now.AddHours(-1), now.AddDays(14));
            foreach (var session in new[] { older, newer, expired, revoked, bobs, elsewhere })
            {
                await AddAsync(session);
            }

            var adas = await InScopeAsync(r => r.ListActiveForUserAsync(Site, Ada, now));
            Assert.Equal(new[] { newer.Id, older.Id }, adas.Select(s => s.Id));

            var site = await InScopeAsync(r => r.ListActiveForSiteAsync(Site, now, take: 10));
            Assert.Equal(new[] { newer.Id, older.Id, bobs.Id }.OrderBy(id => id), site.Select(s => s.Id).OrderBy(id => id));

            var one = await InScopeAsync(r => r.ListActiveForSiteAsync(Site, now, take: 1));
            Assert.Single(one);
        }

        [Fact]
        public async Task Retention_deletes_only_what_ended_before_the_cutoff_and_never_an_active_session()
        {
            var now = DateTime.UtcNow;
            var active = Session(Site, Ada, now.AddDays(-100), now.AddDays(5));            // long-lived but ACTIVE: kept
            var endedLongAgo = Session(Site, Ada, now.AddDays(-100), now.AddDays(-60));    // ended 60 days ago: purged
            var endedRecently = Session(Site, Ada, now.AddDays(-20), now.AddDays(-2));     // ended 2 days ago: kept
            var revokedLongAgo = Session(Site, Bob, now.AddDays(-90), now.AddDays(10));
            revokedLongAgo.Revoke(now.AddDays(-45), Bob, "signed out");                    // revoked 45 days ago: purged
            var otherSite = Session(OtherSite, Ada, now.AddDays(-100), now.AddDays(-60));  // another site: not this pass's
            foreach (var session in new[] { active, endedLongAgo, endedRecently, revokedLongAgo, otherSite })
            {
                await AddAsync(session);
            }

            await using var scope = _services.CreateAsyncScope();
            var purger = scope.ServiceProvider.GetServices<Stomme.Infrastructure.IRetentionPurger>().OfType<SessionRetentionPurger>().Single();
            Assert.Equal(2, await purger.PurgeAsync(Site, days: 30));

            Assert.NotNull(await InScopeAsync(r => r.FindSessionAsync(active.Id)));
            Assert.NotNull(await InScopeAsync(r => r.FindSessionAsync(endedRecently.Id)));
            Assert.NotNull(await InScopeAsync(r => r.FindSessionAsync(otherSite.Id)));
            Assert.Null(await InScopeAsync(r => r.FindSessionAsync(endedLongAgo.Id)));
            Assert.Null(await InScopeAsync(r => r.FindSessionAsync(revokedLongAgo.Id)));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteUserSessionAggregateConformanceTests : UserSessionAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-session-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresUserSessionAggregateConformanceTests : UserSessionAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_session_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The session conformance suite FAILS rather than skips (D46).");
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
    /// EF Core on a LIVE SQL Server scratch database (<see cref="SqlServerTestEndpoint"/>) - the first module-aggregate
    /// suite on SQL Server, as every module-aggregate suite. Fails, never skips, when SQL Server is down.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerUserSessionAggregateConformanceTests : UserSessionAggregateConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_session_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The session conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenUserSessionAggregateConformanceTests : UserSessionAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-session-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-session-conformance-");
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
    public sealed class FileUserSessionAggregateConformanceTests : UserSessionAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-session-file-conformance-{Guid.NewGuid():N}");

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
