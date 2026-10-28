using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide.Operations;
using Sparrow;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// RavenDB-only, and deliberately not part of the cross-provider suite: this pins what
/// <see cref="RavenFrameworkStoreOptions.WaitForNonStaleResults"/> buys the FRAMEWORK STORE, which the
/// relational provider has no equivalent of.
/// <para>
/// <b>Why the obvious test is worthless here.</b> "Write a role, read it back, assert it is there" passes on an
/// idle container whether or not the wait is enabled — src/Stomme.Persistence.RavenDb/README.md measured 25/25 read-after-write queries
/// observing their write immediately. Staleness is invisible in development and appears under load. So a
/// passing read-after-write test proves nothing about staleness, and the condition has to be FORCED.
/// </para>
/// <para>
/// <b>Forcing it has a cost worth knowing about.</b> Issuing the same query repeatedly against an index that
/// is deliberately held still is the one shape in which RavenDB's client HTTP cache can revalidate to a 304
/// and replay a stale response <i>without the server ever honouring the wait</i>. This class therefore turns
/// the HTTP cache off on its own store — see <see cref="InitializeAsync"/>, which carries the measurement.
/// </para>
/// </summary>
public sealed class RavenFrameworkStoreStalenessTests : IAsyncLifetime
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

    private readonly string _database = "stomme-fx-staleness-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;

    public async Task InitializeAsync()
    {
        var store = new DocumentStore { Urls = [Url], Database = _database };

        // THE CLIENT HTTP CACHE MUST BE OFF FOR THIS TEST TO MEASURE WHAT IT CLAIMS TO MEASURE, and that
        // is a statement about THIS TEST's shape, not a workaround for a defect. See the block above
        // WaitForIndexingToResumeAsync for the measurement that proves it.
        //
        // RavenDB's client caches query responses by etag and revalidates with If-None-Match. The server
        // checks that etag in DynamicQueryRunner.ExecuteQuery BEFORE it calls index.Query(), which is where
        // the wait-for-non-stale loop lives:
        //
        //     var etag = index.GetIndexEtag(queryContext, query.Metadata, queryTime);
        //     if (etag == existingResultEtag)
        //         return DocumentQueryResult.NotModifiedResult;   // returns before any waiting happens
        //     ...
        //     return await index.Query(query, queryContext, token, queryTime);   // the wait lives in here
        //
        // The etag covers (lastDocEtag, lastMappedEtag, isStale, ...), so a REPEATED IDENTICAL QUERY whose
        // index has not moved revalidates to 304, and the client replays its cached IsStale=true response
        // WITHOUT THE SERVER EVER WAITING. The client then throws TimeoutException purely because the
        // replayed result is flagged stale. That is the whole of the "Waited 45 ms against a 15 000 ms
        // budget" mystery: 45 ms is a 304 round-trip, not a wait expiring.
        //
        // This test is exactly that pathological shape — the same query four times, and no intervening
        // write between the stale read and the recovered read to move the etag. Real callers are not:
        // DatabaseManager writes a role and then reads, and the write advances lastDocEtag, so the etag
        // differs, no 304 is possible, and the wait genuinely runs. So the cache is an artefact of how this
        // test forces staleness, and disabling it on this test's own store removes the interference instead
        // of hiding it.
        store.Conventions.MaxHttpCacheSize = new Size(0, SizeUnit.Bytes);

        _store = (DocumentStore)store.InitializeStommeStore();
        _store.StampAudit(new SystemAuditContext("staleness-test"));
        await RavenDatabaseBootstrap.EnsureDatabaseAsync(_store);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_database, hardDelete: true));
        }
        catch
        {
            // best-effort cleanup
        }

        _store.Dispose();
    }

    /// <summary>
    /// <b>The host lockout, reproduced through the framework store — and then prevented.</b>
    /// <para>
    /// src/Stomme.Persistence.RavenDb/README.md ranks this first: <c>DatabaseManager</c> resolves the Host role by QUERYING roles it
    /// wrote moments earlier, and swallows a miss (<c>?? 0</c>). Stale index → no Host role → the host user is
    /// locked out of a brand-new install, with no exception and no log line.
    /// </para>
    /// <para>
    /// With indexing stopped, the same read is issued twice. Without the wait it returns an EMPTY role list and
    /// reports nothing — the silent failure. With the wait — which is what <c>AddRavenFrameworkStore</c>
    /// defaults to — it cannot return that wrong answer: it blocks and then fails loudly. Converting a silent
    /// wrong answer into a loud one is the entire value of the default, and this is the test that shows it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stale_index_silently_loses_the_host_role_unless_the_store_waits()
    {
        var unwaiting = NewRoles(waitForNonStaleResults: false);
        var waiting = NewRoles(waitForNonStaleResults: true);

        // Create the auto-index before indexing stops, so this measures staleness and not index creation.
        await using (var warmUp = Sessions(waitForNonStaleResults: true).OpenQuerySession())
        {
            warmUp.Query<RoleAggregate>().Where(item => item.SiteId == null).ToList();
        }

        await _store.Maintenance.SendAsync(new StopIndexingOperation());
        try
        {
            await unwaiting.AddAsync(new RoleAggregate
            {
                SiteId = null,
                Name = "Host",
                Description = "global",
                CreatedBy = "installer",
                ModifiedBy = "installer",
                CreatedOn = DateTime.UtcNow,
                ModifiedOn = DateTime.UtcNow
            });

            // WITHOUT the wait: the Host role is gone from the query's point of view. No exception. This is
            // the value DatabaseManager would run through `?? 0`.
            await using (var stale = Sessions(waitForNonStaleResults: false).OpenQuerySession())
            {
                var roles = stale.Query<RoleAggregate>().Where(item => item.SiteId == null).ToList();
                Assert.Empty(roles);
            }

            // WITH the wait: the same read cannot come back empty. It blocks on the index and then fails
            // loudly — "Waited 15 000 ms for the query to return non stale result". An install that cannot
            // read back what it just wrote now STOPS instead of quietly building a site with no Host role.
            //
            // This step really does block for RavenDB's default 15 s budget (measured), and that is most of
            // this test's runtime. The budget is not adjustable from here: RavenFrameworkStoreOptions
            // exposes WaitForNonStaleResults as a bool, and RavenDocumentSession spends it on a bare
            // WaitForNonStaleResults() with no timeout override. Proving the wait blocks costs the wait.
            await using (var awaited = Sessions(waitForNonStaleResults: true).OpenQuerySession())
            {
                Assert.ThrowsAny<TimeoutException>(() =>
                    awaited.Query<RoleAggregate>().Where(item => item.SiteId == null).ToList());
            }
        }
        finally
        {
            await _store.Maintenance.SendAsync(new StartIndexingOperation());
            await WaitForIndexingToResumeAsync();
        }

        // Indexing resumed: the waiting store observes the write, so the wait is a barrier, not a ban.
        //
        // This read is the one that used to flake, and it is load-bearing that it does NOT pre-wait for the
        // index to catch up before asserting. The index is typically still stale when this runs; what makes
        // the assertion pass is the STORE'S OWN WAIT absorbing the catch-up — which is precisely the claim
        // under test. Pre-waiting here would make the test pass without exercising the wait at all.
        await using var recovered = Sessions(waitForNonStaleResults: true).OpenQuerySession();
        Assert.Single(recovered.Query<RoleAggregate>().Where(item => item.SiteId == null).ToList());
        Assert.NotEmpty(await waiting.GetAllAsync());
    }

    // THIS CLOSES A REAL RACE AND IS NOT WHAT FIXED THE FLAKE. Both halves of that are measured; do not
    // read the first as the second.
    //
    // The race it closes is real: StartIndexingOperation returns before indexing has resumed, so the
    // recovered read above could hit a still-paused index. Polling the server's own status is the honest
    // form of the wait — a Task.Delay would be a guess, tuned until it stopped failing on one machine.
    //
    // What it never did was make this test reliable, and the earlier measurements said so plainly:
    //
    //   no wait at all .................................... 2 failures / 8   (25%)
    //   + wait on the GLOBAL indexing status .............. 1 failure  / 16   (6%)
    //   + wait on global AND per-index status ............. 2 failures / 12  (17%)
    //
    // i.e. NO DEMONSTRATED IMPROVEMENT — the 6% reading was a sample-size artefact. That was correct, and
    // the reason is now known: WAITING FOR "RUNNING" IS NOT WAITING FOR "CAUGHT UP". An index reports
    // Running the moment indexing resumes, while it is still stale. The failure then came from the client's
    // HTTP cache replaying a stale response through a 304 (see InitializeAsync), which no amount of
    // status-polling can prevent. The fix is there, not here.
    //
    // That diagnosis is not inferred, it is measured. Hold an index PERMANENTLY stale (indexing stopped)
    // and issue the SAME wait-for-non-stale query twice. Staleness is identical for both, so the only
    // variable is the cache:
    //
    //   cache OFF : attempt 1 = 15 082 ms   attempt 2 = 15 010 ms   <- the server waits, both times
    //   cache ON  : attempt 1 = 15 073 ms   attempt 2 =      2 ms   <- 304 replay, never waits
    //
    // The 2 ms is the "Waited 45 ms" of the bug report, reproduced on demand and without any load. Note
    // attempt 1 waits the full budget either way: the first query has nothing cached under that etag. That
    // is why the Assert.ThrowsAny step above never flaked while the recovered read did.
    //
    // Repeat-stability, measured against the infra UAT server (192.168.10.20:8081, BL-18 close-out,
    // 2026-08-03). With the MaxHttpCacheSize line above temporarily reverted, this test failed
    // 13/20 filtered runs and 9/12 full-suite runs (two suite instances looping concurrently as load) —
    // every failure the same "Waited 10-40 ms" 304 signature, and NO OTHER test in the 154 ever failed.
    // Against the LAN server the pre-fix shape is ~65-75% failure, far above the ~25% recorded against
    // local Raven: the fast client reaches the server before the resumed index maps the doc almost every
    // time, so the etag has not moved and the 304 replay is near-certain. With the line in place the same
    // conditions produce 0 failures (counts in the BL-18 close-out report/commit).
    //
    // This is kept because it is cheap and it makes the setup's completion explicit, so that a failure of
    // the recovered read is a failure of the thing under test rather than of this method's setup.
    private async Task WaitForIndexingToResumeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var status = await _store.Maintenance.SendAsync(new GetIndexingStatusOperation());

            // BOTH halves: the global Status can flip to Running before the individual indexes do. Neither
            // half implies the index has caught up — only that it is no longer paused. The recovered read's
            // own wait is what absorbs the catch-up.
            var indexesRunning = status.Indexes is null
                || status.Indexes.All(index => index.Status == IndexRunningStatus.Running);

            if (status.Status == IndexRunningStatus.Running && indexesRunning)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            "RavenDB indexing did not report Running within 30 s of StartIndexingOperation, so this test " +
            "cannot distinguish the staleness it pins from its own setup racing. This is a test-harness " +
            "failure, not a framework-store failure.");
    }

    private IServiceProvider Provider(bool waitForNonStaleResults)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new SystemScopeTenantContext());
        services.AddRavenFrameworkStore(_store, options => options.WaitForNonStaleResults = waitForNonStaleResults);
        return services.BuildServiceProvider();
    }

    private ISecurityRoleStore NewRoles(bool waitForNonStaleResults) =>
        Provider(waitForNonStaleResults).GetRequiredService<ISecurityRoleStore>();

    private IFrameworkStoreSessionFactory Sessions(bool waitForNonStaleResults) =>
        Provider(waitForNonStaleResults).GetRequiredService<IFrameworkStoreSessionFactory>();

    private sealed class SystemScopeTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => null;

        public bool IsSystemScope => true;
    }
}
