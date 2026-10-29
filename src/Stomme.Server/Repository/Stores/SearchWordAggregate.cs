using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SearchWord aggregate root — the persistence-side model of the framework store's <c>SearchWord</c>
    /// table. The DISTINCT-WORD DICTIONARY half of the hand-rolled inverted index. Reachable only
    /// through <see cref="ISearchWordStore"/> (writes) and <c>IQuerySession.Query</c> (reads).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row per distinct word across the whole tenant — <c>ix_search_word</c> is UNIQUE on <c>word</c>, with
    /// no site column. <c>SearchContentWord</c> joins it to the content that contains it, carrying the term
    /// frequency. Tenant-side only; neither <see cref="IAuditable"/> nor <see cref="IDeletable"/>, measured:
    /// <c>SearchWordEntityBuilder</c> extends <c>BaseEntityBuilder</c> and live <c>search_word</c> has exactly
    /// THREE columns (<c>search_word_id</c>, <c>word</c>, <c>created_on</c>) with zero dropped stubs.
    /// <c>created_on</c> is a plain column, not an audit stamp — see <see cref="SearchContentAggregate"/>.
    /// </para>
    /// <para>
    /// <b><see cref="Word"/> IS ALWAYS LOWERCASE, and the whole cluster's cross-provider correctness rests on
    /// it.</b> <c>DatabaseSearchProvider.GetWords</c> lowercases every word before it is stored
    /// (<c>term.ToLower(CultureInfo.CurrentCulture)</c>) and <c>SearchUtils.GetKeywords</c> lowercases every
    /// keyword before it is queried, so BOTH sides of the prefix filter are normalized. That is the
    /// normalized-column argument — for two lowercase strings, ordinal and case-insensitive matching are the same
    /// relation — satisfied here for free, and it is why this cluster needs no <c>_normalized</c> twin
    /// column. It matters because <c>StartsWith</c> diverges THREE ways: SQLite's <c>LIKE</c> is
    /// case-INSENSITIVE, PostgreSQL's is case-SENSITIVE, RavenDB's is case-INSENSITIVE. Store one
    /// non-lowercase word here and those three stop coinciding, silently. Pinned by
    /// <c>A_prefix_filter_is_expressible_on_every_provider</c>.
    /// </para>
    /// <para>
    /// <b>The word is NOT tokenized, and must never be.</b> <c>DatabaseSearchProvider.FormatContent</c>
    /// replaces <c>.?!,;:_()[]{}'"/\</c> with spaces but NOT hyphens, so <c>state-of-the-art</c> is ONE row
    /// here. The RavenDB index over this field therefore uses the DEFAULT analyzer, never
    /// <c>FieldIndexing.Search</c> — a full-text analyzer would split it into four terms and change the result
    /// set in both directions (measured: it misses <c>state-of*</c> and invents <c>art*</c>).
    /// </para>
    /// </remarks>
    public class SearchWordAggregate : AggregateRoot<int>
    {
        /// <summary>The distinct word. Always lowercase — see the remarks on this type.</summary>
        public string Word { get; set; }

        /// <summary>A PLAIN column, not an audit stamp.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Binds the aggregate to an existing row's identity for an update or a delete.</summary>
        internal void AssignId(int searchWordId) => Id = searchWordId;
    }
}
