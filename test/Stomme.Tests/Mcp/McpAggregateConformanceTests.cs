#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mcp.Persistence;
using Stomme.Modules.Mcp.Domain;
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

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The reshaped registration aggregate and the audit aggregate round-trip member for member on
    /// EVERY module-aggregate provider - EF (SQLite), EF (PostgreSQL), RavenDB and File - through the module's own
    /// <c>ServerStartup</c> and closed repositories, and every query those repositories expose answers identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D101's F-A: "persistence is trivial" was FALSIFIED - the predecessor platform's aggregate could not be model-built on a relational
    /// install at all (a multi-field value object, a record, two collection properties, <c>DateTimeOffset</c>), and the
    /// shared context swallowed the throw. This suite is what says the reshape worked, on the store the reshape was for.
    /// </para>
    /// <para>
    /// <see cref="DateTime.Kind"/> is asserted EXPLICITLY: equality ignores it, so a suite that only compared instants
    /// would be green on exactly the divergence the aggregates re-stamp to hide. PostgreSQL and RavenDB FAIL rather than
    /// skip. In <c>Stomme.Tests</c> because the conformance project has no File-store module fixture (the BL-78
    /// F11 precedent) - D101 named the conformance project; this is where its three predecessors went for that reason.
    /// </para>
    /// </remarks>
    public abstract class McpAggregateConformanceTests : IAsyncLifetime
    {
        // Millisecond precision: PostgreSQL keeps microseconds, so finer ticks would not survive every provider.
        private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, 250, DateTimeKind.Utc);
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
            new Stomme.Modules.Mcp.Startup.ServerStartup().ConfigureServices(services);
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

        // Every call in its own DI scope: a read must come from the STORE, never from the instance that was written.
        private async Task<T> InScopeAsync<TRepository, T>(Func<TRepository, Task<T>> action)
            where TRepository : notnull
        {
            await using var scope = _services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<TRepository>());
        }

        private Task SaveAsync(McpServerRegistrationAggregate registration) =>
            InScopeAsync<IMcpServerRegistrationRepository, int>(async r => { await r.AddAsync(registration); return 0; });

        private Task SaveAsync(McpAuditEntryAggregate entry) =>
            InScopeAsync<IMcpAuditRepository, int>(async r => { await r.AddAsync(entry); return 0; });

        private static McpServerRegistrationAggregate Registration(int siteId, string name, int? cap = null) =>
            McpServerRegistrationAggregate.Register(
                Guid.NewGuid(), TenantId.Default, siteId, name, McpServerTransport.Create("https://mcp.example.com/" + Guid.NewGuid().ToString("N")),
                cap is null ? null : McpGuardrail.Of(cap), Now);

        private static McpAuditEntryAggregate Entry(int siteId, DateTime at, McpAuditOutcome outcome = McpAuditOutcome.Succeeded) =>
            McpAuditEntryAggregate.Record(Guid.NewGuid(), TenantId.Default, siteId, 42, McpAuditVerb.ToolCall, "list_things", outcome, "{\"q\":\"x\"}", at);

        // ---- anti-vacuity ------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<McpServerRegistrationAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<McpServerRegistrationAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        // ---- round trips -------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_registration_round_trips_member_for_member_with_its_three_value_objects()
        {
            var registration = Registration(Site, "  Docs Server  ", cap: 120);
            registration.MarkApiKey(true);
            registration.ReplaceDiscoveredTools(DiscoveredToolSet.Of(new[]
            {
                new DiscoveredTool("search", "Finds \"things\" - and 'others'.", "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}"),
                new DiscoveredTool("fetch", null, null),
            }), Now.AddMinutes(5));
            await SaveAsync(registration);

            var read = (await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByIdAsync(registration.Id)))!;

            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal(Site, read.SiteId);
            Assert.Equal("Docs Server", read.Name);
            Assert.Equal("DOCS SERVER", read.NameNormalized);
            Assert.Equal(registration.Transport, read.Transport);
            Assert.Equal(registration.Transport.Value, read.Transport.Value);
            Assert.Equal(120, read.Guardrail.MaxRequestsPerMinute);
            Assert.True(read.HasApiKey);
            Assert.True(read.Enabled);
            Assert.Equal(McpServerTrustState.Verified, read.TrustState);

            // The tool set came back as a VALUE OBJECT holding tools - not a string, not an empty list.
            Assert.Equal(registration.DiscoveredTools, read.DiscoveredTools);
            Assert.Equal(new[] { "search", "fetch" }, read.DiscoveredTools.Tools().Select(tool => tool.Name));
            Assert.Equal("Finds \"things\" - and 'others'.", read.DiscoveredTools.Tools()[0].Description);
            Assert.Equal(2, read.ExposedTools().Count);

            Assert.Equal(Now, read.RegisteredOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.RegisteredOnUtc.Kind);
            Assert.Equal(Now.AddMinutes(5), read.LastRefreshedOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.LastRefreshedOnUtc!.Value.Kind);
        }

        [Fact]
        public async Task A_fresh_registration_round_trips_its_defaults_and_its_NULL_refresh_instant()
        {
            var registration = Registration(Site, "Fresh");
            await SaveAsync(registration);

            var read = (await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByIdAsync(registration.Id)))!;

            Assert.Null(read.LastRefreshedOnUtc);
            Assert.False(read.HasApiKey);
            Assert.Equal(McpServerTrustState.Unverified, read.TrustState);
            Assert.Null(read.Guardrail.MaxRequestsPerMinute);
            Assert.Empty(read.DiscoveredTools.Tools());
            Assert.Empty(read.ExposedTools());
        }

        [Fact]
        public async Task An_update_through_a_fresh_scope_persists_and_a_delete_removes_the_row()
        {
            var registration = Registration(Site, "Changing");
            await SaveAsync(registration);

            await InScopeAsync<IMcpServerRegistrationRepository, int>(async r =>
            {
                var loaded = (await r.GetForSiteAsync(registration.Id, Site))!;
                loaded.Disable();
                loaded.SetGuardrail(McpGuardrail.Of(5));
                loaded.ReplaceDiscoveredTools(DiscoveredToolSet.Of(new[] { new DiscoveredTool("only", null, null) }), Now.AddHours(1));
                await r.UpdateAsync(loaded);
                return 0;
            });

            var after = (await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByIdAsync(registration.Id)))!;
            Assert.False(after.Enabled);
            Assert.Equal(5, after.Guardrail.MaxRequestsPerMinute);
            Assert.Equal("only", after.DiscoveredTools.Tools().Single().Name);
            Assert.Empty(after.ExposedTools());

            await InScopeAsync<IMcpServerRegistrationRepository, int>(async r => { await r.DeleteAsync((await r.GetByIdAsync(registration.Id))!); return 0; });
            Assert.Null(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByIdAsync(registration.Id)));
        }

        [Fact]
        public async Task An_audit_entry_round_trips_member_for_member()
        {
            var entry = McpAuditEntryAggregate.Record(
                Guid.NewGuid(), TenantId.Default, Site, 42, McpAuditVerb.ResourceRead, "stomme://site", McpAuditOutcome.ConfirmDenied, "{\"a\":1}", Now);
            await SaveAsync(entry);

            var read = (await InScopeAsync<IMcpAuditRepository, McpAuditEntryAggregate?>(r => r.GetByIdAsync(entry.Id)))!;

            Assert.Equal(Site, read.SiteId);
            Assert.Equal(42, read.UserId);
            Assert.Equal(McpAuditVerb.ResourceRead, read.Verb);
            Assert.Equal("stomme://site", read.Subject);
            Assert.Equal(McpAuditOutcome.ConfirmDenied, read.Outcome);
            Assert.Equal(entry.ArgumentsSha256, read.ArgumentsSha256);
            Assert.Equal(7, read.ArgumentsLength);
            Assert.Equal(Now, read.OccurredOnUtc);
            Assert.Equal(DateTimeKind.Utc, read.OccurredOnUtc.Kind);
        }

        // ---- the queries, one answer on every provider ---------------------------------------------------------------

        [Fact]
        public async Task A_by_id_read_gives_ONE_answer_for_a_missing_registration_and_another_sites()
        {
            var mine = Registration(Site, "Mine");
            var theirs = Registration(OtherSite, "Theirs");
            await SaveAsync(mine);
            await SaveAsync(theirs);

            Assert.NotNull(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetForSiteAsync(mine.Id, Site)));
            // BOTH sites are in ONE tenant here - the default install shape, and the one predecessor-platform's by-id handlers leaked across.
            Assert.Null(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetForSiteAsync(theirs.Id, Site)));
            Assert.Null(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetForSiteAsync(Guid.NewGuid(), Site)));
            Assert.NotNull(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetForSiteAsync(theirs.Id, OtherSite)));
        }

        [Fact]
        public async Task The_registration_queries_answer_by_site_enabled_and_a_NORMALISED_name()
        {
            var on = Registration(Site, "Docs Server");
            var off = Registration(Site, "Archive");
            off.Disable();
            var foreign = Registration(OtherSite, "Docs Server");
            foreach (var registration in new[] { on, off, foreign })
            {
                await SaveAsync(registration);
            }

            Assert.Equal(new[] { "Archive", "Docs Server" },
                (await InScopeAsync<IMcpServerRegistrationRepository, IReadOnlyList<McpServerRegistrationAggregate>>(r => r.ListBySiteAsync(Site))).Select(x => x.Name).OrderBy(n => n));
            Assert.Equal(on.Id, Assert.Single(await InScopeAsync<IMcpServerRegistrationRepository, IReadOnlyList<McpServerRegistrationAggregate>>(r => r.ListEnabledAsync(Site))).Id);

            // Normalised on BOTH sides: whatever the caller's casing or padding, and never another site's row.
            foreach (var asked in new[] { "Docs Server", "docs server", "  DOCS SERVER  " })
            {
                Assert.Equal(on.Id, (await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByNameAsync(Site, asked)))!.Id);
            }

            Assert.Equal(foreign.Id, (await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByNameAsync(OtherSite, "docs server")))!.Id);
            Assert.Null(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByNameAsync(Site, "Docs")));
            Assert.Null(await InScopeAsync<IMcpServerRegistrationRepository, McpServerRegistrationAggregate?>(r => r.GetByNameAsync(Site, "  ")));
        }

        [Fact]
        public async Task The_audit_queries_answer_by_site_and_age_and_refuse_a_non_UTC_instant()
        {
            var old = Entry(Site, Now.AddDays(-100), McpAuditOutcome.Denied);
            var edge = Entry(Site, Now.AddDays(-90));
            var recent = Entry(Site, Now);
            var foreign = Entry(OtherSite, Now.AddDays(-100));
            foreach (var entry in new[] { old, edge, recent, foreign })
            {
                await SaveAsync(entry);
            }

            Assert.Equal(3, (await InScopeAsync<IMcpAuditRepository, IReadOnlyList<McpAuditEntryAggregate>>(r => r.ListBySiteAsync(Site))).Count);

            // The boundary instant is "since", and is NOT "older than": nothing is both kept and purged, nothing is neither.
            var cutoff = Now.AddDays(-90);
            Assert.Equal(new[] { edge.Id, recent.Id }.OrderBy(id => id),
                (await InScopeAsync<IMcpAuditRepository, IReadOnlyList<McpAuditEntryAggregate>>(r => r.ListSinceAsync(Site, cutoff))).Select(e => e.Id).OrderBy(id => id));
            Assert.Equal(old.Id, Assert.Single(await InScopeAsync<IMcpAuditRepository, IReadOnlyList<McpAuditEntryAggregate>>(r => r.ListOlderThanAsync(Site, cutoff))).Id);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                InScopeAsync<IMcpAuditRepository, IReadOnlyList<McpAuditEntryAggregate>>(r => r.ListOlderThanAsync(Site, DateTime.SpecifyKind(Now, DateTimeKind.Local))));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                InScopeAsync<IMcpAuditRepository, IReadOnlyList<McpAuditEntryAggregate>>(r => r.ListSinceAsync(Site, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified))));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteMcpAggregateConformanceTests : McpAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-mcp-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresMcpAggregateConformanceTests : McpAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_mcp_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The MCP aggregate conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenMcpAggregateConformanceTests : McpAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-mcp-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-mcp-conformance-");
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
    public sealed class FileMcpAggregateConformanceTests : McpAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-mcp-file-conformance-{Guid.NewGuid():N}");

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
