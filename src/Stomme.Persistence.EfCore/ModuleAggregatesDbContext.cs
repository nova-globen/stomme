using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// The host-owned registry of module aggregate CLR types that the shared
/// <see cref="ModuleAggregatesDbContext"/> maps (D79). A storage-blind module registers its aggregate here
/// through <c>AddModuleAggregatePersistence&lt;TAggregate, TId&gt;()</c> and authors NO EF <c>DbContext</c> of
/// its own — the shared context reflects over this set in <see cref="ModuleAggregatesDbContext.OnModelCreating"/>.
/// It is a mutable singleton because module registration runs at composition time, before the host boots and
/// before the first context (and therefore the EF model) is built; the set is frozen for the process's life
/// by the time any request resolves a repository.
/// </summary>
public sealed class ModuleAggregateRegistry
{
    private readonly HashSet<System.Type> _types = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, string> _quarantined = new();

    /// <summary>Enrols an aggregate type into the shared context's model. Idempotent.</summary>
    public void Add(System.Type aggregateType) => _types.Add(aggregateType);

    /// <summary>
    /// The aggregate types the shared model maps: every registered one except those quarantined. Every module aggregate
    /// shares ONE model, so a single unmappable aggregate used to fail the model - and every module on the store with
    /// it (BL-263, measured). It is quarantined instead: left out of the model, its repository refusing by name.
    /// </summary>
    public IReadOnlyCollection<System.Type> AggregateTypes => _types.Where(type => !_quarantined.ContainsKey(type)).ToList();

    /// <summary>Every registered aggregate type, quarantined ones included.</summary>
    public IReadOnlyCollection<System.Type> RegisteredTypes => _types;

    /// <summary>The quarantined aggregate types and why each could not be mapped.</summary>
    public IReadOnlyDictionary<System.Type, string> Quarantined => _quarantined;

    /// <summary>Leaves <paramref name="aggregateType"/> out of the shared model. True the first time.</summary>
    public bool Quarantine(System.Type aggregateType, string reason) => _quarantined.TryAdd(aggregateType, reason);

    public bool IsQuarantined(System.Type aggregateType, out string reason) =>
        _quarantined.TryGetValue(aggregateType, out reason!);
}

/// <summary>
/// The repository a quarantined module aggregate gets (BL-263): every call refuses, naming the aggregate and why it could
/// not be mapped - never an empty read that looks like "no data". The rest of the module store is unaffected.
/// </summary>
public sealed class QuarantinedModuleRepository<TAggregate, TId>(string reason) : Stomme.SharedKernel.Domain.IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    private System.InvalidOperationException Refusal() => new(
        $"The module aggregate {typeof(TAggregate).FullName} cannot be mapped into the shared module store, so its module " +
        $"is switched off on this host (every other module works): {reason}");

    public System.Threading.Tasks.Task<TAggregate?> GetByIdAsync(TId id, System.Threading.CancellationToken ct = default) => throw Refusal();

    public System.Threading.Tasks.Task<IReadOnlyList<TAggregate>> GetAllAsync(System.Threading.CancellationToken ct = default) => throw Refusal();

    public System.Threading.Tasks.Task AddAsync(TAggregate aggregate, System.Threading.CancellationToken ct = default) => throw Refusal();

    public System.Threading.Tasks.Task UpdateAsync(TAggregate aggregate, System.Threading.CancellationToken ct = default) => throw Refusal();

    public System.Threading.Tasks.Task DeleteAsync(TAggregate aggregate, System.Threading.CancellationToken ct = default) => throw Refusal();

    public System.Threading.Tasks.Task<IReadOnlyList<TAggregate>> FindAsync(Expression<System.Func<TAggregate, bool>> predicate, System.Threading.CancellationToken ct = default) => throw Refusal();
}

