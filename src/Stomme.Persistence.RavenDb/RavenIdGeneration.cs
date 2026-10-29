using System.Collections.Concurrent;
using Raven.Client.Documents;
using Raven.Client.Documents.Identity;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Assigns database-generated identities to aggregates whose id the caller leaves unset.
/// <para>
/// The framework store's inserts deliberately leave <c>Id</c> at its default and expect the database to
/// assign one (the relational model maps <c>Id</c> as <c>ValueGeneratedOnAdd</c>). Without this port the
/// RavenDB provider addressed every such insert as <c>{TypeName}/0</c> — a blind upsert that silently
/// destroyed the previously inserted document and handed the caller back <c>Id = 0</c>.
/// </para>
/// </summary>
public interface IRavenIdGenerator
{
    /// <summary>True when this generator can produce ids of <paramref name="idType"/>.</summary>
    bool CanGenerate(Type idType);

    /// <summary>
    /// The next identity for <paramref name="tag"/>. Ids are unique and monotonically increasing per tag.
    /// </summary>
    ValueTask<object> NextIdAsync(string tag, Type idType, string? database = null, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IRavenIdGenerator"/> over RavenDB's HiLo generator.
/// <para>
/// <b>Why HiLo.</b> The server hands each client a reserved range ("hi") and the client allocates within it
/// without a round-trip, so an insert costs no extra call in the common case, and ranges are reserved
/// atomically per tag so concurrent clients and processes cannot collide. The alternative —
/// <c>NextIdentityForOperation</c> — is a server round-trip per insert; the install path alone performs
/// hundreds.
/// </para>
/// <para>
/// <b>Why this works despite <c>FindIdentityProperty = _ => false</c>.</b> That convention only disables
/// RavenDB's <i>implicit</i> identity handling — the client neither reads an id off the entity to build a
/// document id nor writes a server-assigned one back. HiLo is invoked here <i>explicitly</i> and the id is
/// assigned to the aggregate before the document id is derived from it, so the two are independent. This is
/// verified live, not assumed: see <c>Id_generation</c> in the conformance suite, which inserts two
/// aggregates with id 0 and asserts they receive distinct ids and both survive.
/// </para>
/// <para>
/// <b>Lifetime.</b> Register as a singleton. Each <see cref="AsyncHiLoIdGenerator"/> caches its reserved
/// range in memory; a per-scope instance would burn a fresh server range on every request. Correctness does
/// not depend on this (ranges never overlap) but throughput does.
/// </para>
/// </summary>
public sealed class RavenHiLoIdGenerator(IDocumentStore store) : IRavenIdGenerator
{
    private readonly ConcurrentDictionary<(string Tag, string Database), AsyncHiLoIdGenerator> _generators = new();

    /// <summary>
    /// Integral ids only. A <c>Guid</c> or <c>string</c> id is caller-assigned by construction (the caller
    /// cannot have a meaningful default), so generating one would mask a caller bug rather than fix it.
    /// </summary>
    public bool CanGenerate(Type idType) => idType == typeof(int) || idType == typeof(long);

    /// <summary>
    /// Drops every cached range: after a restore wrote documents with their ORIGINAL ids, a range this process
    /// reserved earlier may lie below them and hand out an id that overwrites one. The next id reserves afresh from the
    /// server, whose HiLo document the restore has moved past the restored ids.
    /// </summary>
    public void Forget() => _generators.Clear();

    public async ValueTask<object> NextIdAsync(
        string tag,
        Type idType,
        string? database = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(idType);
        if (!CanGenerate(idType))
        {
            throw new NotSupportedException($"RavenDB id generation does not support '{idType.Name}' ids.");
        }

        // AsyncHiLoIdGenerator takes the concrete DocumentStore: it needs the store's request executor to
        // reserve ranges.
        var documentStore = store as DocumentStore
            ?? throw new InvalidOperationException(
                $"RavenDB id generation requires a {nameof(DocumentStore)}; got '{store.GetType().Name}'.");

        var resolved = string.IsNullOrEmpty(database) ? documentStore.Database : database;
        var generator = _generators.GetOrAdd(
            (tag, resolved),
            static (key, s) => new AsyncHiLoIdGenerator(key.Tag, s, key.Database, s.Conventions.IdentityPartsSeparator),
            documentStore);

        ct.ThrowIfCancellationRequested();
        var next = await generator.GetNextIdAsync().ConfigureAwait(false);
        // Cast the int arm to object explicitly: without it both arms unify to long and an int id would be
        // boxed as a long, which then fails to assign to an int Id property.
        return idType == typeof(int) ? checked((int)next.Id) : (object)next.Id;
    }
}
