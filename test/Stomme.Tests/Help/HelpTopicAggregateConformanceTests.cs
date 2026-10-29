#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Help;
using Stomme.Help.Persistence;
using Alias = Stomme.Models.Alias;
using Stomme.Modules.ContextualHelp.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using SiteState = Stomme.Shared.SiteState;
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

namespace Stomme.Tests.Help
{
    /// <summary>
    /// The help topic round-trips member for member on EVERY module-aggregate provider — EF
    /// (SQLite), EF (PostgreSQL), RavenDB and File — through the module's own <c>ServerStartup</c> and its closed
    /// repository, and every method that repository exposes answers identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It also closes the predecessor platform user-gated item 74, and that is why the site comes from <c>SiteState</c>.</b> D103
    /// refutation 13 refuted "SiteId closes item 74 in ~5 lines": the seam's automatic isolation is TENANT-only, and
    /// the default install puts every site in ONE tenant. So the repository takes no site parameter, the site is read
    /// from the resolved alias, and this suite drives every method with TWO SITES IN ONE TENANT.
    /// </para>
    /// <para>
    /// <see cref="DateTime.Kind"/> is asserted EXPLICITLY: equality ignores it, so a suite that only compared instants
    /// would be green on exactly the divergence the aggregate re-stamps to hide. PostgreSQL and RavenDB FAIL rather
    /// than skip.
    /// </para>
    /// </remarks>
    public abstract class HelpTopicAggregateConformanceTests : IAsyncLifetime
    {
        // Millisecond precision: PostgreSQL keeps microseconds, so finer ticks would not survive every provider.
        private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, 250, DateTimeKind.Utc);

        private const int Site = 7;
        private const int OtherSite = 8;
        private const string Contributor = "stomme";

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

            // SCOPED, so each scope below can resolve a different site - which is how two sites in ONE tenant are
            // driven through a repository that takes no site parameter.
            services.AddScoped<SiteState>();
            new Stomme.Modules.ContextualHelp.Startup.ServerStartup().ConfigureServices(services);
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

        /// <summary>
        /// A call in its own DI scope, as SITE <paramref name="siteId"/>. A read must come from the STORE, never from
        /// the instance that was written.
        /// </summary>
        private async Task<T> AsSiteAsync<T>(int siteId, Func<IHelpTopicRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SiteState>().Alias =
                new Alias { AliasId = siteId, SiteId = siteId, TenantId = 1, Name = "site" + siteId };
            return await action(scope.ServiceProvider.GetRequiredService<IHelpTopicRepository>());
        }

        private Task SaveAsync(int siteId, HelpTopicAggregate topic) =>
            AsSiteAsync(siteId, async repository => { await repository.AddAsync(topic); return 0; });

        private static HelpTopicAggregate Seeded(int siteId, string anchor, string locale = "en", string title = "Users", string markdown = "Manage **users**.") =>
            HelpTopicAggregate.Seed(TenantId.Default, siteId, new HelpTopicContribution(anchor, locale, title, markdown), Contributor, Now);

        // ---- anti-vacuity ------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<HelpTopicAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<HelpTopicAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        // ---- the round trip ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_topic_round_trips_member_for_member_including_the_kind_of_every_instant()
        {
            var topic = Seeded(Site, "page:admin/users", title: "  Users  ", markdown: "Manage \"users\" — and 'others'.\n\n| a | b |\n|---|---|\n| 1 | 2 |");
            await SaveAsync(Site, topic);

            var read = (await AsSiteAsync(Site, repository => repository.GetAsync("page:admin/users", "en")))!;

            Assert.NotNull(read);
            Assert.Equal(topic.Id, read.Id);
            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal(Site, read.SiteId);
            Assert.Equal("page:admin/users", read.Anchor);
            Assert.Equal("en", read.Locale);
            Assert.Equal("Users", read.Title);
            Assert.Equal(topic.Markdown, read.Markdown);            // punctuation, an em dash, newlines and a table
            Assert.Equal(1, read.ContentVersion);
            Assert.True(read.IsPublished);
            Assert.True(read.IsSeeded);
            Assert.Equal(Contributor, read.SeedContributorId);

            // The KIND, explicitly: equality ignores it, so this is the only assertion that can see the divergence.
            Assert.Equal(Now, read.CreatedOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.CreatedOnUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, read.UpdatedOnUtc.Kind);
        }

