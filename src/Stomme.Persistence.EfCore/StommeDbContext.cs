using System.Reflection;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stomme.Persistence.EfCore;

/// <summary>Maps the <see cref="TenantId"/> value object to its underlying GUID column.</summary>
internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => TenantId.From(value));

/// <summary>
/// Base EF Core context for Stomme stores. Applies shared-schema tenant isolation (a global query
/// filter on <see cref="ITenantOwned"/> entities, keyed off the ambient <see cref="ITenantContext"/>)
/// and keeps domain-event bookkeeping out of the model. Derived contexts add their <c>DbSet</c>s.
/// Separate-database isolation is achieved by constructing the context with a per-tenant connection.
/// </summary>
public abstract class StommeDbContext : DbContext
{
    private static readonly MethodInfo ApplyTenantConfigurationMethod =
        typeof(StommeDbContext).GetMethod(nameof(ApplyTenantConfiguration), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly ITenantContext _tenantContext;

    protected StommeDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options) => _tenantContext = tenantContext;

    /// <summary>Current tenant (null in system scope); referenced by tenant query filters.</summary>
    public TenantId? CurrentTenant => _tenantContext.CurrentTenantId;

    /// <summary>True in system scope; referenced by tenant query filters to bypass them.</summary>
    public bool SystemScope => _tenantContext.IsSystemScope;

    /// <summary>
    /// Default database schema for schema-per-tenant isolation, set by the tenant-aware context factory.
    /// Null (the default) leaves the provider default schema. The model is cached per schema (see
    /// <see cref="TenantSchemaModelCacheKeyFactory"/>), so a tenant's tables resolve under its own schema.
    /// </summary>
    public string? Schema { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // Treat TenantId as a scalar everywhere, so EF never maps it as a related entity.
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Schema-per-tenant: route all tables to the tenant's schema (set by the tenant-aware factory).
        if (!string.IsNullOrEmpty(Schema))
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        // The transactional outbox table, shared by all Stomme contexts.
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("OutboxMessages");
            outbox.HasKey(m => m.Id);
            outbox.HasIndex(m => m.ProcessedOnUtc);
            outbox.Property(m => m.EventType).IsRequired();
            outbox.Property(m => m.Payload).IsRequired();
            // Store the ordering timestamp as ticks so ORDER BY translates on every provider
            // (SQLite cannot order by DateTimeOffset).
            outbox.Property(m => m.OccurredOnUtc)
                .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            // Retry-backoff gate, also stored as nullable ticks for cross-provider comparison.
            outbox.Property(m => m.NextAttemptOnUtc)
                .HasConversion(
                    v => v == null ? (long?)null : v.Value.UtcTicks,
                    v => v == null ? (DateTimeOffset?)null : new DateTimeOffset(v.Value, TimeSpan.Zero));
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            // Domain events are an in-memory concern, never persisted.
            if (typeof(IHasDomainEvents).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Ignore("DomainEvents");
            }

            // Optimistic concurrency: the aggregate's Version is the concurrency token. A stale write
            // (Version no longer matching the row) updates 0 rows and surfaces as a concurrency conflict.
            // Stored as a 64-bit integer so it maps uniformly across providers (PostgreSQL has no native
            // unsigned int and would otherwise treat a uint as a transaction-id type).
            if (IsAggregateRoot(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .Property(nameof(AggregateRoot<int>.Version))
                    .HasConversion<long>()
                    .IsConcurrencyToken();
            }

            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantConfigurationMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    /// <summary>
    /// Stamps <c>CreatedBy</c>/<c>CreatedOn</c>/<c>ModifiedBy</c>/<c>ModifiedOn</c> on save when set.
    /// <para>
    /// Null by default, so this is strictly additive: the framework store's aggregates are stamped today by
    /// <c>DbContextUtils</c> from the LEGACY context overrides (<c>TenantDBContext</c> derives from
    /// <c>IdentityUserContext</c>, not from this class), and leaving stamping off unless a host opts in
    /// means that path cannot double-stamp. Set it on a context that is NOT behind <c>DbContextUtils</c> —
    /// the seam's intended shape — to keep audit parity with the document provider.
    /// </para>
    /// </summary>
    public IAuditContext? Audit { get; set; }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpAggregateVersions();
        StampAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpAggregateVersions();
        StampAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // The audit rule itself lives in AuditStamp so the relational and document providers cannot drift; only
    // the "insert vs update" signal is provider-specific. Here the change tracker answers it exactly.
    private void StampAudit()
    {
        if (Audit is not { } audit)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                AuditStamp.StampCreated(entry.Entity, audit);
            }
            else if (entry.State == EntityState.Modified)
            {
                AuditStamp.StampModified(entry.Entity, audit);
            }
        }
    }

    // Stamp the next version on every aggregate being written: a new aggregate starts at 1; an update
    // advances the loaded version by one. EF keeps the loaded value as the concurrency token's original
    // value, so the UPDATE matches on it (WHERE Version = original) while writing the incremented value.
    private void BumpAggregateVersions()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (!IsAggregateRoot(entry.Entity.GetType()))
            {
                continue;
            }

            var version = entry.Property(nameof(AggregateRoot<int>.Version));
            if (entry.State == EntityState.Added)
            {
                version.CurrentValue = 1u;
            }
            else if (entry.State == EntityState.Modified)
            {
                version.CurrentValue = (uint)(version.OriginalValue ?? 0u) + 1u;
            }
        }
    }

    private void ApplyTenantConfiguration<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => SystemScope || e.TenantId == CurrentTenant);

    // Shared with the change notifier so both agree on what counts as an aggregate write.
    internal static bool IsAggregateRoot(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
            {
                return true;
            }
        }

        return false;
    }
}
