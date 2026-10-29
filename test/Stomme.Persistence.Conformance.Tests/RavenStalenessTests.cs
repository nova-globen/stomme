using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// RavenDB-specific, and deliberately NOT part of the conformance suite: this pins a behaviour the
/// relational provider does not have. RavenDB indexes update asynchronously, so a query issued after a
/// write may not observe it — and the miss is SILENT (an empty result, not an error).
/// <para>
/// This matters because it does not reproduce by accident. On an idle local container, 25/25 read-after-write
/// queries observed their write immediately: the hazard is invisible in development and appears under load,
/// on a busy server, or while an index rebuilds. So the condition is forced here rather than waited for.
/// </para>
/// </summary>
public sealed class RavenStalenessTests : IAsyncLifetime
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

    private readonly string _database = "stomme-staleness-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;
    private RavenHiLoIdGenerator _idGenerator = default!;

    public async Task InitializeAsync()
    {
        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        await RavenTestDatabases.CreateAsync(_store, _database);
        _idGenerator = new RavenHiLoIdGenerator(_store);
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
    /// The failure mode, made deterministic by pausing indexing: a written document is invisible to a
    /// QUERY while remaining visible to a LOAD by id. Every framework-store read on the seam is a query
    /// so this is the shape the install path would hit — silently half-configuring a site.
    /// </summary>
    [Fact]
    public async Task A_stale_index_makes_a_query_silently_miss_a_written_document()
    {
        var sessions = new RavenDocumentSessionFactory(_store, _idGenerator, waitForNonStaleResults: false);
        await WarmUpAutoIndexAsync(sessions);

        await _store.Maintenance.SendAsync(new StopIndexingOperation());
        try
        {
            var widget = await InsertAsync("invisible-" + Guid.NewGuid().ToString("N"));

            await using var session = sessions.OpenQuerySession();

            // The document exists: a load by id reads the document store directly and finds it.
            Assert.NotNull(await session.LoadAsync<Widget>(widget.Id));

            // The query does not see it. No exception, no warning — just an empty result.
            Assert.Empty(session.Query<Widget>().Where(w => w.Name == widget.Name).ToList());
        }
        finally
        {
            await _store.Maintenance.SendAsync(new StartIndexingOperation());
        }
    }

    /// <summary>
    /// The mitigation: a session opened with the wait blocks until the index catches up, so the same
    /// read-after-write observes the write.
    /// </summary>
    [Fact]
    public async Task Waiting_for_non_stale_results_observes_the_write()
    {
        var sessions = new RavenDocumentSessionFactory(_store, _idGenerator, waitForNonStaleResults: true);
        var widget = await InsertAsync("visible-" + Guid.NewGuid().ToString("N"));

        await using var session = sessions.OpenQuerySession();
        Assert.Single(session.Query<Widget>().Where(w => w.Name == widget.Name).ToList());
    }

    private async Task WarmUpAutoIndexAsync(IDocumentSessionFactory sessions)
    {
        // Auto-indexes are created on first query; create it before indexing stops so the test measures
        // staleness rather than index creation.
        await using var session = sessions.OpenQuerySession();
        session.Query<Widget>().Where(w => w.Name == "warmup").ToList();
    }

    private async Task<Widget> InsertAsync(string name)
    {
        var widget = new Widget(name);
        await new RavenRepository<Widget, int>(
            _store,
            new SystemScopeTenantContext(),
            new AggregateTracker(),
            [],
            new RavenChangeVectorTracker(),
            idGenerator: _idGenerator).AddAsync(widget);
        return widget;
    }

    private sealed class SystemScopeTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => null;

        public bool IsSystemScope => true;
    }
}
