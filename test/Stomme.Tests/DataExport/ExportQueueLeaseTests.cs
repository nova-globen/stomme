#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Persistence.Abstractions;
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
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The export queue's lease, driven against real stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim under test is that NOTHING but the store's concurrency check makes a claim exactly-once: there is
    /// no lock and no semaphore, because the host registers no <c>IDistributedLock</c> on any provider. So each
    /// provider is asked the question directly - two scopes load one Queued job, both claim, both save - and the
    /// processor is then raced against itself over many jobs.
    /// </para>
    /// <para>
    /// <b>The File store is asserted SINGLE-HOST, not assumed safe:</b> it has no concurrency check, the stale
    /// write succeeds, and that case pins the divergence so nobody later reads "four providers green" as "four
    /// providers safe across hosts". PostgreSQL and RavenDB FAIL rather than skip.
    /// </para>
    /// </remarks>
    public abstract class ExportQueueLeaseTests : IAsyncLifetime
    {
        private const int SiteId = 7;

        private readonly ITestOutputHelper _output;
        private ServiceProvider _services = default!;
        private readonly TestClock _clock = new();
        private readonly TestSource _source = new();
        private readonly TestArtifactStore _artifacts = new();
        private readonly TestAuthorizer _authorizer = new();
        private readonly CapturingDispatcher _dispatcher = new();
        private readonly DataExportOptions _options = new() { Enabled = true, HeartbeatRows = 10, LeaseMinutes = 10 };

        protected ExportQueueLeaseTests(ITestOutputHelper output) => _output = output;

        /// <summary>Whether this store rejects a stale write. False is a statement about the File store, not a bug.</summary>
        protected abstract bool HasConcurrencyCheck { get; }

        protected abstract void AddStore(IServiceCollection services);

        protected virtual Task InitializeStoreAsync() => Task.CompletedTask;

        protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

        public async Task InitializeAsync()
        {
            await InitializeStoreAsync();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            AddStore(services);

            // Registered BEFORE the module startup, whose own registrations are all TryAdd: the test doubles win.
            services.AddSingleton<IExportClock>(_clock);
            services.AddSingleton(_options);
            services.AddSingleton<IExportSource>(_source);
            services.AddSingleton<IExportArtifactStore>(_artifacts);
            services.AddSingleton<IExportJobAuthorizer>(_authorizer);
            services.AddSingleton<Stomme.Notifications.INotificationDispatcher>(_dispatcher);

            // This fixture composes the MODULE alone, without the framework. The module startup also registers the built-in
            // event-log source, which reads the framework's ILogRepository - present in every real host, absent here. The
            // registry constructs every source, so the stand-in lets it; no case in this suite exports the log.
            services.AddSingleton<Stomme.Repository.ILogRepository>(ThrowawayLogRepository.Instance);

            new Stomme.Modules.DataExport.Startup.ServerStartup().ConfigureServices(services);
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

        // ---- the store's answer, asked directly --------------------------------------------------------------

        [Fact]
        public async Task Two_scopes_that_both_claim_one_job_the_second_SAVE_is_rejected()
        {
            var job = await QueueAsync();

            await using var scopeA = _services.CreateAsyncScope();
            await using var scopeB = _services.CreateAsyncScope();
            var repositoryA = scopeA.ServiceProvider.GetRequiredService<IExportJobRepository>();
            var repositoryB = scopeB.ServiceProvider.GetRequiredService<IExportJobRepository>();

            // Both load the job while it is still Queued - the state two hosts are in a millisecond apart.
            var a = (await repositoryA.GetByIdAsync(job.Id))!;
            var b = (await repositoryB.GetByIdAsync(job.Id))!;
            Assert.NotSame(a, b);

            a.Claim(_clock.UtcNow, _options.Lease);
            b.Claim(_clock.UtcNow, _options.Lease);

            await repositoryA.UpdateAsync(a);

            if (HasConcurrencyCheck)
            {
                await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repositoryB.UpdateAsync(b));
                Assert.Equal(1, (await ReadAsync(job.Id)).Attempts);
            }
            else
            {
                // SINGLE-HOST ONLY: the File store has no check, so the stale write lands. One host drains its
                // tenants serially (HostedServiceBase), which is the only reason this is sound.
                await repositoryB.UpdateAsync(b);
                _output.WriteLine("[file] the stale claim was accepted - this store is safe on ONE host only");
            }
        }

        [Fact]
        public async Task Two_drains_racing_over_many_jobs_render_each_job_EXACTLY_once()
        {
            const int jobs = 12;
            var ids = new List<Guid>();
            for (var i = 0; i < jobs; i++)
            {
                ids.Add((await QueueAsync()).Id);
            }

            ExportDrainResult[] results;
            if (HasConcurrencyCheck)
            {
                // Hold every render at its first row until both drains have had the chance to claim, so the race
                // is on the CLAIM rather than settled by whichever drain happened to start first.
                _source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var drainA = Task.Run(() => DrainAsync());
                var drainB = Task.Run(() => DrainAsync());
                await Task.Delay(300);
                _source.Gate.SetResult();
                results = await Task.WhenAll(drainA, drainB);
            }
            else
            {
                // The File store is SINGLE-HOST, and one host drains its tenants serially - so back-to-back drains
                // are the only shape it ever sees, and the one asserted. Racing it would prove nothing: MEASURED,
                // two concurrent writers to one document collide with an IOException from the store itself (a
                // loud failure, not a silent lost update), which is the store's contract, not the processor's.
                results = new[] { await DrainAsync(), await DrainAsync() };
                Assert.Equal(new ExportDrainResult(0, 0, 0, 0), results[1]);
            }

            _output.WriteLine($"[race] A={results[0]} B={results[1]}");
            // Anti-vacuity: if one drain simply took everything, nothing was contended and exactly-once proves
            // nothing. A lost claim is the concurrency check being seen to work.
            if (HasConcurrencyCheck)
            {
                Assert.True(results.Sum(r => r.LostRace) > 0, "no claim was contended - the race did not happen, so this run proves nothing");
            }

            Assert.Equal(jobs, results.Sum(r => r.Completed));
            Assert.All(ids, id => Assert.Equal(1, _source.OpensFor(id)));
            Assert.All(ids, id => Assert.Equal(1, _artifacts.CommitsFor(id)));

            foreach (var id in ids)
            {
                var job = await ReadAsync(id);
                Assert.Equal(ExportJobStatus.Ready, job.Status);
                Assert.Equal(1, job.Attempts);
                Assert.Equal(TestSource.DefaultRows, job.RowCount);
            }
        }

        // ---- the heartbeat -----------------------------------------------------------------------------------

        [Fact]
        public async Task A_render_LONGER_than_the_lease_is_not_reclaimed_because_it_heartbeats()
        {
            var job = await QueueAsync();

            // Every pulse (10 rows) moves the clock 4 minutes: the 100-row render "takes" 40 minutes against a
            // 10-minute lease. Without the heartbeat the lease would lapse four times over.
            var observed = new List<bool>();
            _source.OnRow = async row =>
            {
                if (row % 10 == 0)
                {
                    _clock.Advance(TimeSpan.FromMinutes(4));
                    observed.Add((await ReadAsync(job.Id)).IsLeaseExpired(_clock.UtcNow));
                }
            };

            var result = await DrainAsync();

            Assert.Equal(1, result.Completed);
            Assert.NotEmpty(observed);
            // Sampled just AFTER the clock jumped and BEFORE the pulse renewed: 4 minutes into a 10-minute lease.
            Assert.DoesNotContain(true, observed);

            var done = await ReadAsync(job.Id);
            Assert.Equal(1, done.Attempts);
            Assert.True(done.HeartbeatOnUtc > done.StartedOnUtc, "the heartbeat never moved");
        }

        [Fact]
        public async Task A_DEAD_lease_is_taken_over_and_each_attempt_writes_its_OWN_destination()
        {
            var job = await QueueAsync();

            // A renderer that died: claimed, then silence.
            await InScopeAsync(async repository =>
            {
                var loaded = (await repository.GetByIdAsync(job.Id))!;
                loaded.Claim(_clock.UtcNow, _options.Lease);
                await repository.UpdateAsync(loaded);
            });
            _artifacts.SimulateOrphanedAttempt(job.Id, attempt: 1);

            // Still inside the lease: nobody may touch it.
            _clock.Advance(TimeSpan.FromMinutes(9));
            Assert.Equal(new ExportDrainResult(0, 0, 0, 0), await DrainAsync());

            _clock.Advance(TimeSpan.FromMinutes(2));
            var result = await DrainAsync();

            Assert.Equal(1, result.Completed);
            var done = await ReadAsync(job.Id);
            Assert.Equal(ExportJobStatus.Ready, done.Status);
            Assert.Equal(2, done.Attempts);
            Assert.Equal(new[] { 1, 2 }, _artifacts.AttemptsOpenedFor(job.Id).OrderBy(a => a));
        }

        [Fact]
        public async Task A_job_whose_renderer_keeps_dying_FAILS_after_the_attempt_cap_with_an_audit_entry()
        {
            var job = await QueueAsync();
            _source.FailOpenWith = () => new OperationCanceledException("the host went away");

            for (var attempt = 1; attempt <= ExportJobAggregate.MaximumAttempts; attempt++)
            {
                using var stopping = new CancellationTokenSource();
                _source.BeforeOpen = stopping.Cancel;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(stopping.Token));
                Assert.Equal(attempt, (await ReadAsync(job.Id)).Attempts);
                _clock.Advance(TimeSpan.FromMinutes(11));
            }

            _source.FailOpenWith = null;
            _source.BeforeOpen = null;
            var result = await DrainAsync();

            Assert.Equal(1, result.Failed);
            var failed = await ReadAsync(job.Id);
            Assert.Equal(ExportJobStatus.Failed, failed.Status);
            Assert.Equal(ExportFailureCode.AttemptsExhausted, failed.FailureCode);
            Assert.Equal(0, _source.OpensFor(job.Id));

            var audit = await AuditAsync(job.Id);
            Assert.Contains(audit, e => e.Action == ExportAuditAction.Failed && e.FailureCode == ExportFailureCode.AttemptsExhausted);
        }

        // ---- cancellation, authorisation, the cap ------------------------------------------------------------

        [Fact]
        public async Task A_cancel_written_by_SOMEONE_ELSE_stops_the_render_and_removes_the_temp_file()
        {
            var job = await QueueAsync();
            _source.OnRow = async row =>
            {
                if (row == 25)
                {
                    await InScopeAsync(async repository =>
                    {
                        var loaded = (await repository.GetByIdAsync(job.Id))!;
                        loaded.Cancel(_clock.UtcNow);
                        await repository.UpdateAsync(loaded);
                    });
                }
            };

            var result = await DrainAsync();

            Assert.Equal(1, result.Abandoned);
            Assert.Equal(0, result.Completed);
            Assert.Equal(ExportJobStatus.Cancelled, (await ReadAsync(job.Id)).Status);
            Assert.True(_source.RowsPulledFor(job.Id) < TestSource.DefaultRows, "the render ran to the end despite the cancel");
            Assert.Equal(0, _artifacts.CommitsFor(job.Id));
            Assert.True(_artifacts.WasDiscarded(job.Id), "the cancelled attempt's bytes were left behind");
        }

        [Fact]
        public async Task A_requester_who_is_no_longer_authorised_gets_a_FAILED_job_and_no_source_is_opened()
        {
            var job = await QueueAsync();
            _authorizer.Allow = false;

            var result = await DrainAsync();

            Assert.Equal(1, result.Failed);
            var failed = await ReadAsync(job.Id);
            Assert.Equal(ExportFailureCode.NotAuthorized, failed.FailureCode);
            Assert.Equal(0, _source.OpensFor(job.Id));
            Assert.Equal(0, _artifacts.AttemptsOpenedFor(job.Id).Count);
            // A FAILED export is not announced as ready.
            Assert.Empty(_dispatcher.Requests);
        }

        [Fact]
        public async Task An_artifact_past_the_cap_fails_DURING_the_render_not_after_it()
        {
            // The CSV renderer writes through a 64 KB buffer, so bytes reach the counting stream a buffer at a time and
            // the cap can only fire within one buffer of the limit. The table is therefore sized well past both:
            // ~20,000 rows is ~400 KB against a 100 KB cap.
            const long cap = 100 * 1024;
            _source.Rows = 20_000;
            var job = await QueueAsync();
            _options.MaxArtifactBytes = cap;

            var result = await DrainAsync();

            Assert.Equal(1, result.Failed);
            Assert.Equal(ExportFailureCode.ArtifactTooLarge, (await ReadAsync(job.Id)).FailureCode);
            // The cap counts bytes as they are written, so the source was abandoned part-way.
            Assert.True(_source.RowsPulledFor(job.Id) < _source.Rows, "the whole table was rendered before the cap fired");
            Assert.True(_artifacts.BytesFor(job.Id) <= cap, $"{_artifacts.BytesFor(job.Id)} bytes reached the destination past a {cap}-byte cap");
            Assert.True(_artifacts.WasDiscarded(job.Id));
        }

        [Fact]
        public async Task An_exception_from_the_source_becomes_a_CODE_and_its_text_is_stored_nowhere()
        {
            const string secret = "Server=prod-db;Password=hunter2";
            var job = await QueueAsync();
            _source.FailOpenWith = () => new InvalidOperationException(secret);

            var result = await DrainAsync();

            Assert.Equal(1, result.Failed);
            var failed = await ReadAsync(job.Id);
            Assert.Equal(ExportFailureCode.Unknown, failed.FailureCode);

            foreach (var property in typeof(ExportJobAggregate).GetProperties().Where(p => p.PropertyType == typeof(string)))
            {
                Assert.DoesNotContain("hunter2", (string?)property.GetValue(failed) ?? string.Empty);
            }
        }

        [Fact]
        public async Task The_oldest_request_is_rendered_first_and_another_sites_queue_is_left_alone()
        {
            var second = await QueueAsync(at: _clock.UtcNow.AddMinutes(-1));
            var first = await QueueAsync(at: _clock.UtcNow.AddMinutes(-5));
            var otherSite = await QueueAsync(siteId: 8);

            await DrainAsync();

            Assert.Equal(new[] { first.Id, second.Id }, _source.OpenOrder.ToArray());

            // Each COMPLETED export told its requester once - through the processor, not by calling the notifier by hand.
            Assert.Equal(2, _dispatcher.Requests.Count);
            Assert.All(_dispatcher.Requests, request => Assert.Equal("42", Assert.Single(request.Recipients).Value));
            Assert.Equal(ExportJobStatus.Queued, (await ReadAsync(otherSite.Id)).Status);
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private async Task<ExportDrainResult> DrainAsync(CancellationToken ct = default)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ExportQueueProcessor>().DrainSiteAsync(SiteId, ct);
        }

        private async Task InScopeAsync(Func<IExportJobRepository, Task> action)
        {
            await using var scope = _services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IExportJobRepository>());
        }

        private async Task<ExportJobAggregate> QueueAsync(int siteId = SiteId, DateTime? at = null)
        {
            var id = Guid.NewGuid();
            var job = ExportJobAggregate.Request(
                id, TenantId.Default, siteId, 42, TestSource.SourceKey, ExportFormat.Csv, TestSource.ArgumentsFor(id),
                ExportOptionsJson.Serialize(ExportRenderOptions.Default), at ?? _clock.UtcNow);
            await InScopeAsync(repository => repository.AddAsync(job));
            return job;
        }

        private async Task<ExportJobAggregate> ReadAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IExportJobRepository>().GetByIdAsync(id))!;
        }

        private async Task<IReadOnlyList<ExportAuditEntryAggregate>> AuditAsync(Guid jobId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IExportAuditRepository>().ListByJobAsync(SiteId, jobId);
        }

        private sealed class TestClock : IExportClock
        {
            private long _ticks = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc).Ticks;

            public DateTime UtcNow => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

            public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        }

        private sealed class CapturingDispatcher : Stomme.Notifications.INotificationDispatcher
        {
            public ConcurrentQueue<Stomme.Models.Notifications.NotificationDispatchRequest> Requests { get; } = new();

            public Task<Guid> DispatchAsync(Stomme.Models.Notifications.NotificationDispatchRequest request, CancellationToken ct = default)
            {
                Requests.Enqueue(request);
                return Task.FromResult(Guid.NewGuid());
            }
        }

        private sealed class TestAuthorizer : IExportJobAuthorizer
        {
            public bool Allow { get; set; } = true;

            public Task<bool> IsAuthorizedAsync(ExportJobAggregate job, IExportSource source, CancellationToken ct = default) =>
                Task.FromResult(Allow);
        }

        /// <summary>One table of <see cref="Rows"/> rows; records who opened it and how far each render got.</summary>
        private sealed class TestSource : IExportSource
        {
            public const string SourceKey = "test.rows";
            public const int DefaultRows = 100;

            public int Rows { get; set; } = DefaultRows;

            private readonly ConcurrentDictionary<Guid, int> _opens = new();
            private readonly ConcurrentDictionary<Guid, int> _rows = new();

            public ConcurrentQueue<Guid> OpenOrder { get; } = new();

            public TaskCompletionSource? Gate { get; set; }

            public Func<int, Task>? OnRow { get; set; }

            public Func<Exception>? FailOpenWith { get; set; }

            public Action? BeforeOpen { get; set; }

            public string Key => SourceKey;

            public string RequiredPermission => "export:test";

            public TableCardinality TableCardinality => TableCardinality.Single;

            public string? Validate(ExportSourceContext context) => null;

            public int OpensFor(Guid jobId) => _opens.TryGetValue(jobId, out var n) ? n : 0;

            public int RowsPulledFor(Guid jobId) => _rows.TryGetValue(jobId, out var n) ? n : 0;

            // A source is told the site, the requester and the request's own arguments - never the job. So the test
            // carries the job id in the arguments, which is also what proves ArgumentsJson reaches the source intact.
            public static string ArgumentsFor(Guid jobId) => "{\"job\":\"" + jobId.ToString("D") + "\"}";

            private static Guid JobOf(ExportSourceContext context)
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(context.ArgumentsJson!);
                return parsed.RootElement.GetProperty("job").GetGuid();
            }

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default)
            {
                BeforeOpen?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (FailOpenWith is { } fail)
                {
                    throw fail();
                }

                var jobId = JobOf(context);
                _opens.AddOrUpdate(jobId, 1, (_, n) => n + 1);
                OpenOrder.Enqueue(jobId);
                return Task.FromResult<ITabularDocument>(new Document(this, jobId));
            }

            private sealed class Document(TestSource owner, Guid jobId) : ITabularDocument, ITabularTable
            {
                public string Name => "rows";

                public IReadOnlyList<ExportColumn> Columns { get; } = new[]
                {
                    new ExportColumn("N", ExportValueKind.Integer),
                    new ExportColumn("Text", ExportValueKind.Text),
                };

                public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return this;
                    await Task.CompletedTask;
                }

                public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    if (owner.Gate is { } gate)
                    {
                        await gate.Task.WaitAsync(ct);
                    }

                    for (var i = 1; i <= owner.Rows; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        owner._rows.AddOrUpdate(jobId, 1, (_, n) => n + 1);
                        yield return new object?[] { i, "row number " + i };

                        if (owner.OnRow is { } onRow)
                        {
                            await onRow(i);
                        }
                    }
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }

        /// <summary>Keeps each attempt's bytes in memory, keyed by (job, attempt) - so a shared destination would show.</summary>
        private sealed class TestArtifactStore : IExportArtifactStore
        {
            private readonly ConcurrentDictionary<(Guid Job, int Attempt), MemoryStream> _attempts = new();
            private readonly ConcurrentDictionary<Guid, int> _commits = new();
            private readonly ConcurrentDictionary<Guid, bool> _discarded = new();
            private int _nextFileId = 100;

            public int CommitsFor(Guid jobId) => _commits.TryGetValue(jobId, out var n) ? n : 0;

            public bool WasDiscarded(Guid jobId) => _discarded.ContainsKey(jobId);

            public IReadOnlyList<int> AttemptsOpenedFor(Guid jobId) => _attempts.Keys.Where(k => k.Job == jobId).Select(k => k.Attempt).ToList();

            public long BytesFor(Guid jobId) => _attempts.Where(k => k.Key.Job == jobId).Sum(k => k.Value.Length);

            public void SimulateOrphanedAttempt(Guid jobId, int attempt) => _attempts[(jobId, attempt)] = new MemoryStream();

            public Task<Stream> OpenAttemptAsync(ExportJobAggregate job, CancellationToken ct = default)
            {
                var stream = new KeepOpenMemoryStream();
                if (!_attempts.TryAdd((job.Id, job.Attempts), stream))
                {
                    throw new InvalidOperationException($"Attempt {job.Attempts} of job {job.Id} was opened twice - two attempts share a destination.");
                }

                return Task.FromResult<Stream>(stream);
            }

            public Task<int> CommitAttemptAsync(ExportJobAggregate job, long sizeBytes, CancellationToken ct = default)
            {
                Assert.Equal(_attempts[(job.Id, job.Attempts)].Length, sizeBytes);
                _commits.AddOrUpdate(job.Id, 1, (_, n) => n + 1);
                return Task.FromResult(Interlocked.Increment(ref _nextFileId));
            }

            public Task DiscardAttemptAsync(ExportJobAggregate job, CancellationToken ct = default)
            {
                _discarded[job.Id] = true;
                return Task.CompletedTask;
            }

            private sealed class KeepOpenMemoryStream : MemoryStream
            {
                protected override void Dispose(bool disposing)
                {
                    // Left readable after the processor disposes it, so the test can measure what was written.
                }
            }
        }
    }
    /// <summary>EF Core on a throwaway SQLite file - the version token is the check.</summary>
    public sealed class SqliteExportQueueLeaseTests(ITestOutputHelper output) : ExportQueueLeaseTests(output)
    {
        protected override bool HasConcurrencyCheck => true;

        private readonly string _connectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"stomme-dataexport-lease-{Guid.NewGuid():N}.db")};Pooling=True";

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
    public sealed class PostgresExportQueueLeaseTests(ITestOutputHelper output) : ExportQueueLeaseTests(output)
    {
        protected override bool HasConcurrencyCheck => true;

        private readonly string _database = "stomme_dataexport_lease_" + Guid.NewGuid().ToString("N");

        protected override Task InitializeStoreAsync()
        {
            Assert.True(PostgresTestEndpoint.IsReachable(),
                $"PostgreSQL is not reachable at {PostgresTestEndpoint.Host}:{PostgresTestEndpoint.Port} (POSTGRES_TEST_HOST/PORT). " +
                "The export lease suite FAILS rather than skips (D46).");
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

    /// <summary>The generic RavenRepository on a LIVE RavenDB - the change vector is the check. Fails, never skips.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenExportQueueLeaseTests(ITestOutputHelper output) : ExportQueueLeaseTests(output)
    {
        protected override bool HasConcurrencyCheck => true;

        private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

        private readonly string _database = "stomme-dataexport-lease-" + Guid.NewGuid().ToString("N");
        private DocumentStore _store = default!;

        protected override async Task InitializeStoreAsync()
        {
            _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
            await RavenTestDatabaseJanitor.SweepAsync(_store, "stomme-dataexport-lease-");
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

    /// <summary>The generic FileRepository - NO concurrency check, asserted single-host.</summary>
    public sealed class FileExportQueueLeaseTests(ITestOutputHelper output) : ExportQueueLeaseTests(output)
    {
        protected override bool HasConcurrencyCheck => false;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"stomme-dataexport-lease-file-{Guid.NewGuid():N}");

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