        [Fact]
        public async Task A_body_at_the_domains_bound_survives_because_the_COLUMN_is_unbounded()
        {
            // 32 000 is not a valid nvarchar length on SQL Server, so the bound lives in the domain and the column does
            // not. This is the case that says the column really is unbounded on every provider.
            var body = new string('x', HelpTopicContribution.MaximumMarkdownCharacters);
            await SaveAsync(Site, Seeded(Site, "page:long", markdown: body));

            var read = (await AsSiteAsync(Site, repository => repository.GetAsync("page:long", "en")))!;
            Assert.Equal(body, read.Markdown);
        }

        [Fact]
        public async Task An_update_round_trips_and_a_no_op_update_leaves_the_stored_row_alone()
        {
            var topic = Seeded(Site, "page:admin/pages");
            await SaveAsync(Site, topic);

            Assert.True(topic.TryApplySeedUpdate(new HelpTopicContribution("page:admin/pages", "en", "Pages", "Newer text."), Contributor, Now.AddDays(1)));
            await AsSiteAsync(Site, async repository => { await repository.UpdateAsync(topic); return 0; });

            var read = (await AsSiteAsync(Site, repository => repository.GetAsync("page:admin/pages", "en")))!;
            Assert.Equal("Newer text.", read.Markdown);
            Assert.Equal(2, read.ContentVersion);
            Assert.Equal(Now.AddDays(1), read.UpdatedOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.UpdatedOnUtc.Kind);

            // The same contribution again writes nothing, so a boot that changes nothing costs nothing.
            Assert.False(read.TryApplySeedUpdate(new HelpTopicContribution("page:admin/pages", "en", "Pages", "Newer text."), Contributor, Now.AddDays(2)));
        }

        // ---- the deterministic id, across providers ----------------------------------------------------------------

        [Fact]
        public async Task The_SAME_anchor_and_locale_resolve_to_the_SAME_row_on_every_provider()
        {
            await SaveAsync(Site, Seeded(Site, "page:admin/site"));

            // Asked for with different casing and padding - the id is derived from the NORMALIZED form.
            var read = await AsSiteAsync(Site, repository => repository.GetAsync("  PAGE:Admin/Site ", "EN"));
            Assert.NotNull(read);
            Assert.Equal(HelpTopicIds.For(TenantId.Default.Value, Site, "page:admin/site", "en"), read!.Id);
        }

        [Fact]
        public async Task An_unusable_anchor_or_locale_answers_NO_TOPIC_rather_than_throwing()
        {
            // D103 refutation 10: this input arrives from the wire on an endpoint an anonymous visitor can reach.
            Assert.Null(await AsSiteAsync(Site, repository => repository.GetAsync("has space", "en")));
            Assert.Null(await AsSiteAsync(Site, repository => repository.GetAsync("page:x", "not a locale")));
            Assert.Null(await AsSiteAsync(Site, repository => repository.GetAsync(new string('a', 201), "en")));
            Assert.Null(await AsSiteAsync(Site, repository => repository.GetAsync(null!, null!)));
        }

        // ---- TWO SITES IN ONE TENANT: the predecessor platform user-gated item 74 ---------------------------------------------------

