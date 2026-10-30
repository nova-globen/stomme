using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;

namespace Stomme.Persistence.Raven.Tests;

// Live smoke test proving the RavenDB provider satisfies the persistence seam end-to-end against a
// real server (aggregate round-trip, query session, optimistic concurrency, transactional outbox).
// GATED in Stomme.slnx, fail-not-skip: an unreachable server fails
// InitializeAsync loudly. Defaults to this machine's RavenDB, http://127.0.0.1:7000 (env-overridable via RAVENDB_TEST_URL — the
// PostgresTestEndpoint convention); the older default :8091 never existed here.
public sealed class RavenProviderLiveTests : IAsyncLifetime
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") is { Length: > 0 } url ? url : "http://127.0.0.1:7000";

    private readonly string _database = "stomme-live-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = (DocumentStore)new DocumentStore { Urls = [Url], Database = _database }.InitializeStommeStore();
        await _store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(_database)));
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

    private RavenRepository<Widget, Guid> NewRepository(
        ITenantContext? tenantContext = null,
        IDomainEventSerializer? serializer = null,
        RavenChangeVectorTracker? changeVectors = null,
        IAggregateTracker? tracker = null) =>
        new(
            _store,
            tenantContext ?? new SystemScopeTenantContext(),
            tracker ?? new AggregateTracker(),
            serializer is null ? [] : [serializer],
            changeVectors ?? new RavenChangeVectorTracker());

    [Fact]
    public async Task Aggregate_round_trips_through_the_seam()
    {
        var tracker = new AggregateTracker();
        var repo = NewRepository(tracker: tracker);
        var id = Guid.NewGuid();

        await repo.AddAsync(new Widget(id, "first"));
        // In-process (no serializer) path enrolls the aggregate for domain-event dispatch.
        Assert.NotEmpty(tracker.DequeueTracked());

        var loaded = (await repo.GetByIdAsync(id))!;
        Assert.NotNull(loaded);
        Assert.Equal("first", loaded!.Name);

        loaded.Rename("second");
        await repo.UpdateAsync(loaded);

        var reloaded = await repo.GetByIdAsync(id)!;
        Assert.Equal("second", reloaded!.Name);
        // NOTE: the Raven provider enforces optimistic concurrency through RavenDB change vectors
        // (see the stale-update test), not the AggregateRoot.Version field — so Version is not bumped
        // here, unlike the EF provider where Version is the concurrency token.

        var all = await repo.GetAllAsync();
        Assert.Contains(all, w => w.Id == id);

        await repo.DeleteAsync(reloaded);
        Assert.Null(await repo.GetByIdAsync(id));
    }

    // NOTE — WHAT THIS DOES AND DOES NOT PROVE. This reads through RavenDB's NATIVE session
    // (_store.OpenAsyncSession()), NOT through the seam's IQuerySession. It proves the provider's writes
    // land as readable documents; it proves NOTHING about the seam's read contract.
    //
    // This test was previously described as "reads via IQuerySession", and that claim was taken as
    // evidence the seam was verified on RavenDB. It was not: at the time the RavenDB project had no
    // IQuerySession implementation at all. The seam's read path is now implemented
    // (RavenDocumentSession/RavenDocumentSessionFactory) and exercised through its OWN API by
    // test/Stomme.Persistence.Conformance.Tests, which runs one contract against both providers.
    // Renamed to say what it actually is, so the overclaim cannot be made from it again.
    [Fact]
    public async Task Stored_documents_are_readable_through_RavenDBs_native_session()
    {
        var repo = NewRepository();
        var id = Guid.NewGuid();
        await repo.AddAsync(new Widget(id, "queryable"));

        using var session = _store.OpenAsyncSession();
        var byQuery = await session.Query<Widget>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(w => w.Name == "queryable")
            .ToListAsync();

        Assert.Contains(byQuery, w => w.Id == id);
    }

    [Fact]
    public async Task Stale_update_raises_a_concurrency_conflict()
    {
        var id = Guid.NewGuid();
        await NewRepository().AddAsync(new Widget(id, "v1"));

        // Two independent scopes each load the aggregate, then both try to save their version.
        var changeVectorsA = new RavenChangeVectorTracker();
        var changeVectorsB = new RavenChangeVectorTracker();
        var repoA = NewRepository(changeVectors: changeVectorsA);
        var repoB = NewRepository(changeVectors: changeVectorsB);

        var a = await repoA.GetByIdAsync(id);
        var b = await repoB.GetByIdAsync(id);

        a!.Rename("from-A");
        await repoA.UpdateAsync(a);

        b!.Rename("from-B");
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repoB.UpdateAsync(b));
    }

    [Fact]
    public async Task Outbox_captures_domain_events_in_the_same_transaction()
    {
        var repo = NewRepository(serializer: new StubSerializer());
        var id = Guid.NewGuid();

        var widget = new Widget(id, "with-events");
        widget.RaiseCreated();
        await repo.AddAsync(widget);

        using var session = _store.OpenAsyncSession();
        var outbox = await session.Query<OutboxMessage>()
            .Customize(c => c.WaitForNonStaleResults())
            .ToListAsync();

        Assert.Contains(outbox, m => m.EventType == nameof(WidgetCreated));
    }

    // --- test doubles & sample aggregate ---

    private sealed class SystemScopeTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => null;
        public bool IsSystemScope => true;
    }

    private sealed class StubSerializer : IDomainEventSerializer
    {
        public (string EventType, string Payload) Serialize(IDomainEvent domainEvent) =>
            (domainEvent.GetType().Name, "{}");

        public IDomainEvent? Deserialize(string eventType, string payload) => null;
    }

    private sealed record WidgetCreated(Guid Id) : DomainEvent;

    public sealed class Widget : AggregateRoot<Guid>
    {
        private Widget()
        {
        }

        public Widget(Guid id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Name { get; private set; } = string.Empty;

        public void Rename(string name) => Name = name;

        public void RaiseCreated() => RaiseDomainEvent(new WidgetCreated(Id));
    }
}