/// <summary>
/// Builds the shared model with each aggregate isolated (BL-263). A mapping failure inside the per-aggregate step is
/// caught by <see cref="ModuleAggregatesDbContext.OnModelCreating"/> itself; one EF Core only raises when it validates the
/// FINISHED model is found here, by building each aggregate's model alone and quarantining those that fail.
/// </summary>
public static class ModuleAggregateModelGuard
{
    /// <summary>Builds the shared model, quarantining what cannot be mapped. Returns the newly quarantined types.</summary>
    public static IReadOnlyCollection<System.Type> Build(
        DbContextOptions<ModuleAggregatesDbContext> options, ITenantContext tenantContext, ModuleAggregateRegistry registry)
    {
        var before = registry.Quarantined.Keys.ToHashSet();
        try
        {
            using var context = new ModuleAggregatesDbContext(options, tenantContext, registry);
            _ = context.Model;
        }
        catch (System.Exception)
        {
            foreach (var type in registry.AggregateTypes)
            {
                var alone = new ModuleAggregateRegistry();
                alone.Add(type);
                try
                {
                    using var probe = new ModuleAggregatesDbContext(options, tenantContext, alone);
                    _ = probe.Model;
                }
                catch (System.Exception ex)
                {
                    registry.Quarantine(type, ex.Message);
                }
            }

            using var rebuilt = new ModuleAggregatesDbContext(options, tenantContext, registry);
            _ = rebuilt.Model; // what is left maps; if not, the failure is not one aggregate's and surfaces as before
        }

        return registry.Quarantined.Keys.Where(type => !before.Contains(type)).ToList();
    }
}

/// <summary>
/// A single host-owned EF Core context that maps EVERY storage-blind module aggregate (D79), so a module can be
/// persisted on a relational install without authoring its own <see cref="DbContext"/>, value-object converter
/// or provider branch. <see cref="OnModelCreating"/> reflects over the <see cref="ModuleAggregateRegistry"/> and
/// adds each registered aggregate to the model; base <see cref="StommeDbContext"/> then applies the aggregate
/// concurrency token, the <see cref="ITenantOwned"/> query filter and the domain-event ignore reflectively (so a
/// tenant-owned module aggregate is isolated for free, and a module-scoped one like the guestbook is simply
/// unfiltered). Aggregates are added to the model BEFORE the base reflection runs, exactly as a hand-written
/// module context did.
/// <para>
/// <b>Value objects by convention (the D76 "EntityBuilders lie" risk).</b> For any aggregate property whose type
/// implements <see cref="IStringValueObject"/> the context applies a generic
/// <see cref="ValueConverter{TModel, TProvider}"/> that writes <c>Value</c> and reconstructs through the value
/// object's public <c>static Create(string)</c> factory — so the column is a plain string and the module hand-
/// writes no converter. A property typed as a <see cref="ValueObject"/> that carries NO supported marker is
/// REJECTED loudly (<see cref="System.NotSupportedException"/>) rather than left for EF to mis-map as an owned
/// entity; the only shape mapped by convention today is the string value object.
/// </para>
/// <para>
/// <b>Schema.</b> A dynamic reflective model cannot carry EF migrations, so the shared store's schema is brought
/// up with <c>EnsureCreated</c> (a known limit, documented on the helper) — hence the store is a STANDALONE
/// database, never the framework's migrated one. Aggregates enrolled AFTER the database first exists (a module
/// installed from the marketplace, then a restart) get their missing tables created ADDITIVELY by the schema
/// initializer at startup (BL-52) — existing tables and data are never touched.
/// </para>
/// </summary>
public sealed class ModuleAggregatesDbContext : StommeDbContext
{
    private readonly ModuleAggregateRegistry _registry;

    public ModuleAggregatesDbContext(
        DbContextOptions<ModuleAggregatesDbContext> options,
        ITenantContext tenantContext,
        ModuleAggregateRegistry registry)
        : base(options, tenantContext) => _registry = registry;

    /// <summary>
    /// A stable fingerprint of the mapped aggregate set, used by <see cref="ModuleAggregatesModelCacheKeyFactory"/>
    /// so two containers in one process (the EF and Raven conformance fixtures, N modules) that register DIFFERENT
    /// aggregate sets do NOT share EF's process-wide model cache — which is keyed by context CLR type by default
    /// and would otherwise let the first-built model silently mis-map the second container's aggregates.
    /// </summary>
    internal string AggregateSignature => string.Join(
        "|", _registry.AggregateTypes.Select(type => type.FullName).OrderBy(name => name, System.StringComparer.Ordinal));

