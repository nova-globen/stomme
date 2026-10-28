#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Domain;
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
    /// BL-79 (D102) - the four metering aggregates round-trip member for member on EVERY module-aggregate provider - EF
    /// (SQLite), EF (PostgreSQL), RavenDB and File - through the module's own <c>ServerStartup</c> and closed
    /// repositories, and every query those repositories expose answers identically on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installed-site drives prove the wiring on three stores; this proves the STORES agree. It exists for two
    /// things the row carried as assumptions: a filter on a NULLABLE <c>int?</c> (<c>UserId</c>) had been driven on SQLite
    /// alone, and the File provider - a module-aggregate provider, not an installable database - had never seen these
    /// aggregates at all.
    /// </para>
    /// <para>
    /// Instants are compared THROUGH <see cref="MeteringTime.AsUtc"/>, as the module itself reads them: a relational
    /// provider hands a scalar <c>DateTime</c> back with no kind, RavenDB and File hand back UTC, and this module's
    /// convention is "kind-less means UTC" rather than a re-stamp in the getter. What must agree is the INSTANT.
    /// PostgreSQL and RavenDB FAIL rather than skip (D46). Placed in <c>Stomme.Tests</c>: the conformance project has
    /// no File-store module fixture (the BL-78 F11 precedent).
    /// </para>
    /// </remarks>
    public abstract class MeteringAggregateConformanceTests : IAsyncLifetime
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
            new Stomme.Modules.Metering.Startup.ServerStartup().ConfigureServices(services);
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

        private static UsageRecordAggregate Usage(int siteId, string metric, long quantity, int? userId, DateTime at) =>
            UsageRecordAggregate.Append(Guid.NewGuid(), TenantId.Default, siteId, metric, quantity, userId, "tests", Guid.NewGuid(), at);

        private static AiUsageRecordAggregate AiCall(int siteId, int? userId, DateTime at, AiCallStatus status = AiCallStatus.Success) =>
            AiUsageRecordAggregate.Append(
                Guid.NewGuid(), TenantId.Default, siteId, "OpenAI", "GPT-X", "Summariser", 1_000, 2_000, 150, status,
                1.234567m, "eur", 0.75, userId, Guid.NewGuid(), at);

        // ---- anti-vacuity ------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_store_under_test_is_the_named_provider()
        {
            await using var scope = _services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IRepository<QuotaAggregate, Guid>>();
            var name = inner.GetType().Name;
            if (inner is Stomme.Persistence.EfCore.EfRepository<QuotaAggregate, Guid>)
            {
                name = scope.ServiceProvider.GetRequiredService<Stomme.Persistence.EfCore.ModuleAggregatesDbContext>().Database.ProviderName ?? name;
            }

            Assert.Contains(ExpectedStore, name, StringComparison.OrdinalIgnoreCase);
        }

        // ---- round trips -------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_quota_round_trips_member_for_member_and_an_update_persists()
        {
            var id = QuotaIds.For(TenantId.Default, Site, "tests.units", QuotaPeriod.Monthly);
            var quota = QuotaAggregate.Define(id, TenantId.Default, Site, "tests.units", QuotaPeriod.Monthly, 3_000_000_000L, 80, Now);
            quota.Apply(2_500_000_000L, Now);                       // past int range, and past the 80% threshold
            quota.GrantOverride(500, Now.AddHours(6));
            await AddAsync<IQuotaRepository, QuotaAggregate>(quota);

            var read = (await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetByIdAsync(id)))!;

            Assert.Equal(TenantId.Default, read.TenantId);
            Assert.Equal(Site, read.SiteId);
            Assert.Equal("tests.units", read.Metric);
            Assert.Equal(QuotaPeriod.Monthly, read.Period);
            Assert.Equal(3_000_000_000L, read.Limit);
            Assert.False(read.Unlimited);
            Assert.Equal(80, read.WarnAtPercent);
            Assert.Equal(2_500_000_000L, read.Consumed);
            Assert.Equal(QuotaStatus.Active, read.Status);
            Assert.Equal(500, read.OverrideAdditional);
            Assert.Equal(Now.AddHours(6), MeteringTime.AsUtc(read.OverrideExpiresUtc!.Value));
            Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), MeteringTime.AsUtc(read.PeriodStartUtc));
            Assert.True(read.WarningRaised);
            Assert.False(read.ExceededRaised);

            // An update through a FRESH scope - the shape a page and a job both use.
            await InScopeAsync<IQuotaRepository, int>(async r =>
            {
                var loaded = (await r.GetByIdAsync(id))!;
                loaded.Suspend();
                loaded.Redefine(QuotaAggregate.UnlimitedLimit, 0);
                await r.UpdateAsync(loaded);
                return 0;
            });

            var after = (await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetByIdAsync(id)))!;
            Assert.Equal(QuotaStatus.Suspended, after.Status);
            Assert.True(after.Unlimited);
            Assert.True(after.IsUnlimited());
            Assert.Equal(2_500_000_000L, after.Consumed);
        }

        [Fact]
        public async Task A_null_override_expiry_and_a_null_user_round_trip_as_NULL()
        {
            var id = QuotaIds.For(TenantId.Default, Site, "tests.nulls", QuotaPeriod.Total);
            await AddAsync<IQuotaRepository, QuotaAggregate>(QuotaAggregate.Define(id, TenantId.Default, Site, "tests.nulls", QuotaPeriod.Total, 5, 0, Now));
            var quota = (await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetByIdAsync(id)))!;
            Assert.Null(quota.OverrideExpiresUtc);

            var usage = Usage(Site, "tests.nulls", 1, userId: null, Now);
            await AddAsync<IUsageRecordRepository, UsageRecordAggregate>(usage);
            var read = (await InScopeAsync<IUsageRecordRepository, UsageRecordAggregate?>(r => r.GetByIdAsync(usage.Id)))!;
            Assert.Null(read.UserId);
            Assert.Equal(FoldState.Pending, read.FoldState);
            Assert.Equal("tests", read.Source);
            Assert.Equal(usage.OperationId, read.OperationId);
            Assert.Equal(Now, MeteringTime.AsUtc(read.OccurredOnUtc));
        }

        [Fact]
        public async Task An_AI_call_and_a_rollup_round_trip_with_their_decimals_EXACT()
        {
            var call = AiCall(Site, userId: 42, Now, AiCallStatus.Error);
            await AddAsync<IAiUsageRecordRepository, AiUsageRecordAggregate>(call);
            var readCall = (await InScopeAsync<IAiUsageRecordRepository, AiUsageRecordAggregate?>(r => r.GetByIdAsync(call.Id)))!;

            Assert.Equal("OpenAI", readCall.Provider);
            Assert.Equal("openai", readCall.ProviderNormalized);
            Assert.Equal("gpt-x", readCall.ModelNormalized);
            Assert.Equal("summariser", readCall.AgentNormalized);
            Assert.Equal(1_000, readCall.InputTokens);
            Assert.Equal(2_000, readCall.OutputTokens);
            Assert.Equal(150, readCall.LatencyMs);
            Assert.Equal(AiCallStatus.Error, readCall.Status);
            Assert.Equal(1.234567m, readCall.EstimatedCost);        // exactly: a cost that drifts is a bill that drifts
            Assert.Equal("EUR", readCall.CurrencyCode);
            Assert.Equal(0.75, readCall.QualityScore);
            Assert.Equal(42, readCall.UserId);
            Assert.Equal(call.OperationId, readCall.OperationId);
            Assert.Equal(Now, MeteringTime.AsUtc(readCall.OccurredOnUtc));

            // A cache hit's rule is the AGGREGATE's, so it survives every store.
            var cached = AiCall(Site, userId: null, Now, AiCallStatus.Cached);
            await AddAsync<IAiUsageRecordRepository, AiUsageRecordAggregate>(cached);
            var readCached = (await InScopeAsync<IAiUsageRecordRepository, AiUsageRecordAggregate?>(r => r.GetByIdAsync(cached.Id)))!;
            Assert.Equal(0m, readCached.EstimatedCost);
            Assert.Equal(0, readCached.BillableTokens());
            Assert.Equal(1_000, readCached.InputTokens);

            var day = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
            var rollup = AiUsageRollupAggregate.For(TenantId.Default, Site, day, "openai", "gpt-x", "summariser", "EUR");
            rollup.Set(calls: 3_000_000_000L, inputTokens: 5, outputTokens: 6, errors: 1, estimatedCost: 98765.432109m);
            await AddAsync<IAiUsageRollupRepository, AiUsageRollupAggregate>(rollup);
            var readRollup = (await InScopeAsync<IAiUsageRollupRepository, AiUsageRollupAggregate?>(r => r.GetByIdAsync(rollup.Id)))!;

            Assert.Equal(AiUsageRollupAggregate.IdFor(TenantId.Default, Site, day, "openai", "gpt-x", "summariser"), readRollup.Id);
            Assert.Equal(3_000_000_000L, readRollup.Calls);
            Assert.Equal(11, readRollup.BillableTokens());
            Assert.Equal(1, readRollup.Errors);
            Assert.Equal(98765.432109m, readRollup.EstimatedCost);
            Assert.Equal(day, MeteringTime.AsUtc(readRollup.DayUtc));
        }

        // ---- the queries, one answer on every provider ---------------------------------------------------------------

        [Fact]
        public async Task The_usage_queries_answer_by_site_window_fold_state_age_and_a_NULLABLE_user()
        {
            var early = Usage(Site, "tests.a", 1, userId: 42, Now.AddDays(-10));
            var middle = Usage(Site, "tests.a", 2, userId: null, Now.AddDays(-5));
            var late = Usage(Site, "tests.b", 3, userId: 42, Now);
            var applied = Usage(Site, "tests.a", 4, userId: 43, Now.AddDays(-5));
            applied.MarkApplied();
            var foreign = Usage(OtherSite, "tests.a", 5, userId: 42, Now.AddDays(-5));
            foreach (var row in new[] { early, middle, late, applied, foreign })
            {
                await AddAsync<IUsageRecordRepository, UsageRecordAggregate>(row);
            }

            static long[] Quantities(IEnumerable<UsageRecordAggregate> rows) => rows.Select(row => row.Quantity).OrderBy(q => q).ToArray();

            Assert.Equal(new long[] { 1, 2, 3, 4 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListBySiteAsync(Site))));

            // The window is half-open: [from, to).
            Assert.Equal(new long[] { 2, 4 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r =>
                r.ListForWindowAsync(Site, "tests.a", Now.AddDays(-5), Now))));
            Assert.Equal(new long[] { 1 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r =>
                r.ListForWindowAsync(Site, "tests.a", Now.AddDays(-10), Now.AddDays(-5)))));

            // The metric is normalised on BOTH sides (D49): a mixed-case argument finds the canonical rows.
            Assert.Equal(new long[] { 2, 4 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r =>
                r.ListForWindowAsync(Site, "Tests.A", Now.AddDays(-5), Now))));

            // Pending AND older than the cut-off: the applied row of the same instant is not pending.
            Assert.Equal(new long[] { 1, 2 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r =>
                r.ListPendingAsync(Site, Now.AddDays(-1)))));

            Assert.Equal(new long[] { 2, 3, 4 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListSinceAsync(Site, Now.AddDays(-5)))));
            Assert.Equal(new long[] { 1 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListOlderThanAsync(Site, Now.AddDays(-5)))));

            // THE case the row carried as an assumption: an int? column filtered by an int. The NULL row matches no user,
            // another user's row is not returned, and another SITE's row for the same user is not either.
            Assert.Equal(new long[] { 1, 3 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListByUserAsync(Site, 42))));
            Assert.Equal(new long[] { 4 }, Quantities(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListByUserAsync(Site, 43))));
            Assert.Empty(await InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListByUserAsync(Site, 999)));
        }

        [Fact]
        public async Task The_quota_AI_and_rollup_queries_answer_by_site_age_and_user()
        {
            var quotaId = QuotaIds.For(TenantId.Default, Site, "tests.q", QuotaPeriod.Daily);
            await AddAsync<IQuotaRepository, QuotaAggregate>(QuotaAggregate.Define(quotaId, TenantId.Default, Site, "tests.q", QuotaPeriod.Daily, 1, 0, Now));
            var foreignQuotaId = QuotaIds.For(TenantId.Default, OtherSite, "tests.q", QuotaPeriod.Daily);
            await AddAsync<IQuotaRepository, QuotaAggregate>(QuotaAggregate.Define(foreignQuotaId, TenantId.Default, OtherSite, "tests.q", QuotaPeriod.Daily, 1, 0, Now));

            Assert.Single(await InScopeAsync<IQuotaRepository, IReadOnlyList<QuotaAggregate>>(r => r.ListBySiteAsync(Site)));
            Assert.NotNull(await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetAsync(TenantId.Default, Site, "Tests.Q", QuotaPeriod.Daily)));
            Assert.Null(await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetAsync(TenantId.Default, Site, "tests.q", QuotaPeriod.Monthly)));
            // ONE answer for "no such quota" and "another site's quota".
            Assert.Null(await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetForCurrentSiteAsync(foreignQuotaId, Site)));
            Assert.Null(await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetForCurrentSiteAsync(Guid.NewGuid(), Site)));
            Assert.NotNull(await InScopeAsync<IQuotaRepository, QuotaAggregate?>(r => r.GetForCurrentSiteAsync(quotaId, Site)));

            var old = AiCall(Site, userId: 42, Now.AddDays(-40));
            var recent = AiCall(Site, userId: null, Now);
            var foreign = AiCall(OtherSite, userId: 42, Now);
            foreach (var call in new[] { old, recent, foreign })
            {
                await AddAsync<IAiUsageRecordRepository, AiUsageRecordAggregate>(call);
            }

            Assert.Equal(2, (await InScopeAsync<IAiUsageRecordRepository, IReadOnlyList<AiUsageRecordAggregate>>(r => r.ListBySiteAsync(Site))).Count);
            Assert.Equal(recent.Id, Assert.Single(await InScopeAsync<IAiUsageRecordRepository, IReadOnlyList<AiUsageRecordAggregate>>(r => r.ListSinceAsync(Site, Now.AddDays(-30)))).Id);
            Assert.Equal(old.Id, Assert.Single(await InScopeAsync<IAiUsageRecordRepository, IReadOnlyList<AiUsageRecordAggregate>>(r => r.ListOlderThanAsync(Site, Now.AddDays(-30)))).Id);
            Assert.Equal(old.Id, Assert.Single(await InScopeAsync<IAiUsageRecordRepository, IReadOnlyList<AiUsageRecordAggregate>>(r => r.ListByUserAsync(Site, 42))).Id);

            var today = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
            var rollupToday = AiUsageRollupAggregate.For(TenantId.Default, Site, today, "openai", "gpt-x", "a", "USD");
            var rollupOld = AiUsageRollupAggregate.For(TenantId.Default, Site, today.AddDays(-40), "openai", "gpt-x", "a", "USD");
            var rollupForeign = AiUsageRollupAggregate.For(TenantId.Default, OtherSite, today, "openai", "gpt-x", "a", "USD");
            foreach (var rollup in new[] { rollupToday, rollupOld, rollupForeign })
            {
                await AddAsync<IAiUsageRollupRepository, AiUsageRollupAggregate>(rollup);
            }

            Assert.Equal(2, (await InScopeAsync<IAiUsageRollupRepository, IReadOnlyList<AiUsageRollupAggregate>>(r => r.ListBySiteAsync(Site))).Count);
            // The boundary day itself is "since".
            Assert.Equal(rollupToday.Id, Assert.Single(await InScopeAsync<IAiUsageRollupRepository, IReadOnlyList<AiUsageRollupAggregate>>(r => r.ListSinceAsync(Site, today))).Id);
            Assert.Equal(rollupOld.Id, Assert.Single(await InScopeAsync<IAiUsageRollupRepository, IReadOnlyList<AiUsageRollupAggregate>>(r => r.ListOlderThanAsync(Site, today))).Id);
        }

        [Fact]
        public async Task A_non_UTC_instant_is_refused_by_the_queries_and_a_delete_removes_the_row()
        {
            var local = DateTime.SpecifyKind(Now, DateTimeKind.Local);
            await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync<IUsageRecordRepository, IReadOnlyList<UsageRecordAggregate>>(r => r.ListSinceAsync(Site, local)));
            await Assert.ThrowsAsync<ArgumentException>(() => InScopeAsync<IAiUsageRollupRepository, IReadOnlyList<AiUsageRollupAggregate>>(r => r.ListOlderThanAsync(Site, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified))));

            var row = Usage(Site, "tests.gone", 1, userId: 1, Now);
            await AddAsync<IUsageRecordRepository, UsageRecordAggregate>(row);
            await InScopeAsync<IUsageRecordRepository, int>(async r => { await r.DeleteAsync((await r.GetByIdAsync(row.Id))!); return 0; });
            Assert.Null(await InScopeAsync<IUsageRecordRepository, UsageRecordAggregate?>(r => r.GetByIdAsync(row.Id)));
        }
    }

    /// <summary>EF Core over a throwaway SQLite file - the default module-aggregate store of a stock install.</summary>
    public sealed class SqliteMeteringAggregateConformanceTests : MeteringAggregateConformanceTests
    {
        protected override string ExpectedStore => "Sqlite";

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-metering-conformance-{Guid.NewGuid():N}.db")}";

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
    public sealed class PostgresMeteringAggregateConformanceTests : MeteringAggregateConformanceTests
    {
        protected override string ExpectedStore => "Npgsql";

        private readonly string _database = "stomme_metering_conformance_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The metering aggregate conformance suite FAILS rather than skips (D46).");
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
    public sealed class RavenMeteringAggregateConformanceTests : MeteringAggregateConformanceTests
    {
        protected override string ExpectedStore => "RavenRepository";

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-metering-conformance-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-metering-conformance-");
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
    public sealed class FileMeteringAggregateConformanceTests : MeteringAggregateConformanceTests
    {
        protected override string ExpectedStore => "FileRepository";

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-metering-file-conformance-{Guid.NewGuid():N}");

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
