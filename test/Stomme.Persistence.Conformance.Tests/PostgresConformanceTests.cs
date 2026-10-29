using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The conformance suite against the EF Core provider ON POSTGRESQL — the database the product
/// actually ships on, which the SQLite fixture (<see cref="EfCoreConformanceTests"/>) structurally cannot
/// represent (the limit the SQLite fixtures cannot overcome). Each test creates a scratch database on the shared test server
/// (<c>stomme_conformance_wgt_*</c>), <c>EnsureCreated</c>s the fixture context — exactly as the SQLite
/// twin does — and force-drops it in teardown; stale leftovers of a killed run are swept at initialization
/// (see <see cref="PostgresConformanceEndpoint"/>).
/// </summary>
/// <remarks>
/// FAILS, never skips, when PostgreSQL is unreachable — the fail-not-skip principle;
/// <see cref="PostgresParityCoverageTests"/> is the named diagnosis.
/// </remarks>
public sealed class PostgresConformanceTests : PersistenceConformanceTests
{
    internal const string DatabasePrefix = "stomme_conformance_wgt_";

    private readonly string _database = DatabasePrefix + Guid.NewGuid().ToString("N");
    private IDocumentSessionFactory _sessions = default!;

    protected override IDocumentSessionFactory Sessions => _sessions;

    public override async Task InitializeAsync()
    {
        await PostgresConformanceEndpoint.SweepStaleAsync(DatabasePrefix);
        await PostgresConformanceEndpoint.CreateDatabaseAsync(_database);

        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        _sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
        {
            [StoreNames.Main] = NewContext
        });
    }

    public override async Task DisposeAsync() => await PostgresConformanceEndpoint.DropDatabaseAsync(_database);

    protected override IRepository<Widget, int> NewRepository(
        IDomainEventSerializer? serializer = null,
        IAggregateTracker? tracker = null) =>
        // A fresh context per repository = a fresh change tracker, so two repositories can conflict.
        new EfRepository<Widget, int>(
            NewContext(),
            tracker ?? new AggregateTracker(),
            new SystemScopeTenantContext(),
            serializer is null ? [] : [serializer]);

    private ConformanceContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ConformanceContext>()
            .UseNpgsql(PostgresConformanceEndpoint.ConnectionString(_database))
            .Options;

        // Opt the seam's context in to audit stamping — same rationale as the SQLite twin.
        return new ConformanceContext(options, new SystemScopeTenantContext()) { Audit = Audit };
    }

    private sealed class ConformanceContext(DbContextOptions options, ITenantContext tenantContext)
        : StommeDbContext(options, tenantContext)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Widget>(widget =>
            {
                widget.HasKey(w => w.Id);
                // The framework store's shape: the database assigns the identity, so an insert leaves it 0.
                widget.Property(w => w.Id).ValueGeneratedOnAdd();
            });
        }
    }
}
