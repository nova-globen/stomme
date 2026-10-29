#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Domain;
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
    /// The two health aggregates round-trip member for member on EVERY module-aggregate provider - EF
    /// (SQLite), EF (PostgreSQL), RavenDB and File - through the module's own <c>ServerStartup</c> and closed repositories,
    /// and every query those repositories expose answers identically on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The installed-site drives (<c>InstalledSiteHealthDriveTests</c>, <c>HealthSiteIsolationTests</c>)
    /// cover SQLite, PostgreSQL and RavenDB - the three stores an install can choose. The File provider is a module-aggregate
    /// provider rather than an installable database, so no install reaches it, and until this suite it had never met these
    /// aggregates, so the File store was never driven. This is the coverage the metering, notification,
    /// help and MCP modules carry for the same provider; an INSTALLED File host needs a
    /// <c>FileGateStore</c> in the harness, which is a known shared harness gap.
    /// </para>
    /// <para>
    /// The alert row is the interesting one: four nullable instants, a nullable ENUM, a nullable string and a value object.
    /// Instants are compared THROUGH <see cref="HealthTime.AsUtc"/>, as the module reads them - a relational provider hands
    /// a scalar back with no kind, RavenDB and File hand back UTC. PostgreSQL and RavenDB FAIL rather than skip.
    /// </para>
    /// </remarks>
    public abstract class HealthAggregateConformanceTests : IAsyncLifetime
    {
        // Millisecond precision: PostgreSQL keeps microseconds, so finer ticks would not survive every provider.
        private static readonly DateTime Now = new(2026, 9, 23, 12, 34, 0, 250, DateTimeKind.Utc);
        private const int Site = 7;
        private const int OtherSite = 8;

        private static readonly HealthSubject Store = HealthSubject.For("stomme", "store");
        private static readonly HealthSubject Cache = HealthSubject.For("stomme", "cache");

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
            new Stomme.Modules.Health.Startup.ServerStartup().ConfigureServices(services);
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

        private Task AddAsync<TRepository, TAggregate>(TAggregate aggregate)
            where TRepository : IRepository<TAggregate, Guid>
            where TAggregate : AggregateRoot<Guid> =>
            InScopeAsync<TRepository, int>(async repository => { await repository.AddAsync(aggregate); return 0; });

        private static HealthRollupAggregate Rollup(HealthProbeScope scope, int siteId, HealthSubject subject, HealthRollupWindow window, DateTime start)
        {
            var row = HealthRollupAggregate.For(TenantId.Default, scope, siteId, subject, window, start);
            row.Set(checks: 60, healthy: 57, degraded: 2, unhealthy: 1, averageLatencyMs: 12.25, maxLatencyMs: 480.5, availabilityPercent: 95);
            return row;
        }

        // ---- anti-vacuity ------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<HealthAlertStateAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<HealthAlertStateAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        // ---- round trips -------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_rollup_round_trips_member_for_member_and_an_upsert_persists()
        {
            var row = Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, Now);
            await AddAsync<IHealthRollupRepository, HealthRollupAggregate>(row);

            var read = (await InScopeAsync<IHealthRollupRepository, HealthRollupAggregate?>(r => r.GetByIdAsync(row.Id)))!;

            Assert.Equal(HealthIds.ForRollup(TenantId.Default, HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, row.PeriodStartUtc), read.Id);
            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal(HealthProbeScope.Site, read.Scope);
            Assert.Equal(Site, read.SiteId);
            Assert.Equal(Store, read.Subject);                                       // the value object, by value
            Assert.Equal(HealthRollupWindow.Hour, read.Window);
            Assert.Equal(new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc), HealthTime.AsUtc(read.PeriodStartUtc));   // snapped
            Assert.Equal((60, 57, 2, 1), (read.Checks, read.Healthy, read.Degraded, read.Unhealthy));
            Assert.Equal(12.25, read.AverageLatencyMs);
            Assert.Equal(480.5, read.MaxLatencyMs);
            Assert.Equal(95d, read.AvailabilityPercent);

            // The job's write shape: GetById in a FRESH scope, change, Update.
            await InScopeAsync<IHealthRollupRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(row.Id))!;
                loaded.Set(checks: 61, healthy: 61, degraded: 0, unhealthy: 0, averageLatencyMs: 1.5, maxLatencyMs: 3, availabilityPercent: 100);
                await r.UpdateAsync(loaded);
                return 0;
            });

            var after = (await InScopeAsync<IHealthRollupRepository, HealthRollupAggregate?>(r => r.GetByIdAsync(row.Id)))!;
            Assert.Equal((61, 61, 0, 0), (after.Checks, after.Healthy, after.Degraded, after.Unhealthy));
            Assert.Equal(100d, after.AvailabilityPercent);
            Assert.Single(await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r => r.GetAllAsync()));
        }

        [Fact]
        public async Task An_alert_row_round_trips_its_NULLABLE_instants_enum_and_text_and_its_transitions_persist()
        {
            // Fresh: every nullable member is NULL, and must come back NULL rather than as a default.
            var fresh = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Site, Site, Store, Now);
            await AddAsync<IHealthAlertStateRepository, HealthAlertStateAggregate>(fresh);
            var readFresh = (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetByIdAsync(fresh.Id)))!;
            AssertSame(fresh, readFresh);

            // Driven through the transition machine in FRESH scopes - the job's shape - so every nullable member is set.
            await InScopeAsync<IHealthAlertStateRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(fresh.Id))!;
                loaded.Observe(HealthState.Unhealthy, "connection refused", Now);
                loaded.ScheduleRecheck(HealthState.Unhealthy, TimeSpan.FromMinutes(2), Now);
                await r.UpdateAsync(loaded);
                return 0;
            });
            var scheduled = (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetByIdAsync(fresh.Id)))!;
            Assert.Equal(HealthState.Unhealthy, scheduled.CurrentState);
            Assert.Equal(HealthState.Unhealthy, scheduled.ScheduledForState);         // a nullable ENUM, set
            Assert.Equal(Now, HealthTime.AsUtc(scheduled.BadSinceUtc!.Value));
            Assert.Equal(Now.AddMinutes(2), HealthTime.AsUtc(scheduled.NextEvaluationOnUtc!.Value));
            Assert.Equal("connection refused", scheduled.LastDescription);

            await InScopeAsync<IHealthAlertStateRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(fresh.Id))!;
                loaded.Raise(HealthState.Unhealthy, Now.AddMinutes(2));
                loaded.Acknowledge(Now.AddMinutes(3));
                await r.UpdateAsync(loaded);
                return 0;
            });

            var expected = (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetByIdAsync(fresh.Id)))!;
            Assert.True(expected.HasOutstandingAlert);
            Assert.Equal(1, expected.AlertCount);
            Assert.Equal(Now.AddMinutes(2), HealthTime.AsUtc(expected.AlertRaisedOnUtc!.Value));
            Assert.Equal(Now.AddMinutes(3), HealthTime.AsUtc(expected.AcknowledgedOnUtc!.Value));
            Assert.Null(expected.ScheduledForState);                                    // the nullable enum, set then NULL again
            Assert.Null(expected.NextEvaluationOnUtc);

            await InScopeAsync<IHealthAlertStateRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(fresh.Id))!;
                loaded.Observe(HealthState.Healthy, null, Now.AddMinutes(5));
                loaded.Clear(Now.AddMinutes(5), TimeSpan.FromMinutes(30));
                await r.UpdateAsync(loaded);
                return 0;
            });

            var cleared = (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetByIdAsync(fresh.Id)))!;
            Assert.False(cleared.HasOutstandingAlert);
            Assert.Equal(HealthState.Healthy, cleared.CurrentState);
            Assert.Null(cleared.BadSinceUtc);                                           // set, then back to NULL
            Assert.Equal(Now.AddMinutes(5), HealthTime.AsUtc(cleared.AlertClearedOnUtc!.Value));
            Assert.Equal(Now.AddMinutes(35), HealthTime.AsUtc(cleared.CooldownUntilUtc!.Value));
            Assert.True(cleared.InCooldown(Now.AddMinutes(10)));
            Assert.False(cleared.InCooldown(Now.AddMinutes(36)));
        }

        private static void AssertSame(HealthAlertStateAggregate expected, HealthAlertStateAggregate actual)
        {
            static DateTime? Utc(DateTime? value) => value is null ? null : HealthTime.AsUtc(value.Value);

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Equal(expected.Scope, actual.Scope);
            Assert.Equal(expected.SiteId, actual.SiteId);
            Assert.Equal(expected.Subject, actual.Subject);
            Assert.Equal(expected.CurrentState, actual.CurrentState);
            Assert.Equal(expected.LastAlertedState, actual.LastAlertedState);
            Assert.Equal(Utc(expected.BadSinceUtc), Utc(actual.BadSinceUtc));
            Assert.Equal(Utc(expected.NextEvaluationOnUtc), Utc(actual.NextEvaluationOnUtc));
            Assert.Equal(expected.ScheduledForState, actual.ScheduledForState);
            Assert.Equal(Utc(expected.AlertRaisedOnUtc), Utc(actual.AlertRaisedOnUtc));
            Assert.Equal(Utc(expected.AlertClearedOnUtc), Utc(actual.AlertClearedOnUtc));
            Assert.Equal(Utc(expected.CooldownUntilUtc), Utc(actual.CooldownUntilUtc));
            Assert.Equal(expected.AlertCount, actual.AlertCount);
            Assert.Equal(expected.LastDescription, actual.LastDescription);
            Assert.Equal(Utc(expected.AcknowledgedOnUtc), Utc(actual.AcknowledgedOnUtc));
        }

        [Fact]
        public async Task Every_instant_reads_back_with_KIND_Utc_not_merely_the_right_ticks()
        {
            // DateTime equality IGNORES Kind, so every assertion above - which compares through HealthTime.AsUtc, the
            // way the module reads - passes on a provider that hands back Unspecified. A later ToUniversalTime() or ISO
            // rendering then shifts by the host's offset on a non-UTC host. Asked RAW here, member by member.
            var rollup = Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, Now);
            await AddAsync<IHealthRollupRepository, HealthRollupAggregate>(rollup);

            var alert = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Site, Site, Store, Now);
            alert.Observe(HealthState.Unhealthy, "down", Now);
            alert.ScheduleRecheck(HealthState.Unhealthy, TimeSpan.FromMinutes(2), Now);
            alert.Clear(Now, TimeSpan.FromMinutes(30));
            await AddAsync<IHealthAlertStateRepository, HealthAlertStateAggregate>(alert);

            var readRollup = (await InScopeAsync<IHealthRollupRepository, HealthRollupAggregate?>(r => r.GetByIdAsync(rollup.Id)))!;
            var readAlert = (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetByIdAsync(alert.Id)))!;

            Assert.Equal(DateTimeKind.Utc, readRollup.PeriodStartUtc.Kind);                  // a DateTime
            Assert.Equal(DateTimeKind.Utc, readAlert.BadSinceUtc!.Value.Kind);               // DateTime? members, set
            Assert.Equal(DateTimeKind.Utc, readAlert.AlertClearedOnUtc!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, readAlert.CooldownUntilUtc!.Value.Kind);
            Assert.Equal(Now, readAlert.BadSinceUtc.Value);
        }

        // ---- the queries, one answer on every provider ---------------------------------------------------------------

        [Fact]
        public async Task The_rollup_queries_answer_by_scope_site_window_and_a_HALF_OPEN_range()
        {
            var hour = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
            var rows = new[]
            {
                Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, hour.AddHours(-3)),
                Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, hour.AddHours(-2)),
                Rollup(HealthProbeScope.Site, Site, Cache, HealthRollupWindow.Hour, hour.AddHours(-1)),
                Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Hour, hour),
                Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Day, hour),                  // another window
                Rollup(HealthProbeScope.Site, OtherSite, Store, HealthRollupWindow.Hour, hour.AddHours(-2)), // another site
                Rollup(HealthProbeScope.Tenant, 0, Store, HealthRollupWindow.Hour, hour.AddHours(-2)),      // another scope
            };
            foreach (var row in rows)
            {
                await AddAsync<IHealthRollupRepository, HealthRollupAggregate>(row);
            }

            static DateTime[] Starts(IEnumerable<HealthRollupAggregate> found) => found.Select(row => HealthTime.AsUtc(row.PeriodStartUtc)).ToArray();

            // Newest window first.
            Assert.Equal(new[] { hour, hour.AddHours(-1), hour.AddHours(-2), hour.AddHours(-3) },
                Starts(await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r => r.ListAsync(HealthProbeScope.Site, Site, HealthRollupWindow.Hour))));

            // [from, to), oldest first: the boundary hour at `to` is OUT, the one at `from` is IN.
            Assert.Equal(new[] { hour.AddHours(-2), hour.AddHours(-1) },
                Starts(await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                    r.ListForRangeAsync(HealthProbeScope.Site, Site, HealthRollupWindow.Hour, hour.AddHours(-2), hour))));

            Assert.Equal(new[] { hour.AddHours(-3), hour.AddHours(-2) },
                Starts(await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                    r.ListOlderThanAsync(HealthProbeScope.Site, Site, HealthRollupWindow.Hour, hour.AddHours(-1)))).OrderBy(start => start).ToArray());

            // The retention sweep: any scope of THIS site id, one window. Site 0 carries the tenant-scope row.
            Assert.Equal(3, (await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                r.ListOlderThanAnywhereAsync(Site, HealthRollupWindow.Hour, hour))).Count);
            Assert.Equal(HealthProbeScope.Tenant, Assert.Single(await InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                r.ListOlderThanAnywhereAsync(0, HealthRollupWindow.Hour, hour))).Scope);
        }

        [Fact]
        public async Task The_alert_queries_answer_by_site_and_the_by_id_read_gives_ONE_answer_for_missing_and_foreign()
        {
            var mine = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Site, Site, Store, Now);
            var mineToo = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Site, Site, Cache, Now);
            var foreign = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Site, OtherSite, Store, Now);
            var shared = HealthAlertStateAggregate.For(TenantId.Default, HealthProbeScope.Tenant, 0, Store, Now);
            foreach (var row in new[] { mine, mineToo, foreign, shared })
            {
                await AddAsync<IHealthAlertStateRepository, HealthAlertStateAggregate>(row);
            }

            Assert.Equal(new[] { mine.Id, mineToo.Id }.OrderBy(id => id),
                (await InScopeAsync<IHealthAlertStateRepository, IReadOnlyList<HealthAlertStateAggregate>>(r => r.ListBySiteAsync(Site))).Select(row => row.Id).OrderBy(id => id));

            Assert.Equal(mine.Id, (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r =>
                r.GetAsync(TenantId.Default, HealthProbeScope.Site, Site, Store)))!.Id);
            Assert.Equal(shared.Id, (await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r =>
                r.GetAsync(TenantId.Default, HealthProbeScope.Tenant, 0, Store)))!.Id);
            Assert.Null(await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r =>
                r.GetAsync(TenantId.Default, HealthProbeScope.Site, Site, HealthSubject.For("stomme", "smtp"))));

            Assert.NotNull(await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetForCurrentSiteAsync(mine.Id, Site)));
            Assert.Null(await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetForCurrentSiteAsync(foreign.Id, Site)));
            Assert.Null(await InScopeAsync<IHealthAlertStateRepository, HealthAlertStateAggregate?>(r => r.GetForCurrentSiteAsync(Guid.NewGuid(), Site)));
        }

        [Fact]
        public async Task A_non_UTC_instant_is_refused_by_the_queries_and_a_delete_removes_the_row()
        {
            var local = DateTime.SpecifyKind(Now, DateTimeKind.Local);
            await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                r.ListOlderThanAsync(HealthProbeScope.Site, Site, HealthRollupWindow.Hour, local)));
            await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync<IHealthRollupRepository, IReadOnlyList<HealthRollupAggregate>>(r =>
                r.ListOlderThanAnywhereAsync(Site, HealthRollupWindow.Hour, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified))));

            var row = Rollup(HealthProbeScope.Site, Site, Store, HealthRollupWindow.Minute, Now);
            await AddAsync<IHealthRollupRepository, HealthRollupAggregate>(row);
            await InScopeAsync<IHealthRollupRepository, int>(async r => { await r.DeleteAsync((await r.GetByIdAsync(row.Id))!); return 0; });
            Assert.Null(await InScopeAsync<IHealthRollupRepository, HealthRollupAggregate?>(r => r.GetByIdAsync(row.Id)));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteHealthAggregateConformanceTests : HealthAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-health-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresHealthAggregateConformanceTests : HealthAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_health_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The health aggregate conformance suite FAILS rather than skips (D46).");
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
    /// EF Core on a LIVE SQL Server scratch database (the shared test server, <see cref="SqlServerTestEndpoint"/>) - the first module-aggregate
    /// suite on SQL Server, added to measure it. Fails, never skips, when SQL Server is down.
    /// </summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerHealthAggregateConformanceTests : HealthAggregateConformanceTests
    {
        protected override string ExpectedStore => "SqlServer";

        private readonly string _database = "stomme_health_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(SqlServerTestEndpoint.IsReachable(),
                $"SQL Server is not reachable at {SqlServerTestEndpoint.Host}:{SqlServerTestEndpoint.Port} (SQLSERVER_TEST_HOST/PORT). " +
                "The health aggregate conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenHealthAggregateConformanceTests : HealthAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-health-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-health-conformance-");
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
    public sealed class FileHealthAggregateConformanceTests : HealthAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-health-file-conformance-{Guid.NewGuid():N}");

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
