using System.Linq.Expressions;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Persistence.File;

/// <summary>
/// A simple, RELIABLE file-based <see cref="IRepository{TAggregate, TId}"/> — the THIRD persistence provider
/// (D80), proving the module persistence registry is genuinely provider-extensible: an unchanged storage-blind
/// module (the Guestbook) runs on it with ZERO module edits. One aggregate is one JSON document under a
/// per-tenant directory (<c>{RootPath}/{tenantId}/{AggregateTypeName}/{id}.json</c>), so tenants are isolated
/// physically. All reads/writes use ASYNC file I/O (<see cref="System.IO.File.ReadAllTextAsync(string, CancellationToken)"/>
/// / <see cref="System.IO.File.WriteAllTextAsync(string, string?, CancellationToken)"/>); the D33 sync-over-async
/// allowlist stays at ONE.
/// <para>
/// <b>Serialization</b> is the SAME Newtonsoft path the RavenDB store uses (<see cref="FileStoreSerializer"/>),
/// so a private-constructor aggregate and its <see cref="IStringValueObject"/> value objects round-trip — DRIVEN
/// green by the Guestbook conformance suite (the <c>AuthorName</c> value object survives the trip).
/// </para>
/// <para>
/// <b>Not transactional and no optimistic concurrency</b> (a deliberately simple store): a write is a single
/// file overwrite, there is no outbox, and a concurrent writer last-wins. Domain events are enrolled with the
/// ambient <see cref="IAggregateTracker"/> for in-process dispatch by the unit of work, exactly as the EF store
/// does when no outbox serializer is registered.
/// </para>
/// <para>
/// <b>String semantics.</b> <see cref="FindAsync"/> compiles the predicate and filters in memory, so a raw string
/// <c>==</c> is ordinal/case-SENSITIVE — matching EF/SQLite and diverging from RavenDB, exactly as the D78
/// <c>FindAsync</c> contract documents (safe for non-string scalars; strings must use the normalized-column
/// pattern). <b>A VALUE OBJECT is different, and the difference is measured (BL-150):</b> in memory it is the value
/// object's OWN <c>Equals</c> that decides, so <c>e =&gt; e.Author == AuthorName.Create("ada lovelace")</c> matches
/// "Ada Lovelace" here (<c>AuthorName</c> compares lower-cased) and on RavenDB, but not on EF/SQLite, which compares the
/// converted string case-sensitively. Neither shape is in the safe set; this store matches EF only for raw strings.
/// </para>
/// </summary>
public sealed class FileRepository<TAggregate, TId>(
    ITenantContext tenantContext,
    IAggregateTracker aggregateTracker,
    ModuleFileStoreOptions options)
    : IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    public async Task<TAggregate?> GetByIdAsync(TId id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var document = FileStoreSerializer.Deserialize<TAggregate>(await System.IO.File.ReadAllTextAsync(path, ct));
        return document is not null && IsVisible(document) ? document : null;
    }

    public async Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default)
    {
        var directory = TypeDirectory();
        var results = new List<TAggregate>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var document = FileStoreSerializer.Deserialize<TAggregate>(await System.IO.File.ReadAllTextAsync(file, ct));
            if (document is not null && IsVisible(document))
            {
                results.Add(document);
            }
        }

        return results;
    }

    // GetAll + compile + in-memory filter. In-memory string == is ordinal/case-SENSITIVE (matches EF/SQLite;
    // diverges from RavenDB), and a value object compares by its OWN Equals (BL-150) — safe only for the non-string
    // predicates the IRepository.FindAsync contract documents. The Guestbook's ModuleId filter is an int equality.
    public async Task<IReadOnlyList<TAggregate>> FindAsync(
        Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var all = await GetAllAsync(ct);
        var matches = predicate.Compile();
        return all.Where(matches).ToList();
    }

    public async Task AddAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var path = PathFor(aggregate.Id);
        if (System.IO.File.Exists(path))
        {
            throw new InvalidOperationException(
                $"A {typeof(TAggregate).Name} with id '{aggregate.Id}' already exists in the file store.");
        }

        await WriteAsync(path, aggregate, ct);
        aggregateTracker.Track(aggregate);
    }

    public async Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await WriteAsync(PathFor(aggregate.Id), aggregate, ct);
        aggregateTracker.Track(aggregate);
    }

    public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var path = PathFor(aggregate.Id);
        if (System.IO.File.Exists(path))
        {
            // No async delete exists in the BCL; File.Delete is genuinely synchronous, not sync-over-async.
            System.IO.File.Delete(path);
        }

        aggregateTracker.Track(aggregate);
        return Task.CompletedTask;
    }

    private static async Task WriteAsync(string path, TAggregate aggregate, CancellationToken ct) =>
        await System.IO.File.WriteAllTextAsync(path, FileStoreSerializer.Serialize(aggregate), ct);

    // {RootPath}/{tenantId}/{AggregateTypeName}, created on demand. The ambient tenant partitions the tree, so
    // one tenant can never read another's files. A null/system scope maps to a stable "system" partition.
    private string TypeDirectory()
    {
        var tenant = tenantContext.IsSystemScope || tenantContext.CurrentTenantId is null
            ? "system"
            : tenantContext.CurrentTenantId.Value.ToString("N");

        var directory = System.IO.Path.Combine(options.RootPath, tenant, typeof(TAggregate).Name);
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private string PathFor(TId id) => System.IO.Path.Combine(TypeDirectory(), FileName(id));

    // The id is the file name. Guid/int/long ids are already file-safe; any other id is sanitized so a stray
    // path separator cannot escape the tenant/type directory.
    private static string FileName(TId id)
    {
        var raw = id.ToString() ?? string.Empty;
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(invalid, '_');
        }

        return raw + ".json";
    }

    private bool IsVisible(TAggregate aggregate) =>
        aggregate is not ITenantOwned owned
        || tenantContext.IsSystemScope
        || Equals(tenantContext.CurrentTenantId, owned.TenantId);
}
