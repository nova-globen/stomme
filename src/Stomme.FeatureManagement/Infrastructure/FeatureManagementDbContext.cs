using System.Text.Json;
using Stomme.FeatureManagement.Domain;
using Stomme.Persistence.EfCore;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stomme.FeatureManagement.Infrastructure;

/// <summary>
/// The relational (EF Core) store for the Feature Management module's aggregates — the flags and their
/// append-only audit trail — persisted through the backlog-item-01 persistence seam. Inherits the shared
/// tenant query filter, optimistic-concurrency token, and outbox table from <see cref="StommeDbContext"/>.
/// The aggregate's read-only collections (role allow-list, per-user overrides) are stored as JSON columns
/// through their backing fields, so the domain model keeps its encapsulation.
/// </summary>
public sealed class FeatureManagementDbContext(DbContextOptions<FeatureManagementDbContext> options, ITenantContext tenantContext)
    : StommeDbContext(options, tenantContext)
{
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    public DbSet<FeatureFlagAudit> FeatureFlagAudits => Set<FeatureFlagAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeatureFlag>(flag =>
        {
            flag.ToTable("FeatureFlags");
            flag.HasKey(f => f.Id);
            flag.Property(f => f.Key).IsRequired().HasMaxLength(128);
            flag.HasIndex(f => f.Key);
            flag.Ignore(f => f.TargetRoles);
            flag.Ignore(f => f.UserOverrides);
            flag.Property<HashSet<string>>("_targetRoles").HasColumnName("TargetRoles").HasConversion(StringSetConverter, StringSetComparer);
            flag.Property<Dictionary<Guid, bool>>("_userOverrides").HasColumnName("UserOverrides").HasConversion(OverrideMapConverter, OverrideMapComparer);
        });

        modelBuilder.Entity<FeatureFlagAudit>(audit =>
        {
            audit.ToTable("FeatureFlagAudits");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Key).IsRequired().HasMaxLength(128);
            // Store the ordering timestamp as ticks so ORDER BY translates on every provider
            // (SQLite cannot order by DateTimeOffset).
            audit.Property(a => a.ChangedAtUtc)
                .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });

        // Base config (tenant filters, outbox, concurrency token, domain-event ignore) runs after the
        // entities are in the model.
        base.OnModelCreating(modelBuilder);
    }

    // Converter/comparer expressions compile to expression trees, so the lambdas only call methods.
    private static readonly ValueConverter<HashSet<string>, string> StringSetConverter =
        new(set => SerializeSet(set), json => DeserializeSet(json));

    private static readonly ValueComparer<HashSet<string>> StringSetComparer =
        new((a, b) => SetEquals(a, b), set => SetHash(set), set => SnapshotSet(set));

    private static readonly ValueConverter<Dictionary<Guid, bool>, string> OverrideMapConverter =
        new(map => SerializeOverrides(map), json => DeserializeOverrides(json));

    private static readonly ValueComparer<Dictionary<Guid, bool>> OverrideMapComparer =
        new((a, b) => OverridesEqual(a, b), map => OverridesHash(map), map => SnapshotOverrides(map));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string SerializeSet(HashSet<string> set) => JsonSerializer.Serialize(set, JsonOptions);

    private static HashSet<string> DeserializeSet(string json)
    {
        var items = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
        return new HashSet<string>(items ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private static bool SetEquals(HashSet<string>? a, HashSet<string>? b) => (a ?? []).SetEquals(b ?? new HashSet<string>());

    private static int SetHash(HashSet<string> set) =>
        set.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.GetHashCode(StringComparison.OrdinalIgnoreCase)));

    private static HashSet<string> SnapshotSet(HashSet<string> set) => new(set, StringComparer.OrdinalIgnoreCase);

    private static string SerializeOverrides(Dictionary<Guid, bool> map) => JsonSerializer.Serialize(map, JsonOptions);

    private static Dictionary<Guid, bool> DeserializeOverrides(string json)
    {
        var map = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<Guid, bool>>(json, JsonOptions);
        return map ?? [];
    }

    private static bool OverridesEqual(Dictionary<Guid, bool>? a, Dictionary<Guid, bool>? b)
    {
        a ??= [];
        b ??= [];
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }

    private static int OverridesHash(Dictionary<Guid, bool> map) =>
        map.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key.GetHashCode(), kv.Value.GetHashCode()));

    private static Dictionary<Guid, bool> SnapshotOverrides(Dictionary<Guid, bool> map) => new(map);
}
