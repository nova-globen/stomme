using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Database.Sqlite;
using Stomme.Databases.Interfaces;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests
{
    // The driven half of the seam contract: the Job/JobLog REPOSITORIES exercised end-to-end against a real database whose
    // schema is written by hand to match the MASTER migration set — not EnsureCreated, which would build the
    // schema from the very model under test (and would also silently GIVE JobLog audit columns if JobLogAggregate
    // ever became IAuditable, defeating the mutation guard below).
    //
    // The conformance suite (Stomme.Persistence.Conformance.Tests) proves the STORE and QUERY shapes on both
    // providers. This proves the things it structurally cannot: the repository ORCHESTRATION (mapper round-trip,
    // cache wrapping, GetJobs pruning), the severed JobLog.Job re-population, and — the load-bearing one —
    // IAuditable audit STAMPING through DbContextUtils, which the conformance fixtures deliberately do not run
    // (they use a plain context and set the stamps as test data). The context here overrides SaveChangesAsync to
    // call DbContextUtils exactly as MasterDBContext does.
    public sealed class JobsOnTheSeamTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // Job/JobLog exactly as the MASTER migration set leaves them, cross-checked column-for-column against the
        // live stomme-pg-44357 database (host 45432): job has the audit quartet and a NULLABLE maximum_duration;
        // job_log has NEITHER audit nor deletable columns, and Server/Instance were added by 10020001.
        private const string JobSchemaDdl = """
            CREATE TABLE "Job" (
                "JobId" INTEGER NOT NULL CONSTRAINT "PK_Job" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "JobType" TEXT NOT NULL,
                "Frequency" TEXT NOT NULL,
                "Interval" INTEGER NOT NULL,
                "StartDate" TEXT NULL,
                "EndDate" TEXT NULL,
                "IsEnabled" INTEGER NOT NULL,
                "IsStarted" INTEGER NOT NULL,
                "IsExecuting" INTEGER NOT NULL,
                "NextExecution" TEXT NULL,
                "RetentionHistory" INTEGER NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "CreatedOn" TEXT NOT NULL,
                "ModifiedBy" TEXT NOT NULL,
                "ModifiedOn" TEXT NOT NULL,
                "MaximumDuration" INTEGER NULL
            );
            CREATE TABLE "JobLog" (
                "JobLogId" INTEGER NOT NULL CONSTRAINT "PK_JobLog" PRIMARY KEY AUTOINCREMENT,
                "JobId" INTEGER NOT NULL,
                "StartDate" TEXT NOT NULL,
                "FinishDate" TEXT NULL,
                "Succeeded" INTEGER NULL,
                "Notes" TEXT NULL,
                "Server" TEXT NULL,
                "Instance" TEXT NULL
            );
            """;

        // Reproduces MasterDBContext: maps ConfigureMasterFrameworkStore AND runs the DbContextUtils audit hook.
        private sealed class StampingMasterContext(DbContextOptions options, IRelationalDatabase database, IHttpContextAccessor accessor) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                DbContextUtils.SaveChanges(this, accessor);
                return base.SaveChangesAsync(cancellationToken);
            }
        }

        private sealed class StubTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        // No HttpContext: the background scheduler runs with no request, so DbContextUtils stamps an empty
        // username and the current time — precisely the shape the live job table showed (created_on set,
        // created_by = '').
        private sealed class NoRequestAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        // A pass-through cache: every GetCache is a miss that runs the factory, so the read path under test is
        // actually exercised rather than served from memory.
        private sealed class PassThroughCache : ICacheManager
        {
            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory) => factory(CancellationToken.None);
            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => factory(CancellationToken.None);
            public Task RemoveCacheAsync(string key) => Task.CompletedTask;
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory) => factory(CancellationToken.None);
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => factory(CancellationToken.None);
            public Task RemoveCacheAsync(Alias alias, string key) => Task.CompletedTask;
            public Task ClearAllAsync() => Task.CompletedTask;
        }

        // A cache that actually CACHES (unlike PassThroughCache): a hit is served from the dictionary and a null
        // factory result is cached as a genuine negative entry — exactly the FusionCache behaviour that lets a
        // stale null survive. RemoveCacheAsync evicts. Shared across repository instances the way the singleton
        // FusionCache is shared across the scheduler's per-tick scopes. Needed to reproduce the AddJobAsync
        // stale-negative-cache bug, which PassThroughCache structurally cannot see.
        private sealed class CachingCacheManager : ICacheManager
        {
            private readonly Dictionary<string, object> _store = new();

            private async Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> factory)
            {
                if (_store.TryGetValue(key, out var cached))
                {
                    return (T)cached;
                }
                var value = await factory(CancellationToken.None);
                _store[key] = value!;
                return value;
            }

            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory) => GetOrAddAsync(key, factory);
            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => GetOrAddAsync(key, factory);
            public Task RemoveCacheAsync(string key) { _store.Remove(key); return Task.CompletedTask; }
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory) => GetOrAddAsync(key, factory);
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => GetOrAddAsync(key, factory);
            public Task RemoveCacheAsync(Alias alias, string key) { _store.Remove(key); return Task.CompletedTask; }
            public Task ClearAllAsync() { _store.Clear(); return Task.CompletedTask; }
        }

        // In-test store adapters: an EfRepository over ONE StampingMasterContext, wrapped as the closed store the
        // repository consumes. Mirrors EfJobStore/EfJobLogStore, which do the same over MasterDBContext.
        private sealed class TestJobStore(DbContext context, ITenantContext tenantContext) : IJobStore
        {
            private readonly IRepository<JobAggregate, int> _r = new EfRepository<JobAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<JobAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<JobAggregate>> GetAllAsync(CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(JobAggregate a, CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(JobAggregate a, CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(JobAggregate a, CancellationToken ct = default) => _r.DeleteAsync(a, ct);
        }

        private sealed class TestJobLogStore(DbContext context, ITenantContext tenantContext) : IJobLogStore
        {
            private readonly IRepository<JobLogAggregate, int> _r = new EfRepository<JobLogAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<JobLogAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<JobLogAggregate>> GetAllAsync(CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(JobLogAggregate a, CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(JobLogAggregate a, CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(JobLogAggregate a, CancellationToken ct = default) => _r.DeleteAsync(a, ct);
        }

        private sealed class TestMasterSessions(Func<DbContext> context) : IMasterStoreSessionFactory
        {
            private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = context
            });

            public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);
            public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
        }

        private readonly IHttpContextAccessor _accessor = new NoRequestAccessor();
        private readonly ITenantContext _tenantContext = new StubTenantContext();
        private readonly ServiceProvider _jobStoreServices;
        private readonly ServiceProvider _jobLogStoreServices;

        public JobsOnTheSeamTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = JobSchemaDdl;
            command.ExecuteNonQuery();

            // The DI graph the repositories actually get: IJobStore/IJobLogStore registered TRANSIENT, so each
            // resolve is a fresh store over a fresh context — which is what JobRepository.NewStore()/
            // JobLogRepository.NewStore() depend on (the per-write-store pattern, as UrlMapping uses it). A store
            // constructor-injected as a singleton would fail every Add-then-Update in one scope; that this is
            // transient is the fix under test.
            _jobStoreServices = new ServiceCollection()
                .AddTransient<IJobStore>(_ => new TestJobStore(NewContext(), _tenantContext))
                .BuildServiceProvider();
            _jobLogStoreServices = new ServiceCollection()
                .AddTransient<IJobLogStore>(_ => new TestJobLogStore(NewContext(), _tenantContext))
                .BuildServiceProvider();
        }

        public void Dispose()
        {
            _jobStoreServices.Dispose();
            _jobLogStoreServices.Dispose();
            _connection.Dispose();
        }

        private StampingMasterContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StampingMasterContext(builder.Options, _database, _accessor);
        }

        private IJobRepository NewJobRepository() =>
            new JobRepository(_jobStoreServices, new TestMasterSessions(NewContext), new PassThroughCache());

        private IJobLogRepository NewJobLogRepository() =>
            new JobLogRepository(_jobLogStoreServices, new TestMasterSessions(NewContext));

        private static Job NewJob(string jobType, string name = "A Job") => new()
        {
            Name = name,
            JobType = jobType,
            Frequency = "m",
            Interval = 5,
            IsEnabled = true,
            RetentionHistory = 10,
            MaximumDuration = 30
        };

        // THE audit-stamp guard. The job table's created_by/created_on/modified_by/modified_on are NOT NULL, and
        // nothing sets them — AddJobAsync builds the aggregate straight from the model, whose stamps are default.
        // DbContextUtils stamps them because JobAggregate is IAuditable. Drop that interface and CreatedOn stays
        // default(DateTime) and this fails — the mutation check hazard 2 demands.
        [Fact]
        public async Task Adding_a_job_assigns_an_id_and_stamps_the_audit_columns()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Alpha, Stomme.Server"));

            Assert.True(job.JobId > 0);                 // the database assigned the identity
            Assert.True(job.CreatedOn >= before);       // DbContextUtils stamped it — NOT default(DateTime)
            Assert.True(job.ModifiedOn >= before);
        }

        [Fact]
        public async Task A_job_is_found_by_its_type_and_by_its_id()
        {
            var added = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Beta, Stomme.Server", "Beta"));

            var byType = await NewJobRepository().GetJobAsync("Stomme.Jobs.Beta, Stomme.Server");
            var byId = await NewJobRepository().GetJobAsync(added.JobId);

            Assert.NotNull(byType);
            Assert.Equal("Beta", byType.Name);
            Assert.NotNull(byId);
            Assert.Equal(added.JobId, byId.JobId);
        }

        // The scheduler's read-then-write shape: read the job through a session, mutate the detached model,
        // write it back through the store. ModifiedOn must advance (re-stamped), and IsExecuting must persist —
        // the concurrency flag the scheduler flips to stop a job running twice.
        [Fact]
        public async Task Updating_a_job_persists_the_change_and_restamps_modified_on()
        {
            var repo = NewJobRepository();
            var job = await repo.AddJobAsync(NewJob("Stomme.Jobs.Gamma, Stomme.Server"));
            var stampedAt = job.ModifiedOn;

            await Task.Delay(10);
            job.IsExecuting = true;
            await NewJobRepository().UpdateJobAsync(job);

            var reloaded = await NewJobRepository().GetJobAsync(job.JobId);
            Assert.True(reloaded.IsExecuting);
            Assert.True(reloaded.ModifiedOn > stampedAt);
        }

        // GetJobs prunes any job whose type no longer resolves (an uninstalled module) — a READ (through the
        // session) followed by DELETEs (through the store) on ONE repository instance. Proves the constructor
        // -injected store is correct here: the read never went through the store, so the deletes raise no
        // identity conflict (hazard 6, verified by mutation not by call graph — this test IS the mutation).
        [Fact]
        public async Task Get_jobs_prunes_jobs_whose_type_no_longer_resolves()
        {
            await NewJobRepository().AddJobAsync(NewJob(typeof(JobsOnTheSeamTests).AssemblyQualifiedName!, "Real"));
            await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Uninstalled, Stomme.Missing", "Ghost"));

            var jobs = (await NewJobRepository().GetJobsAsync()).ToList();

            Assert.Single(jobs);
            Assert.Equal("Real", jobs[0].Name);
        }

        [Fact]
        public async Task Deleting_a_job_removes_it()
        {
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Delta, Stomme.Server"));

            await NewJobRepository().DeleteJobAsync(job.JobId);

            Assert.Null(await NewJobRepository().GetJobAsync(job.JobId));
            Assert.Empty(await NewJobRepository().GetJobsAsync());
        }

        // THE reverse audit-stamp guard. The job_log table has NO audit columns. If JobLogAggregate were
        // IAuditable, DbContextUtils would set CurrentValues["CreatedBy"] on an entity type that has no such
        // mapped property and THROW — so this Add would fail. That it succeeds proves JobLog is correctly NOT
        // IAuditable (hazard 2, the other direction).
        [Fact]
        public async Task Adding_a_job_log_assigns_an_id_and_does_not_stamp_audit()
        {
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Logged, Stomme.Server"));

            var log = await NewJobLogRepository().AddJobLogAsync(new JobLog
            {
                JobId = job.JobId,
                StartDate = DateTime.UtcNow,
                Server = "TESTHOST",
                Instance = ""
            });

            Assert.True(log.JobLogId > 0);
        }

        // The scheduler's log lifecycle: insert a running log, read its id back, UPDATE the FinishDate/Succeeded
        // when the run ends (AddJobLogAsync then UpdateJobLogAsync in HostedServiceBase).
        [Fact]
        public async Task A_job_log_is_added_running_then_finished()
        {
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Runner, Stomme.Server"));

            var log = await NewJobLogRepository().AddJobLogAsync(new JobLog
            {
                JobId = job.JobId, StartDate = DateTime.UtcNow, Succeeded = false, Server = "TESTHOST", Instance = ""
            });

            log.FinishDate = DateTime.UtcNow;
            log.Succeeded = true;
            await NewJobLogRepository().UpdateJobLogAsync(log);

            var reloaded = await NewJobLogRepository().GetJobLogAsync(log.JobLogId);
            Assert.NotNull(reloaded.FinishDate);
            Assert.True(reloaded.Succeeded);
        }

        // THE severed-navigation guard. JobLog.Job is [NotMapped]; the repository re-populates it from a
        // second master-session query. The Admin > Jobs > Log screen reads log.Job.Name and log.Job.IsExecuting,
        // so a null Job would NRE it. Drop PopulateJobsAsync and this fails.
        [Fact]
        public async Task A_job_log_repopulates_its_severed_job_navigation()
        {
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.Named, Stomme.Server", "The Named Job"));
            job.IsExecuting = true;
            await NewJobRepository().UpdateJobAsync(job);

            var log = await NewJobLogRepository().AddJobLogAsync(new JobLog
            {
                JobId = job.JobId, StartDate = DateTime.UtcNow, Server = "TESTHOST", Instance = ""
            });

            var reloaded = await NewJobLogRepository().GetJobLogAsync(log.JobLogId);
            Assert.NotNull(reloaded.Job);
            Assert.Equal("The Named Job", reloaded.Job.Name);
            Assert.True(reloaded.Job.IsExecuting);

            // The list read stitches the job on too.
            var list = (await NewJobLogRepository().GetJobLogsAsync(job.JobId)).ToList();
            Assert.Equal("The Named Job", Assert.Single(list).Job.Name);
        }

        // The -1 wildcard, composed in C# so it never becomes a -1 == -1 fold the providers disagree on.
        // Here on SQLite it only guards the composition logic; the cross-provider divergence is caught in the
        // conformance suite against a live RavenDB.
        [Fact]
        public async Task Job_logs_filter_by_job_and_the_minus_one_wildcard_returns_all()
        {
            var jobA = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.A, Stomme.Server"));
            var jobB = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.B, Stomme.Server"));

            await NewJobLogRepository().AddJobLogAsync(new JobLog { JobId = jobA.JobId, StartDate = DateTime.UtcNow, Server = "H", Instance = "" });
            await NewJobLogRepository().AddJobLogAsync(new JobLog { JobId = jobA.JobId, StartDate = DateTime.UtcNow, Server = "H", Instance = "" });
            await NewJobLogRepository().AddJobLogAsync(new JobLog { JobId = jobB.JobId, StartDate = DateTime.UtcNow, Server = "H", Instance = "" });

            Assert.Equal(2, (await NewJobLogRepository().GetJobLogsAsync(jobA.JobId)).Count());
            Assert.Equal(3, (await NewJobLogRepository().GetJobLogsAsync()).Count()); // the -1 overload: everything
        }

        // Write site 1. The scheduler auto-registers a job and then, in the SAME scope (ONE repository
        // instance, ONE store, ONE MasterDBContext), immediately flips IsExecuting and updates it
        // (HostedServiceBase lines 109 then 151). AddJobAsync leaves the added aggregate tracked in the store's
        // context — EfRepository leaves writes tracked — so UpdateJobAsync attaching a DIFFERENT
        // aggregate with the same key throws "already being tracked". This reuses ONE repository instance on
        // purpose: every other test here news a fresh repository per call, which dodges the defect. Before the
        // fix (constructor-injected store) this throws; after it (a fresh transient store per write, the
        // UrlMapping pattern) it persists.
        [Fact]
        public async Task Adding_then_updating_a_job_through_one_repository_instance_persists_the_update()
        {
            var repo = NewJobRepository();
            var job = await repo.AddJobAsync(NewJob("Stomme.Jobs.Registered, Stomme.Server"));

            job.IsExecuting = true;
            job.NextExecution = DateTime.UtcNow.AddMinutes(5);
            await repo.UpdateJobAsync(job); // same repo/store/context as the Add — threw before the fix

            var reloaded = await NewJobRepository().GetJobAsync(job.JobId);
            Assert.True(reloaded.IsExecuting);
            Assert.NotNull(reloaded.NextExecution);
        }

        // Write site 2. The scheduler opens a JobLog running (AddJobLogAsync) and, at the end of the run,
        // finalizes the SAME log (UpdateJobLogAsync) through the SAME repository instance in the SAME scope
        // (HostedServiceBase lines 162 then 186). Before the fix the finalizing Update hit the identity map and
        // threw, so EVERY run cycle left Succeeded=0 / FinishDate=NULL — the job started but never finished. This
        // asserts the finalization actually PERSISTS, reading it back from the database.
        [Fact]
        public async Task Opening_then_finalizing_a_job_log_through_one_repository_instance_persists_the_finalization()
        {
            var job = await NewJobRepository().AddJobAsync(NewJob("Stomme.Jobs.LogRunner, Stomme.Server"));

            var jobLogs = NewJobLogRepository();
            var log = await jobLogs.AddJobLogAsync(new JobLog
            {
                JobId = job.JobId, StartDate = DateTime.UtcNow, Succeeded = false, Server = "TESTHOST", Instance = ""
            });

            log.FinishDate = DateTime.UtcNow;
            log.Succeeded = true;
            await jobLogs.UpdateJobLogAsync(log); // same repo/store/context as the Add — threw before the fix

            var reloaded = await NewJobLogRepository().GetJobLogAsync(log.JobLogId);
            Assert.NotNull(reloaded.FinishDate);
            Assert.True(reloaded.Succeeded);
        }

        // The FULL run-tick shape end to end (HostedServiceBase lines 151-196), the exact sequence a live
        // fresh install runs every minute for an enabled, due job: flip the job executing, open a log, finalize
        // the log, then advance the job (NextExecution set, IsExecuting cleared). The job repository takes TWO
        // writes in one scope (Update then Update) and the log repository takes two (Add then Update) — both
        // conflicted before the fix, which is why the DB showed IsExecuting stuck at 1, NextExecution NULL, and
        // the log never finalized. Asserts the persisted end-state the scheduler intends.
        [Fact]
        public async Task A_full_run_tick_finalizes_the_log_and_advances_the_job_through_one_scope()
        {
            // Registration happened on a prior tick (a separate scope).
            var registeredType = "Stomme.Jobs.FullTick, Stomme.Server";
            await NewJobRepository().AddJobAsync(NewJob(registeredType));

            // This tick's repositories — resolved once per scope, exactly as HostedServiceBase does.
            var jobs = NewJobRepository();
            var jobLogs = NewJobLogRepository();

            var job = await jobs.GetJobAsync(registeredType);

            // line 151: mark the job executing.
            job.IsExecuting = true;
            job = await jobs.UpdateJobAsync(job);

            // line 162: open the log running.
            var log = await jobLogs.AddJobLogAsync(new JobLog
            {
                JobId = job.JobId, StartDate = DateTime.UtcNow, Succeeded = false, Server = "TESTHOST", Instance = ""
            });

            // line 186: finalize the log (Add-then-Update on the log repository).
            log.FinishDate = DateTime.UtcNow;
            log.Succeeded = true;
            await jobLogs.UpdateJobLogAsync(log);

            // line 196: advance the job (Update-then-Update on the job repository).
            job.NextExecution = DateTime.UtcNow.AddMinutes(5);
            job.IsExecuting = false;
            await jobs.UpdateJobAsync(job);

            // Verify the persisted end-state in the database.
            var reloadedJob = await NewJobRepository().GetJobAsync(job.JobId);
            Assert.False(reloadedJob.IsExecuting);
            Assert.NotNull(reloadedJob.NextExecution);

            var reloadedLog = await NewJobLogRepository().GetJobLogAsync(log.JobLogId);
            Assert.True(reloadedLog.Succeeded);
            Assert.NotNull(reloadedLog.FinishDate);
        }

        // THE stale-negative-cache guard. GetJobAsync(string) caches a NULL miss under Job:{jobType}; before the
        // fix AddJobAsync did NOT invalidate it (UpdateJobAsync/DeleteJobAsync did), so the scheduler's
        // auto-registration read a stale null every tick and re-INSERTed the job — "UNIQUE constraint failed:
        // Job.JobType" forever, jobs never running. This reproduces it with a REAL caching manager (PassThroughCache
        // cannot: it never caches) and the live UNIQUE index on JobType (migration 10020101 makes it unique; the
        // hand-written schema above omits it, so add it here). Drop the RemoveCacheAsync in AddJobAsync and the
        // second registration tick throws.
        [Fact]
        public async Task Adding_a_job_invalidates_the_negative_cache_so_the_scheduler_does_not_reinsert_it()
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"CREATE UNIQUE INDEX ""IX_Job_JobType"" ON ""Job"" (""JobType"");";
                command.ExecuteNonQuery();
            }

            // One cache shared across per-tick repository instances, as the singleton FusionCache is shared. The
            // store provider stays transient (the fix under test), so each write still gets a fresh context.
            var cache = new CachingCacheManager();
            IJobRepository Repo() => new JobRepository(_jobStoreServices, new TestMasterSessions(NewContext), cache);

            // A RESOLVABLE type so GetJobsAsync's prune (Type.GetType(JobType) == null → delete) keeps it — the
            // uniqueness of JobType, not its shape, is what this exercises.
            var jobType = typeof(JobsOnTheSeamTests).AssemblyQualifiedName!;

            // Models HostedServiceBase auto-registration: read the job, add it only if absent.
            async Task RegisterTickAsync()
            {
                var repo = Repo();
                if (await repo.GetJobAsync(jobType) == null)
                {
                    await repo.AddJobAsync(NewJob(jobType, "Scheduled"));
                }
            }

            // Tick 1: absent → GetJobAsync caches a NULL miss, then auto-registration inserts.
            await RegisterTickAsync();

            // The stale null must be gone: a fresh read now returns the job.
            var found = await Repo().GetJobAsync(jobType);
            Assert.NotNull(found);
            Assert.Equal(jobType, found.JobType);

            // Tick 2: before the fix the cached null survived, so this re-inserted and the UNIQUE index threw.
            // With the fix the job is found and no insert is attempted.
            var replay = await Record.ExceptionAsync(RegisterTickAsync);
            Assert.Null(replay);

            // Exactly one row — no duplicate was ever inserted.
            Assert.Single(await Repo().GetJobsAsync());
        }
    }
}
