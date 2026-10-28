using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.SharedKernel.Domain;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The SAME conformance suite against the RavenDB provider, live against a real server
/// (RAVENDB_TEST_URL, default http://127.0.0.1:7000). Each test class instance gets its own database.
/// </summary>
public sealed class RavenConformanceTests : PersistenceConformanceTests
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

    private readonly string _database = "stomme-conformance-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;
    private IRavenIdGenerator _idGenerator = default!;
    private IDocumentSessionFactory _sessions = default!;

    protected override IDocumentSessionFactory Sessions => _sessions;

    public override async Task InitializeAsync()
    {
        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        // The document provider has no change tracker to ask "is this an insert?", so audit stamps are
        // applied by a store-level listener.
        _store.StampAudit(Audit);
        await RavenTestDatabases.CreateAsync(_store, _database);

        _idGenerator = new RavenHiLoIdGenerator(_store);
        // waitForNonStaleResults: RavenDB indexes update asynchronously, so a query issued right after a
        // write may silently miss it. These tests are read-after-write throughout — exactly like the real
        // install/seed path, which is why the install path will need this same setting. See src/Stomme.Persistence.RavenDb/README.md.
        _sessions = new RavenDocumentSessionFactory(_store, _idGenerator, waitForNonStaleResults: true);
    }

    public override async Task DisposeAsync()
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

    protected override IRepository<Widget, int> NewRepository(
        IDomainEventSerializer? serializer = null,
        IAggregateTracker? tracker = null) =>
        // A fresh change-vector tracker per repository = a fresh scope, so two repositories can conflict.
        new RavenRepository<Widget, int>(
            _store,
            new SystemScopeTenantContext(),
            tracker ?? new AggregateTracker(),
            serializer is null ? [] : [serializer],
            new RavenChangeVectorTracker(),
            idGenerator: _idGenerator);
}
