using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The persistence seam's contract, expressed once and run against EVERY provider (D3/D7: the abstraction
/// is provider-agnostic, or it is not an abstraction). Each provider supplies a fixture; the tests below
/// know nothing about EF Core or RavenDB.
/// </summary>
public abstract class PersistenceConformanceTests : IAsyncLifetime
{
    protected const string AuditUser = "conformance-user";

    /// <summary>The username the provider is configured to stamp; both providers stamp the same one.</summary>
    protected static readonly IAuditContext Audit = new SystemAuditContext(AuditUser);

    /// <summary>
    /// A fresh unit-of-work scope: an independent identity map / change tracker, as a new request would
    /// get. Two repositories from two calls must be able to conflict with each other.
    /// </summary>
    protected abstract IRepository<Widget, int> NewRepository(IDomainEventSerializer? serializer = null, IAggregateTracker? tracker = null);

    /// <summary>The seam's read path (D19). Every framework-store read goes through this, never the repository.</summary>
    protected abstract IDocumentSessionFactory Sessions { get; }

    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    // --- IRepository ---

    [Fact]
    public async Task Add_assigns_a_database_generated_id()
    {
        var repository = NewRepository();
        var widget = new Widget("generated");
        Assert.Equal(0, widget.Id);

        await repository.AddAsync(widget);

        Assert.NotEqual(0, widget.Id);
    }

    /// <summary>
    /// THE discriminating test for id generation. Two inserts that both arrive with id 0 — exactly what
    /// the framework store does — must receive DISTINCT ids and BOTH must survive.
    /// <para>
    /// Before the fix the RavenDB provider addressed both as <c>Widget/0</c> and blind-upserted: the second
    /// insert silently destroyed the first, with no exception and no conflict. A test that inserts a single
    /// aggregate passes on that broken code, which is why the id must be asserted distinct AND both
    /// documents must be read back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_inserts_with_unset_ids_get_distinct_ids_and_both_survive()
    {
        var first = new Widget("first");
        var second = new Widget("second");

        await NewRepository().AddAsync(first);
        await NewRepository().AddAsync(second);

        Assert.NotEqual(0, first.Id);
        Assert.NotEqual(0, second.Id);
        Assert.NotEqual(first.Id, second.Id);

        // Both documents still exist: the second insert did not overwrite the first.
        var reloadedFirst = await NewRepository().GetByIdAsync(first.Id);
        var reloadedSecond = await NewRepository().GetByIdAsync(second.Id);
        Assert.NotNull(reloadedFirst);
        Assert.NotNull(reloadedSecond);
        Assert.Equal("first", reloadedFirst!.Name);
        Assert.Equal("second", reloadedSecond!.Name);

        var all = await NewRepository().GetAllAsync();
        Assert.Contains(all, w => w.Id == first.Id && w.Name == "first");
        Assert.Contains(all, w => w.Id == second.Id && w.Name == "second");
    }

    /// <summary>
    /// The destruction half of the id defect, isolated so it is provable on its own. This asserts only the
    /// DOCUMENT COUNT and never inspects an id, so it stays reachable — and fails — even when id generation
    /// is broken. Against the pre-fix provider both inserts address <c>Widget/0</c> and the second
    /// blind-upserts the first: two inserts, one surviving document, no exception.
    /// </summary>
    [Fact]
    public async Task Two_inserts_with_unset_ids_produce_two_documents()
    {
        var before = (await NewRepository().GetAllAsync()).Count;

        await NewRepository().AddAsync(new Widget("survivor-a"));
        await NewRepository().AddAsync(new Widget("survivor-b"));

        var after = await NewRepository().GetAllAsync();
        Assert.Equal(before + 2, after.Count);
        Assert.Contains(after, w => w.Name == "survivor-a");
        Assert.Contains(after, w => w.Name == "survivor-b");
    }

