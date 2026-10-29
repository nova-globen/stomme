#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.AiAssistant.Persistence;
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

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// The conversation on every store an install can choose: the transcript, the data context
    /// and the proposed artifacts (a child entity carrying its own dataset list) ride the JSON collection columns and come
    /// back intact; a user lists their own conversations on their own site and nobody else's; a by-id read for another user
    /// or site answers "not found"; and the erasure participant deletes exactly the user's conversations on the site,
    /// idempotently. the predecessor platform's <c>Conversation_round_trips_through_the_repository_with_its_state_intact</c> is the first case,
    /// on four stores instead of an in-memory one. Composed through the module's OWN startup, the way the host does it.
    /// </summary>
    public abstract class AssistantConversationStoreConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 30, 12, 30, 0, DateTimeKind.Utc);
        private const int Site = 3;
        private const int OtherSite = 4;
        private const int User = 11;
        private const int OtherUser = 12;

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
            new Stomme.Modules.AiAssistant.Startup.ServerStartup().ConfigureServices(services);
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

        private Task SaveAsync(AssistantConversationAggregate conversation, bool add = true) => InScopeAsync(async sp =>
        {
            var repository = sp.GetRequiredService<IAssistantConversationRepository>();
            if (add)
            {
                await repository.AddAsync(conversation);
            }
            else
            {
                await repository.UpdateAsync(conversation);
            }

            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            return 0;
        });

        private static AssistantConversationAggregate Conversation(int siteId, int userId) =>
            AssistantConversationAggregate.Start(Guid.NewGuid(), TenantId.Default, siteId, userId, Now);

        [Fact]
        public async Task Conversation_round_trips_through_the_repository_with_its_state_intact()
        {
            var conversation = Conversation(Site, User);
            conversation.AddMessage(MessageRole.User, "make me a pipeline", Now);
            conversation.AddMessage(MessageRole.Assistant, "Here is a draft - apply it when it looks right.", Now.AddSeconds(3));
            var attached = Guid.NewGuid();
            conversation.AttachDataContext(DataContextRef.Create(attached), Now);
            var artifact = conversation.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Pipeline, "Daily import", "{\"steps\":[]}", AgentTrace.Create("pipeline-architect", "drafted from the sales dataset"), new[] { attached }, Now);
            await SaveAsync(conversation);

            var loaded = await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().GetForUserAsync(conversation.Id, Site, User));

            Assert.NotNull(loaded);
            Assert.Equal((Site, User, ConversationStatus.Active), (loaded!.SiteId, loaded.UserId, loaded.Status));
            Assert.Equal(new[] { (MessageRole.User, "make me a pipeline"), (MessageRole.Assistant, "Here is a draft - apply it when it looks right.") },
                loaded.Messages.Select(message => (message.Role, message.Content)).ToArray());
            Assert.Equal(DateTimeKind.Utc, loaded.Messages[1].AtUtc.Kind);
            Assert.Equal(Now.AddSeconds(3), loaded.Messages[1].AtUtc);
            Assert.Equal(attached, Assert.Single(loaded.DataContext).DatasetId);
            Assert.True(loaded.DataContext[0].SummaryOnly);
            var loadedArtifact = Assert.Single(loaded.Artifacts);
            Assert.Equal((artifact.Id, ArtifactKind.Pipeline, "Daily import", "{\"steps\":[]}", ArtifactState.Proposed), (loadedArtifact.Id, loadedArtifact.Kind, loadedArtifact.Title, loadedArtifact.SpecJson, loadedArtifact.State));
            Assert.Equal(("pipeline-architect", "drafted from the sales dataset"), (loadedArtifact.Trace.AgentName, loadedArtifact.Trace.Summary));
            Assert.Equal(attached, Assert.Single(loadedArtifact.ReferencedDatasetIds));

            // A change to a child is read back.
            loaded.ApplyArtifact(artifact.Id, Now.AddMinutes(1));
            await SaveAsync(loaded, add: false);
            var reloaded = await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().GetForUserAsync(conversation.Id, Site, User));
            Assert.Equal(ArtifactState.Applied, Assert.Single(reloaded!.Artifacts).State);
        }

        [Fact]
        public async Task A_user_reads_their_own_conversations_on_their_own_site_and_nobody_elses()
        {
            var mine = Conversation(Site, User);
            var theirs = Conversation(Site, OtherUser);
            var otherSite = Conversation(OtherSite, User);
            await SaveAsync(mine);
            await SaveAsync(theirs);
            await SaveAsync(otherSite);

            var listed = await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().ListForUserAsync(Site, User));
            Assert.Equal(mine.Id, Assert.Single(listed).Id);

            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().GetForUserAsync(theirs.Id, Site, User)));
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().GetForUserAsync(otherSite.Id, Site, User)));
        }

        [Fact]
        public async Task The_data_package_carries_the_users_conversations_and_nobody_elses()
        {
            var mine = Conversation(Site, User);
            mine.AddMessage(MessageRole.User, "my own words", Now);
            mine.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Dashboard, "Overview", "{\"widgets\":[]}", AgentTrace.Create("dashboard-designer", "drafted"), null, Now);
            await SaveAsync(mine);
            await SaveAsync(Conversation(Site, OtherUser));
            await SaveAsync(Conversation(OtherSite, User));

            var entities = await InScopeAsync(sp => sp.GetServices<Stomme.Portability.ISubjectDataContributor>().Single(c => c.SubjectDataType == "assistant-conversation").ExportForSubjectAsync(new Stomme.Portability.SubjectScope(Site, User)));

            var entity = Assert.Single(entities);
            Assert.Equal(mine.Id.ToString("N"), entity.Ref.EntityId);
            Assert.Contains("my own words", entity.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("Overview", entity.PayloadJson, StringComparison.Ordinal);
        }
        [Fact]
        public async Task The_erasure_participant_deletes_exactly_the_users_conversations_on_the_site()
        {
            var first = Conversation(Site, User);
            first.AddMessage(MessageRole.User, "my own words", Now);
            var second = Conversation(Site, User);
            var someoneElse = Conversation(Site, OtherUser);
            var otherSite = Conversation(OtherSite, User);
            foreach (var conversation in new[] { first, second, someoneElse, otherSite })
            {
                await SaveAsync(conversation);
            }

            var outcomes = await InScopeAsync(sp => sp.GetServices<ISubjectErasureParticipant>().Single(p => p.Name == "AI assistant conversations").EraseAsync(new ErasureSubject(Site, User, null)));
            var outcome = Assert.Single(outcomes);
            Assert.Equal(("AssistantConversationAggregate", ErasureHandling.Delete, 2), (outcome.Store, outcome.Handling, outcome.Affected));

            Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().ListForUserAsync(Site, User)));
            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().ListForUserAsync(Site, OtherUser)));
            Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IAssistantConversationRepository>().ListForUserAsync(OtherSite, User)));

            // Idempotent: a second run finds nothing to do.
            var again = await InScopeAsync(sp => sp.GetServices<ISubjectErasureParticipant>().Single(p => p.Name == "AI assistant conversations").EraseAsync(new ErasureSubject(Site, User, null)));
            Assert.Equal(0, Assert.Single(again).Affected);
        }
    }

    public sealed class SqliteAssistantConversationStoreConformanceTests : AssistantConversationStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-assistant-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresAssistantConversationStoreConformanceTests : AssistantConversationStoreConformanceTests
    {
        private readonly string _database = "stomme_assistant_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The conversation store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenAssistantConversationStoreConformanceTests : AssistantConversationStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-assistant-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-assistant-conformance-");
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

    public sealed class FileAssistantConversationStoreConformanceTests : AssistantConversationStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-assistant-file-conformance-{Guid.NewGuid():N}");

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