        [Fact]
        public async Task EVERY_repository_method_answers_for_the_site_the_scope_resolved_and_no_other()
        {
            var mine = Seeded(Site, "page:admin/users", title: "Mine");
            var theirs = Seeded(OtherSite, "page:admin/users", title: "Theirs");
            await SaveAsync(Site, mine);
            await SaveAsync(OtherSite, theirs);

            // ONE tenant, two sites - the default install's shape, and the one that makes tenant-only isolation useless.
            Assert.Equal(mine.TenantId, theirs.TenantId);
            Assert.NotEqual(mine.Id, theirs.Id);

            // GetAsync: the same anchor and locale, a different topic per site.
            Assert.Equal("Mine", (await AsSiteAsync(Site, r => r.GetAsync("page:admin/users", "en")))!.Title);
            Assert.Equal("Theirs", (await AsSiteAsync(OtherSite, r => r.GetAsync("page:admin/users", "en")))!.Title);

            // GetByIdForSiteAsync: the OTHER site's id gets the same answer as an id that exists nowhere.
            Assert.Null(await AsSiteAsync(Site, r => r.GetByIdForSiteAsync(theirs.Id)));
            Assert.Null(await AsSiteAsync(Site, r => r.GetByIdForSiteAsync(Guid.NewGuid())));
            Assert.NotNull(await AsSiteAsync(Site, r => r.GetByIdForSiteAsync(mine.Id)));

            // ListAsync and ListPublishedAsync: this site's rows alone.
            Assert.Equal(new[] { mine.Id }, (await AsSiteAsync(Site, r => r.ListAsync())).Select(topic => topic.Id));
            Assert.Equal(new[] { theirs.Id }, (await AsSiteAsync(OtherSite, r => r.ListAsync())).Select(topic => topic.Id));
            Assert.Equal(new[] { mine.Id }, (await AsSiteAsync(Site, r => r.ListPublishedAsync())).Select(topic => topic.Id));
        }

        [Fact]
        public async Task A_DRAFT_is_listed_by_the_manage_list_and_NOT_by_the_published_one()
        {
            var draft = HelpTopicAggregate.Author(TenantId.Default, Site, "page:draft", "en", "Draft", "Not ready.", Now);
            await SaveAsync(Site, draft);

            Assert.Contains(await AsSiteAsync(Site, r => r.ListAsync()), topic => topic.Id == draft.Id);
            Assert.DoesNotContain(await AsSiteAsync(Site, r => r.ListPublishedAsync()), topic => topic.Id == draft.Id);

            // ...and publishing it moves it, on every provider.
            draft.Publish(Now.AddDays(1));
            await AsSiteAsync(Site, async r => { await r.UpdateAsync(draft); return 0; });
            Assert.Contains(await AsSiteAsync(Site, r => r.ListPublishedAsync()), topic => topic.Id == draft.Id);
        }

        [Fact]
        public async Task With_NO_resolved_site_every_method_answers_empty_rather_than_everything()
        {
            await SaveAsync(Site, Seeded(Site, "page:noalias"));

            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IHelpTopicRepository>();   // SiteState with no Alias

            Assert.Null(await repository.GetAsync("page:noalias", "en"));
            Assert.Null(await repository.GetByIdForSiteAsync(HelpTopicIds.For(TenantId.Default.Value, Site, "page:noalias", "en")));
            Assert.Empty(await repository.ListAsync());
            Assert.Empty(await repository.ListPublishedAsync());
        }

        [Fact]
        public async Task A_delete_removes_the_row_on_every_provider()
        {
            var topic = Seeded(Site, "page:gone");
            await SaveAsync(Site, topic);
            Assert.NotNull(await AsSiteAsync(Site, r => r.GetAsync("page:gone", "en")));

            await AsSiteAsync(Site, async r => { await r.DeleteAsync(topic); return 0; });
            Assert.Null(await AsSiteAsync(Site, r => r.GetAsync("page:gone", "en")));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteHelpTopicAggregateConformanceTests : HelpTopicAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-help-conformance-{Guid.NewGuid():N}.db")}";

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
                System.IO.File.Delete(new SqliteConnectionStringBuilder(_connectionString).DataSource);
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
    public sealed class PostgresHelpTopicAggregateConformanceTests : HelpTopicAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_help_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The help aggregate conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenHelpTopicAggregateConformanceTests : HelpTopicAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-help-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-help-conformance-");
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
    public sealed class FileHelpTopicAggregateConformanceTests : HelpTopicAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-help-file-conformance-{Guid.NewGuid():N}");

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