    [Fact]
    public async Task Generated_ids_are_monotonic()
    {
        var widgets = new List<Widget>();
        for (var i = 0; i < 5; i++)
        {
            var widget = new Widget($"monotonic-{i}");
            await NewRepository().AddAsync(widget);
            widgets.Add(widget);
        }

        var ids = widgets.ConvertAll(w => w.Id);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id).ToList(), ids);
    }

    [Fact]
    public async Task Get_by_id_returns_null_when_absent() =>
        Assert.Null(await NewRepository().GetByIdAsync(987654));

    [Fact]
    public async Task Aggregate_round_trips()
    {
        var widget = new Widget("round-trip");
        await NewRepository().AddAsync(widget);

        var loaded = (await NewRepository().GetByIdAsync(widget.Id))!;
        Assert.NotNull(loaded);
        Assert.Equal("round-trip", loaded!.Name);
        Assert.Equal(widget.Id, loaded.Id);
    }

    [Fact]
    public async Task Update_persists_changes()
    {
        var widget = new Widget("before");
        await NewRepository().AddAsync(widget);

        var repository = NewRepository();
        var loaded = (await repository.GetByIdAsync(widget.Id))!;
        loaded!.Rename("after");
        await repository.UpdateAsync(loaded);

        var reloaded = await NewRepository().GetByIdAsync(widget.Id)!;
        Assert.Equal("after", reloaded!.Name);
    }

    [Fact]
    public async Task Delete_removes_the_aggregate()
    {
        var widget = new Widget("doomed");
        await NewRepository().AddAsync(widget);

        var repository = NewRepository();
        var loaded = (await repository.GetByIdAsync(widget.Id))!;
        await repository.DeleteAsync(loaded!);

        Assert.Null(await NewRepository().GetByIdAsync(widget.Id));
    }

    /// <summary>A stale write must surface as the seam's exception on every provider, not the native one.</summary>
    [Fact]
    public async Task Stale_update_raises_a_concurrency_conflict()
    {
        var widget = new Widget("contested");
        await NewRepository().AddAsync(widget);

        var repositoryA = NewRepository();
        var repositoryB = NewRepository();
        var a = await repositoryA.GetByIdAsync(widget.Id);
        var b = await repositoryB.GetByIdAsync(widget.Id);

        a!.Rename("from-A");
        await repositoryA.UpdateAsync(a);

        b!.Rename("from-B");
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repositoryB.UpdateAsync(b));
    }

    // --- IQuerySession / IDocumentSession (the seam's read path, D19) ---

    [Fact]
    public async Task Query_session_loads_by_id()
    {
        var widget = new Widget("loadable");
        await NewRepository().AddAsync(widget);

        await using var session = Sessions.OpenQuerySession();
        var loaded = await session.LoadAsync<Widget>(widget.Id);

        Assert.NotNull(loaded);
        Assert.Equal("loadable", loaded!.Name);
    }

    [Fact]
    public async Task Query_session_load_returns_null_when_absent()
    {
        await using var session = Sessions.OpenQuerySession();
        Assert.Null(await session.LoadAsync<Widget>(987654));
    }

    /// <summary>
    /// The read every migrated cluster performs: filter server-side, materialize with plain LINQ inside the
    /// repository so the IQueryable never leaks (D7).
    /// </summary>
    [Fact]
    public async Task Query_session_filters_and_materializes()
    {
        var widget = new Widget("queryable-" + Guid.NewGuid().ToString("N"));
        await NewRepository().AddAsync(widget);

        await using var session = Sessions.OpenQuerySession();
        var matches = session.Query<Widget>()
            .Where(w => w.Name == widget.Name)
            .ToList();

        Assert.Single(matches);
        Assert.Equal(widget.Id, matches[0].Id);
    }

    [Fact]
    public async Task Query_session_identity_map_returns_the_same_instance()
    {
        var widget = new Widget("identity-mapped");
        await NewRepository().AddAsync(widget);

        await using var session = Sessions.OpenQuerySession();
        var first = await session.LoadAsync<Widget>(widget.Id);
        var second = await session.LoadAsync<Widget>(widget.Id);

        // NotNull before Same: Assert.Same(null, null) passes, so without this the test would survive a
        // LoadAsync that returned null for everything.
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Document_session_stores_and_saves()
    {
        var widget = new Widget("session-stored");

        await using (var session = Sessions.OpenSession())
        {
            session.Store(widget);
            await session.SaveChangesAsync();
        }

        Assert.NotEqual(0, widget.Id);
        var reloaded = await NewRepository().GetByIdAsync(widget.Id)!;
        Assert.NotNull(reloaded);
        Assert.Equal("session-stored", reloaded!.Name);
    }

    [Fact]
    public async Task Document_session_deletes()
    {
        var widget = new Widget("session-deleted");
        await NewRepository().AddAsync(widget);

        await using (var session = Sessions.OpenSession())
        {
            var loaded = await session.LoadAsync<Widget>(widget.Id);
            session.Delete(loaded!);
            await session.SaveChangesAsync();
        }

        Assert.Null(await NewRepository().GetByIdAsync(widget.Id));
    }

    /// <summary>Nothing is written until SaveChangesAsync: Store alone must not reach the database.</summary>
    [Fact]
    public async Task Document_session_does_not_write_before_save_changes()
    {
        var widget = new Widget("unsaved-" + Guid.NewGuid().ToString("N"));

        await using (var session = Sessions.OpenSession())
        {
            session.Store(widget);
        }

        await using var verify = Sessions.OpenQuerySession();
        Assert.Empty(verify.Query<Widget>().Where(w => w.Name == widget.Name).ToList());
    }

    // --- outbox ---

    [Fact]
    public async Task Outbox_captures_domain_events_with_the_aggregate()
    {
        var widget = new Widget("with-events");
        widget.RaiseCreated();

        await NewRepository(serializer: new StubSerializer()).AddAsync(widget);

        await using var session = Sessions.OpenQuerySession();
        var outbox = session.Query<OutboxMessage>()
            .Where(m => m.EventType == nameof(WidgetCreated))
            .ToList();

        Assert.NotEmpty(outbox);
    }

    [Fact]
    public async Task Domain_events_are_tracked_when_no_outbox_serializer_is_registered()
    {
        var tracker = new AggregateTracker();
        var widget = new Widget("tracked-events");
        widget.RaiseCreated();

        await NewRepository(tracker: tracker).AddAsync(widget);

        Assert.NotEmpty(tracker.DequeueTracked());
    }

    // --- audit ---

    /// <summary>
    /// Both providers must stamp identically. On the relational path the stamps come from the change
    /// tracker; on the document path from an OnBeforeStore listener — two mechanisms, one rule
    /// (<see cref="AuditStamp"/>). This test is the guard against those two drifting apart.
    /// </summary>
    [Fact]
    public async Task Insert_stamps_created_and_modified()
    {
        var widget = new Widget("audited");
        await NewRepository().AddAsync(widget);

        var loaded = (await NewRepository().GetByIdAsync(widget.Id))!;

        Assert.Equal(AuditUser, loaded!.CreatedBy);
        Assert.NotEqual(default, loaded.CreatedOn);
        Assert.Equal(AuditUser, loaded.ModifiedBy);
        Assert.NotEqual(default, loaded.ModifiedOn);
    }

    [Fact]
    public async Task Update_stamps_modified_and_preserves_created()
    {
        var widget = new Widget("audited-update");
        await NewRepository().AddAsync(widget);
        var createdOn = widget.CreatedOn;

        var repository = NewRepository();
        var loaded = (await repository.GetByIdAsync(widget.Id))!;
        loaded!.Rename("audited-updated");
        await repository.UpdateAsync(loaded);

        var reloaded = await NewRepository().GetByIdAsync(widget.Id)!;
        Assert.Equal(AuditUser, reloaded!.ModifiedBy);
        Assert.Equal(AuditUser, reloaded.CreatedBy);
        // The creation stamp is never rewritten by an update.
        Assert.Equal(createdOn, reloaded.CreatedOn, TimeSpan.FromSeconds(1));
    }

    // --- test doubles ---

    protected sealed class StubSerializer : IDomainEventSerializer
    {
        public (string EventType, string Payload) Serialize(IDomainEvent domainEvent) =>
            (domainEvent.GetType().Name, "{}");

        public IDomainEvent? Deserialize(string eventType, string payload) => null;
    }

    protected sealed class SystemScopeTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => null;

        public bool IsSystemScope => true;
    }
}
