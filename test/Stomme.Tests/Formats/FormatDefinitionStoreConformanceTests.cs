#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Formats;
using Stomme.Formats.Persistence;
using Stomme.Modules.Formats.Domain;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
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

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// BL-94 increment 1 (D110) - the Formats store's observable contract, once, driven on EF (SQLite and PostgreSQL),
    /// RavenDB and File through the module's own startup: a definition round-trips with its three string value objects
    /// intact and its instants read back as UTC; a site's definitions are found by normalised name whatever the case
    /// the caller used (D49) and listed in name order; another site's are not; an update and a delete land. Fail-not-skip
    /// on the external stores (D46).
    /// </summary>
    public abstract class FormatDefinitionStoreConformanceTests : IAsyncLifetime
    {
        private static readonly DateTime Now = new(2026, 9, 27, 1, 2, 3, 456, DateTimeKind.Utc);
        private const int Site = 7;
        private const int OtherSite = 8;

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
            new Stomme.Modules.Formats.Startup.ServerStartup().ConfigureServices(services);
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

        private async Task<T> InScopeAsync<T>(Func<IFormatDefinitionRepository, Task<T>> action)
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IFormatDefinitionRepository>());
        }

        private static FormatDefinitionAggregate Definition(int siteId, string name, FormatKind kind = FormatKind.Date, string pattern = "yyyy-MM-dd HH:mm", string culture = "en-US", string? timeZone = "Europe/Stockholm") =>
            FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, siteId, name, kind, pattern, culture, timeZone, Now);

        [Fact]
        public async Task A_definition_round_trips_with_its_value_objects_and_utc_instants()
        {
            var definition = Definition(Site, "Audit date");
            await InScopeAsync(async repository => { await repository.AddAsync(definition); return 0; });

            var read = await InScopeAsync(repository => repository.GetByIdAsync(definition.Id));

            Assert.NotNull(read);
            Assert.Equal(("Audit date", "AUDIT DATE", Site, FormatKind.Date), (read.Name, read.NameNormalized, read.SiteId, read.Kind));
            Assert.Equal(("yyyy-MM-dd HH:mm", "en-US", "Europe/Stockholm"), (read.Pattern.Value, read.CultureName.Value, read.TimeZoneId.Value));
            Assert.Equal(Now, read.CreatedUtc);
            Assert.Equal(DateTimeKind.Utc, read.CreatedUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, read.ModifiedUtc.Kind);
            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal("2026-06-23 16:00", read.ToDescriptor().Format(new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero)));
        }

        [Fact]
        public async Task A_site_finds_its_definition_by_name_whatever_the_case_and_not_another_sites()
        {
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(Definition(Site, "Invoice Date"));
                await repository.AddAsync(Definition(OtherSite, "Invoice Date"));
                return 0;
            });

            var mine = await InScopeAsync(repository => repository.FindByNameAsync(Site, "  invoice DATE "));
            var none = await InScopeAsync(repository => repository.FindByNameAsync(Site, "Delivery date"));

            Assert.NotNull(mine);
            Assert.Equal(Site, mine.SiteId);
            Assert.Null(none);
        }

        [Fact]
        public async Task A_sites_definitions_list_in_name_order_and_an_update_and_a_delete_land()
        {
            var zulu = Definition(Site, "Zulu", FormatKind.Number, "N2", "");
            var alpha = Definition(Site, "alpha", FormatKind.Locale, null, "de-DE", null);
            await InScopeAsync(async repository =>
            {
                await repository.AddAsync(zulu);
                await repository.AddAsync(alpha);
                await repository.AddAsync(Definition(OtherSite, "Other"));
                return 0;
            });

            Assert.Equal(new[] { "alpha", "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(definition => definition.Name));

            await InScopeAsync(async repository =>
            {
                var read = (await repository.GetByIdAsync(zulu.Id))!;
                read.Update("Zulu", FormatKind.Number, "N3", "de-DE", null, Now.AddMinutes(1));
                await repository.UpdateAsync(read);
                return 0;
            });
            var updated = await InScopeAsync(repository => repository.GetByIdAsync(zulu.Id));
            Assert.Equal(("N3", "de-DE", Now.AddMinutes(1)), (updated!.Pattern.Value, updated.CultureName.Value, updated.ModifiedUtc));

            await InScopeAsync(async repository =>
            {
                await repository.DeleteAsync((await repository.GetByIdAsync(alpha.Id))!);
                return 0;
            });
            Assert.Null(await InScopeAsync(repository => repository.GetByIdAsync(alpha.Id)));
            Assert.Equal(new[] { "Zulu" }, (await InScopeAsync(repository => repository.ListForSiteAsync(Site))).Select(definition => definition.Name));
        }
    }

    public sealed class SqliteFormatDefinitionStoreConformanceTests : FormatDefinitionStoreConformanceTests
    {
        private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-formats-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresFormatDefinitionStoreConformanceTests : FormatDefinitionStoreConformanceTests
    {
        private readonly string _database = "stomme_formats_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). The Formats store conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenFormatDefinitionStoreConformanceTests : FormatDefinitionStoreConformanceTests
    {
        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";
        private readonly string _database = "stomme-formats-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-formats-conformance-");
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

    public sealed class FileFormatDefinitionStoreConformanceTests : FormatDefinitionStoreConformanceTests
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-formats-file-conformance-{Guid.NewGuid():N}");

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
