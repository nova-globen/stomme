#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Collaboration.Persistence;
using Stomme.Erasure;
using Stomme.Modules.Collaboration.Domain;
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

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// The lock and the session on every store an install can choose: the round trips that
    /// <c>Repository_RoundTripsAResourceLock</c> and <c>Repository_RoundTripsAnEditSession</c> check, on four stores instead
    /// of an in-memory one - the resource reference as one scalar, the participants and the change stream on the JSON
    /// collection columns, a release read back; a site reads its own and never another's; the erasure participant deletes
    /// the user's locks and removes them from every session of the site, idempotently; the data package carries them.
    /// Composed through the module's own startup, the way the host does it.
    /// </summary>
    public abstract class CollaborationStoreConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        private static readonly ResourceRef Board = ResourceRef.Of("dashboard", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
        private static readonly ResourceRef Flow = ResourceRef.Of("pipeline", "Def-1a");
        private const int Site = 3;
        private const int OtherSite = 4;
        private const int Ada = 11;
        private const int Bo = 12;

        private ServiceProvider _services = default!;

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AddStore(services);
            new Stomme.Modules.Collaboration.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }

        private Task SaveAsync<T>(T aggregate, bool add = true)
            where T : AggregateRoot<Guid> => InScopeAsync(async sp =>
        {
            var repository = sp.GetRequiredService<IRepository<T, Guid>>();
            if (add)
            {
                await repository.AddAsync(aggregate);
            }
            else
            {
                await repository.UpdateAsync(aggregate);
            }

            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            return 0;
        });

        private static ResourceLockAggregate Lock(int siteId, ResourceRef resource, int holderId, LockScope scope = LockScope.Resource) =>
            ResourceLockAggregate.Acquire(Guid.NewGuid(), TenantId.Default, siteId, resource, scope, holderId, holderId == Ada ? "Ada" : "Bo", TimeSpan.FromMinutes(2), Now);

        [Fact]
        public async Task Repository_RoundTripsAResourceLock()
        {
            var resourceLock = Lock(Site, Flow, Ada, LockScope.Field);
            await SaveAsync(resourceLock);

            var loaded = Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListUnreleasedAsync(Site, Flow, LockScope.Field)));
            Assert.Equal((resourceLock.Id, Flow, LockScope.Field, Ada, "Ada", 120, false), (loaded.Id, loaded.Resource, loaded.Scope, loaded.HolderUserId, loaded.HolderDisplayName, loaded.LeaseSeconds, loaded.Released));
            Assert.Equal((Now, Now.AddMinutes(2), DateTimeKind.Utc), (loaded.AcquiredUtc, loaded.ExpiresUtc, loaded.ExpiresUtc.Kind));

            // The id's case is kept - a pipeline definition id is matched ordinally, on every store.
            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListUnreleasedAsync(Site, ResourceRef.Of("pipeline", "def-1a"), LockScope.Field)));
            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListUnreleasedAsync(Site, Flow, LockScope.Resource)));
            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListUnreleasedAsync(OtherSite, Flow, LockScope.Field)));

            loaded.ReleaseByAdministrator(Bo);
            await SaveAsync(loaded, add: false);
            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListUnreleasedAsync(Site)));
            var released = Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListForUserAsync(Site, Ada)));
            Assert.Equal((true, (int?)Bo), (released.Released, released.ReleasedByUserId));
        }

        [Fact]
        public async Task Repository_RoundTripsAnEditSession()
        {
            var session = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, Site, Board, Now);
            session.Join(Ada, "Ada", Now);
            session.Join(Bo, "Bo", Now.AddSeconds(5));
            session.UpdatePresence(Bo, "widget:2", "14");
            session.BroadcastChange(Ada, "layout changed", Now.AddSeconds(9));
            await SaveAsync(session);

            var loaded = await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().FindOpenAsync(Site, Board));
            Assert.NotNull(loaded);
            Assert.Equal((session.Id, Board, EditSessionStatus.Open, Now), (loaded!.Id, loaded.Resource, loaded.Status, loaded.StartedUtc));
            Assert.Equal(new[] { (Ada, "Ada", (string?)null), (Bo, "Bo", "widget:2") }, loaded.Participants.Select(participant => (participant.UserId, participant.DisplayName, participant.ActiveRegion)).ToArray());
            Assert.Equal("14", loaded.Participants[1].CursorPosition);
            Assert.Equal(Now.AddSeconds(5), loaded.Participants[1].JoinedUtc);
            var change = Assert.Single(loaded.Changes);
            Assert.Equal((Ada, "layout changed", Now.AddSeconds(9), DateTimeKind.Utc), (change.AuthorUserId, change.Descriptor, change.AtUtc, change.AtUtc.Kind));

            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().GetForSiteAsync(session.Id, OtherSite)));
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().FindOpenAsync(OtherSite, Board)));

            loaded.Close();
            await SaveAsync(loaded, add: false);
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().FindOpenAsync(Site, Board)));
            Assert.Equal(EditSessionStatus.Closed, (await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().GetForSiteAsync(session.Id, Site)))!.Status);
        }

        [Fact]
        public async Task The_erasure_participant_removes_the_user_from_collaboration_on_the_site_and_nobody_else()
        {
            await SaveAsync(Lock(Site, Board, Ada));
            await SaveAsync(Lock(Site, Flow, Bo));
            await SaveAsync(Lock(OtherSite, Board, Ada));
            var session = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, Site, Board, Now);
            session.Join(Ada, "Ada", Now);
            session.Join(Bo, "Bo", Now);
            session.BroadcastChange(Ada, "mine", Now);
            session.BroadcastChange(Bo, "theirs", Now);
            await SaveAsync(session);

            var outcomes = await InScopeAsync(sp => sp.GetServices<ISubjectErasureParticipant>().Single(p => p.Name == "Collaboration locks and editing sessions").EraseAsync(new ErasureSubject(Site, Ada, null)));

            Assert.Equal(new[] { ("ResourceLockAggregate", 1), ("EditSessionAggregate", 1) }, outcomes.Select(outcome => (outcome.Store, outcome.Affected)).ToArray());
            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListForUserAsync(Site, Ada)));
            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListForUserAsync(Site, Bo)));
            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IResourceLockRepository>().ListForUserAsync(OtherSite, Ada)));
            var kept = (await InScopeAsync(sp => sp.GetRequiredService<IEditSessionRepository>().GetForSiteAsync(session.Id, Site)))!;
            Assert.Equal(Bo, Assert.Single(kept.Participants).UserId);
            Assert.Equal("theirs", Assert.Single(kept.Changes).Descriptor);

            // Idempotent.
            var again = await InScopeAsync(sp => sp.GetServices<ISubjectErasureParticipant>().Single(p => p.Name == "Collaboration locks and editing sessions").EraseAsync(new ErasureSubject(Site, Ada, null)));
            Assert.All(again, outcome => Assert.Equal(0, outcome.Affected));
        }

        [Fact]
        public async Task The_data_package_carries_the_users_locks_and_sessions_and_nobody_elses()
        {
            await SaveAsync(Lock(Site, Board, Ada));
            await SaveAsync(Lock(Site, Flow, Bo));
            var session = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, Site, Board, Now);
            session.Join(Ada, "Ada", Now);
            session.BroadcastChange(Ada, "my change", Now);
            await SaveAsync(session);
            var theirs = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, Site, Flow, Now);
            theirs.Join(Bo, "Bo", Now);
            await SaveAsync(theirs);

            var entities = await InScopeAsync(sp => sp.GetServices<Stomme.Portability.ISubjectDataContributor>().Single(c => c.SubjectDataType == "collaboration").ExportForSubjectAsync(new Stomme.Portability.SubjectScope(Site, Ada)));

            Assert.Equal(2, entities.Count);
            Assert.Contains(entities, entity => entity.PayloadJson.Contains("\"kind\":\"lock\"", StringComparison.Ordinal) && entity.PayloadJson.Contains(Board.Value, StringComparison.Ordinal));
            Assert.Contains(entities, entity => entity.PayloadJson.Contains("my change", StringComparison.Ordinal));
            Assert.DoesNotContain(entities, entity => entity.PayloadJson.Contains(Flow.Value, StringComparison.Ordinal));
        }
    }

    public sealed class SqliteCollaborationStoreConformanceTests : CollaborationStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-collaboration-conformance-{Guid.NewGuid():N}.db")}";

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
            }

            return Task.CompletedTask;
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresCollaborationStoreConformanceTests : CollaborationStoreConformanceTests
    {
        private readonly string _database = "stomme_collaboration_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The collaboration store conformance suite FAILS rather than skips (D46).");
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

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenCollaborationStoreConformanceTests : CollaborationStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-collaboration-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-collaboration-conformance-");
            await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
        }

        protected override void AddStore(IServiceCollection services)
        {
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:DefaultDBType"] = "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server" })
                .Build());
            services.AddRavenFrameworkStore(_store);
        }

        protected override async Task DisposeStoreAsync()
        {
            _store.Dispose();
            try
            {
                using var cleanup = new DocumentStore { Urls = [Url] }.Initialize();
                await cleanup.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
            }
            catch
            {
            }
        }
    }

    public sealed class FileCollaborationStoreConformanceTests : CollaborationStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-collaboration-file-conformance-{Guid.NewGuid():N}");

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
            }

            return Task.CompletedTask;
        }
    }
}