    /// <summary>
    /// Every module-aggregate <see cref="System.DateTime"/> is UTC, and reads back SAYING so (BL-117).
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-09-23 (<c>HealthAggregateConformanceTests</c>, five stores): SQLite and SQL Server hand a stored
    /// instant back with <c>Kind = Unspecified</c>; PostgreSQL (<c>timestamptz</c>), RavenDB and the File store return
    /// <c>Utc</c>. <c>DateTime</c> equality ignores <c>Kind</c>, so every round-trip assertion passed while the kinds
    /// diverged - and a later <c>ToUniversalTime()</c> or ISO rendering then shifts by the host's offset on a non-UTC
    /// host, on two providers only. The convention stamps <c>Utc</c> on READ, and converts a <c>Local</c> value to UTC on
    /// WRITE; an <c>Unspecified</c> value is written as it is - PostgreSQL still refuses it loudly, which is the
    /// behaviour to keep, and every module's own factories already stamp UTC.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // Covers DateTime? as well - MEASURED: with a second, explicit DateTime? line removed, the nullable members of the
        // health alert row still read back Utc on SQLite and SQL Server, so that line was dead and is not here.
        configurationBuilder.Properties<System.DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jsonElementTypes = new HashSet<System.Type>();

        // BL-263: the class name is the table name in this one store, and EF cannot put two classes in one table. A clash
        // quarantines the module's aggregate when exactly one framework aggregate holds the name (it keeps it), and every
        // one of them otherwise - two modules, or two framework assemblies, have no rightful owner.
        foreach (var clash in _registry.AggregateTypes.GroupBy(type => type.Name).Where(group => group.Count() > 1))
        {
            var framework = clash.Where(type => type.Assembly.GetName().Name?.StartsWith("Stomme.", System.StringComparison.Ordinal) == true).ToList();
            foreach (var type in clash.Where(type => framework.Count != 1 || !framework.Contains(type)))
            {
                _registry.Quarantine(type, $"another module aggregate is also named '{type.Name}', and the class name is its table name in the shared store; rename it.");
            }
        }

        foreach (var aggregate in _registry.AggregateTypes)
        {
            try
            {
                MapAggregate(modelBuilder, aggregate, jsonElementTypes);
            }
            catch (System.Exception ex) when (ex is System.NotSupportedException or System.InvalidOperationException or System.ArgumentException)
            {
                // BL-263: one aggregate that cannot be mapped is left out - quarantined, its repository refusing by name -
                // instead of failing the model every other module on the store shares.
                _registry.Quarantine(aggregate, ex.Message);
                modelBuilder.Ignore(aggregate);
            }
        }

        // EF's discovery ran when each aggregate was added (before its collection was configured explicitly) and may
        // have left an element type behind as an entity of its own — MEASURED: an Entity<Guid> child list still
        // produced an empty "CollectionProbeAttempt" table beside the JSON column. The collection lives in the column,
        // so an element type that is not itself a registered aggregate is removed from the model.
        foreach (var elementType in jsonElementTypes)
        {
            if (!_registry.AggregateTypes.Contains(elementType) && modelBuilder.Model.FindEntityType(elementType) is not null)
            {
                modelBuilder.Ignore(elementType);
            }
        }

