using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The conformance suite against the EF Core provider — the reference implementation of the seam.
/// Backed by SQLite over a single shared in-memory connection, so every context in a test sees one
/// database while the tests stay isolated from each other.
/// </summary>
public sealed class EfCoreConformanceTests : PersistenceConformanceTests
{
    private SqliteConnection _connection = default!;
    private IDocumentSessionFactory _sessions = default!;

    protected override IDocumentSessionFactory Sessions => _sessions;

    public override async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await using (var context = NewContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        _sessions = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
        {
            [StoreNames.Main] = NewContext
        });
    }

    public override async Task DisposeAsync() => await _connection.DisposeAsync();

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
            .UseSqlite(_connection)
            .Options;

        // Opt the seam's context in to audit stamping. The framework store's aggregates are stamped today
        // by DbContextUtils from the LEGACY context overrides, which this context is not behind.
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
