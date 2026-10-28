#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Landing;
using Stomme.Landing.Persistence;
using Stomme.Modules.Landing.Domain;
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

namespace Stomme.Tests.Landing
{
    /// <summary>
    /// BL-98 increment 1 - the landing page on every store the installation can choose: the content, the source, the status,
    /// the nullable instants and the site filter round-trip alike on SQLite, PostgreSQL, RavenDB and the file store.
    /// </summary>
    public abstract class LandingStoreConformanceTests : IAsyncLifetime
    {
        private const int Site = 7;
        private const int OtherSite = 8;
        private static readonly DateTime At = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

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
            new Stomme.Modules.Landing.Startup.ServerStartup().ConfigureServices(services);
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

        [Fact]
        public async Task A_published_predefined_page_round_trips_and_is_found_by_its_site_only()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, Site, At);
            page.ConfigurePredefined(new PredefinedLandingContent("Welcome", "We make things.", new[]
            {
                new LandingSectionView("Start", "Read this first.", "Go", "/start"),
                new LandingSectionView("Docs", "Everything else.", "Docs", "https://example.com/docs"),
            }), At);
            page.Publish(At.AddMinutes(1));
            await InScopeAsync(async sp => { await sp.GetRequiredService<ILandingPageRepository>().AddAsync(page); await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(); return 0; });

            var read = await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(Site));
            Assert.NotNull(read);
            Assert.Equal((page.Id, LandingSource.Predefined, LandingStatus.Published, (DateTime?)At.AddMinutes(1), (Guid?)null), (read!.Id, read.Source, read.Status, read.PublishedUtc, read.DashboardId));
            var content = read.Predefined()!;
            Assert.Equal(("Welcome", 2, "https://example.com/docs"), (content.Headline, content.Sections.Count, content.Sections[1].LinkUrl));
            Assert.Null(await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(OtherSite)));
        }

        [Fact]
        public async Task An_update_and_a_reset_are_read_back()
        {
            var page = LandingPageAggregate.Create(TenantId.Default, Site, At);
            page.ConfigurePredefined(new PredefinedLandingContent("First", string.Empty, Array.Empty<LandingSectionView>()), At);
            await InScopeAsync(async sp => { await sp.GetRequiredService<ILandingPageRepository>().AddAsync(page); await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(); return 0; });

            await InScopeAsync(async sp =>
            {
                var pages = sp.GetRequiredService<ILandingPageRepository>();
                var loaded = (await pages.FindForSiteAsync(Site))!;
                loaded.ConfigurePredefined(new PredefinedLandingContent("Second", "Edited.", Array.Empty<LandingSectionView>()), At.AddMinutes(1));
                await pages.UpdateAsync(loaded);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });
            Assert.Equal("Second", (await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(Site)))!.Predefined()!.Headline);

            await InScopeAsync(async sp =>
            {
                var pages = sp.GetRequiredService<ILandingPageRepository>();
                var loaded = (await pages.FindForSiteAsync(Site))!;
                loaded.Reset(At.AddMinutes(2));
                await pages.UpdateAsync(loaded);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });
            var reset = (await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(Site)))!;
            Assert.Equal((LandingSource.None, LandingStatus.Draft, (PredefinedLandingContent?)null), (reset.Source, reset.Status, reset.Predefined()));
        }

        [Fact]
        public async Task A_generated_page_round_trips_its_html_and_a_reset_clears_it()
        {
            // BL-98 increment 3: the generated HTML is one scalar column on every store - long, with quotes, entities and
            // non-ASCII text, read back byte for byte.
            var html = "<section data-section=\"hero\" style=\"padding: 2rem; color: #123456\"><h1>Välkommen &amp; \"hello\"</h1>"
                + string.Concat(System.Linq.Enumerable.Repeat("<p>Ett stycke text om det som webbplatsen erbjuder.</p>", 200))
                + "</section><section data-section=\"contact\"><a href=\"/contact\" target=\"_top\">Contact</a></section>";
            var page = LandingPageAggregate.Create(TenantId.Default, Site, At);
            page.ConfigureGenerated(html, At);
            page.Publish(At.AddMinutes(1));
            await InScopeAsync(async sp => { await sp.GetRequiredService<ILandingPageRepository>().AddAsync(page); await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(); return 0; });

            var read = (await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(Site)))!;
            Assert.Equal((LandingSource.GeneratedHtml, LandingStatus.Published, html), (read.Source, read.Status, read.GeneratedHtml));
            Assert.Null(read.Predefined());

            await InScopeAsync(async sp =>
            {
                var pages = sp.GetRequiredService<ILandingPageRepository>();
                var loaded = (await pages.FindForSiteAsync(Site))!;
                loaded.Reset(At.AddMinutes(2));
                await pages.UpdateAsync(loaded);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return 0;
            });
            var reset = (await InScopeAsync(sp => sp.GetRequiredService<ILandingPageRepository>().FindForSiteAsync(Site)))!;
            Assert.Equal((LandingSource.None, (string?)null), (reset.Source, reset.GeneratedHtml));
        }
    }

    public sealed class SqliteLandingStoreConformanceTests : LandingStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-landing-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresLandingStoreConformanceTests : LandingStoreConformanceTests
    {
        private readonly string _database = "stomme_landing_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(), $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port}. The Landing store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenLandingStoreConformanceTests : LandingStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-landing-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-landing-conformance-");
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

    public sealed class FileLandingStoreConformanceTests : LandingStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-landing-file-conformance-{Guid.NewGuid():N}");

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