        // Base config (aggregate version token, outbox table, tenant query filter, domain-event ignore) runs
        // after the aggregates are in the model, so its reflective loop sees them.
        base.OnModelCreating(modelBuilder);
    }

    private static void MapAggregate(ModelBuilder modelBuilder, System.Type aggregate, HashSet<System.Type> jsonElementTypes)
    {
        {
            var entity = modelBuilder.Entity(aggregate);

            foreach (var property in aggregate.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(IStringValueObject).IsAssignableFrom(property.PropertyType))
                {
                    var propertyBuilder = entity
                        .Property(property.PropertyType, property.Name)
                        .HasConversion(StringValueObjectConverter.For(property.PropertyType));

                    if (MaxLengthOf(property.PropertyType) is { } maxLength)
                    {
                        propertyBuilder.HasMaxLength(maxLength);
                    }
                }
                else if (property.PropertyType == typeof(TenantId))
                {
                    // NOT an unsupported shape: StommeDbContext.ConfigureConventions already registers a
                    // GLOBAL TenantId -> Guid conversion (TenantIdConverter), so EF maps this as a scalar
                    // column exactly like a hand-written module context would. The reflective guard below
                    // predates any tenant-owned module aggregate and did not know that.
                    // MEASURED (endgame proof-of-life, 2026-08-04; ported under D98 Wave 0 / BL-92): without
                    // this branch EVERY aggregate implementing ITenantOwned — i.e. every tenant-scoped the predecessor platform
                    // aggregate, 38 of 47 — threw at model build. The throw was caught and logged by
                    // ModuleAggregateStoreInitializer ("it will read as empty"), so the module silently
                    // persisted NOTHING until the first query surfaced the same exception through the mediator.
                    // The existing storage-blind modules (Guestbook, Blogs) are module-scoped rather than
                    // tenant-owned, which is why this had never fired. Gate: ModuleAggregateTenantOwnedPersistenceTests.
                }
                else if (ModuleAggregateJsonCollections.IsJsonCollection(property))
                {
                    // BL-78 (D100 point 7): a collection property is ONE JSON column read/written through its backing
                    // field. Configured explicitly here so it wins over EF's own discovery, which (MEASURED) left
                    // enum/string lists silently unmapped, turned an Entity<Guid> child list into an EF-only
                    // navigation table, and threw for a keyless record element. Gate:
                    // ModuleAggregateCollectionMappingTests + CollectionBearingAggregateConformanceTests.
                    jsonElementTypes.Add(ModuleAggregateJsonCollections.Apply(entity, aggregate, property));
                }
                else if (typeof(ValueObject).IsAssignableFrom(property.PropertyType))
                {
                    // A value object EF cannot map as a scalar by convention (it has no key and a private
                    // constructor). Fail loudly rather than let EF interpret it as an owned entity and silently
                    // mis-map it — the D79 brief's required outcome for an unsupported VO shape.
                    throw new System.NotSupportedException(
                        $"The shared module store cannot map property '{aggregate.Name}.{property.Name}' of type " +
                        $"'{property.PropertyType.Name}': only string value objects (IStringValueObject with a public " +
                        "static Create(string) factory) are supported by convention today. Register the aggregate " +
                        "through the AddModuleAggregatePersistence overload that takes the module's own DbContext, or " +
                        "add support for this value-object shape.");
                }
            }
        }
    }

    // A public const/static int named MaxLength on the value object (AuthorName.MaxLength) sizes the column,
    // matching what a hand-written converter set. Absent, the column is an unbounded string — still a plain
    // string, which is all the convention promises.
    private static int? MaxLengthOf(System.Type valueObjectType)
    {
        var field = valueObjectType.GetField("MaxLength", BindingFlags.Public | BindingFlags.Static);
        return field is { IsLiteral: true } && field.FieldType == typeof(int)
            ? (int?)field.GetRawConstantValue()
            : null;
    }
}

/// <summary>
/// Builds a <see cref="ValueConverter"/> that persists an <see cref="IStringValueObject"/> as its <c>Value</c>
/// string and reconstructs it through the value object's public <c>static Create(string)</c> factory — the same
/// contract the RavenDB seam uses (<c>ValueObjectJsonConverters</c>), so both providers reconstruct a value
/// object the identical way.
/// </summary>
internal static class StringValueObjectConverter
{
    private static readonly MethodInfo BuildMethod =
        typeof(StringValueObjectConverter).GetMethod(nameof(Build), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static ValueConverter For(System.Type valueObjectType) =>
        (ValueConverter)BuildMethod.MakeGenericMethod(valueObjectType).Invoke(null, null)!;

    private static ValueConverter Build<TValueObject>()
        where TValueObject : class, IStringValueObject
    {
        var create = typeof(TValueObject).GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new System.NotSupportedException(
                $"'{typeof(TValueObject).Name}' must expose a public static {typeof(TValueObject).Name} Create(string) " +
                "factory to be persisted as a string value object by convention.");

        var raw = Expression.Parameter(typeof(string), "raw");
        Expression<System.Func<string, TValueObject>> from =
            Expression.Lambda<System.Func<string, TValueObject>>(
                Expression.Convert(Expression.Call(create, raw), typeof(TValueObject)), raw);

        Expression<System.Func<TValueObject, string>> to = value => value.Value;
        return new ValueConverter<TValueObject, string>(to, from);
    }
}

/// <summary>
/// Keys EF's model cache on the mapped aggregate set (plus schema) instead of only the context CLR type, so two
/// containers registering different aggregate sets into <see cref="ModuleAggregatesDbContext"/> in one process
/// each get their own model rather than the first-built one silently serving both.
/// </summary>
public sealed class ModuleAggregatesModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is ModuleAggregatesDbContext moduleContext
            ? (context.GetType(), moduleContext.Schema, moduleContext.AggregateSignature, designTime)
            : (object)(context.GetType(), designTime);

    public object Create(DbContext context) => Create(context, false);
}

/// <summary>Reads a stored instant back as UTC; writes a Local one as UTC (see <see cref="ModuleAggregatesDbContext"/>).</summary>
internal sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<System.DateTime, System.DateTime>(
    value => value.Kind == System.DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => System.DateTime.SpecifyKind(value, System.DateTimeKind.Utc));
