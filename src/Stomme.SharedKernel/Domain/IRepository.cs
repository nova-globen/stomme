using System.Linq.Expressions;

namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Generic repository interface for aggregate roots.
/// Each bounded context should define specific repositories extending this.
/// </summary>
public interface IRepository<TAggregate, in TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(TAggregate aggregate, CancellationToken ct = default);
    Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default);
    Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default);

    /// <summary>
    /// Returns every aggregate matching <paramref name="predicate"/>, translated to the underlying store's
    /// native query (EF Core → SQL <c>WHERE</c>; RavenDB → an indexed session query) so the filter runs in the
    /// database, not in memory (the "keep the filter in the database" rule). This is the
    /// ONE provider-neutral filtered read a storage-blind module needs, so it no longer has to ship an EF query
    /// plus a RavenDB twin.
    /// <para>
    /// <b>THE CONTRACT — READ THIS.</b> The providers translate the same LINQ differently, and the compiler
    /// cannot see the divergence (CLAUDE.md "Providers diverge where the compiler cannot see it"). The predicate
    /// is guaranteed to behave IDENTICALLY on every provider ONLY when it is:
    /// <list type="bullet">
    ///   <item>equality, inequality or range/comparison (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    ///     <c>&gt;</c>, <c>&gt;=</c>) on a NON-STRING scalar: <c>int</c>, <c>long</c>, <c>Guid</c>, an
    ///     <c>enum</c>, <c>bool</c>, or <c>DateTime</c>;</item>
    ///   <item>boolean composition (<c>&amp;&amp;</c>, <c>||</c>, <c>!</c>) of such terms.</item>
    /// </list>
    /// <b>STRINGS ARE NOT SAFE and this method does NOT make them safe.</b> A raw string comparison DIVERGES:
    /// bare <c>==</c> is case-SENSITIVE on EF/PostgreSQL but case-INSENSITIVE on RavenDB; <c>StartsWith</c>
    /// splits three ways (SQLite insensitive, PostgreSQL SENSITIVE, RavenDB insensitive); and <c>.ToLower()</c>
    /// THROWS on RavenDB. To filter on text, apply the normalized-column pattern — persist a
    /// <c>*_normalized</c> (e.g. upper-invariant) column and compare the NORMALIZED column against the SAME
    /// normalization of the argument (<c>e =&gt; e.NameNormalized == name.ToUpperInvariant()</c>, the constant
    /// normalized in code, not translated by the provider). Do NOT pass a raw <c>.ToLower()</c>/mixed-case
    /// string comparison and expect agreement; it is measured to diverge (see the FindAsync conformance suite, which
    /// drives both the divergence and the normalized agreement).
    /// <b>A string VALUE OBJECT is a string here (measured on EF/SQLite, RavenDB and File).</b>
    /// <c>e =&gt; e.Author == AuthorName.Create("ada lovelace")</c> TRANSLATES on every provider, and diverges exactly
    /// as a raw string does - EF compares the converted string case-sensitively (0 rows for "Ada Lovelace"), RavenDB
    /// insensitively (1), and the File store by the value object's own <c>Equals</c> (1, because <c>AuthorName</c>
    /// compares lower-cased). Filter a text value object through a normalized column, like any other text.
    /// </para>
    /// </summary>
    /// <param name="predicate">A store-translatable filter honouring the contract above.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TAggregate>> FindAsync(
        Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"FindAsync is implemented by the provider repositories (EfRepository/RavenRepository). " +
            $"{GetType().Name} does not implement a store-translatable filtered read.");
}
