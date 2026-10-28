using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SearchContentWord aggregate root — the persistence-side model of the framework store's
    /// <c>SearchContentWord</c> table (D50). The TERM-FREQUENCY half of the hand-rolled inverted index:
    /// "word W occurs N times in content C". Reachable only through <see cref="ISearchContentWordStore"/>
    /// (writes) and <c>IQuerySession.Query</c> (reads).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ix_search_content_word</c> is UNIQUE on <c>(search_content_id, search_word_id)</c>. Neither
    /// <see cref="IAuditable"/> nor <see cref="IDeletable"/>, measured: <c>SearchContentWordEntityBuilder</c>
    /// extends <c>BaseEntityBuilder</c> and live <c>search_content_word</c> has exactly SIX columns with zero
    /// dropped stubs. <c>created_on</c>/<c>modified_on</c> are PLAIN columns that
    /// <c>DatabaseSearchProvider.AnalyzeSearchContentAsync</c> sets by hand from <c>TimeProvider</c> — they
    /// look like audit stamps and are not, and there is no <c>created_by</c>/<c>modified_by</c> to pair them
    /// with. See <see cref="SearchContentAggregate"/>.
    /// </para>
    /// <para>
    /// <b>THE TWO NAVIGATIONS ARE SEVERED AT THE CONTRACT (D45/D48/D49).</b> <c>SearchContentWord.SearchWord</c>
    /// and <c>SearchContentWord.SearchContent</c> are mapped navigations on the wire contract; with
    /// <see cref="SearchWordAggregate"/> and <see cref="SearchContentAggregate"/> mapped onto the same tables,
    /// EF would rediscover those contracts THROUGH the navigations and break the model at startup — the
    /// mechanism D45 observed and D48 met again. They are <c>[NotMapped]</c> now, and this aggregate carries
    /// only the two ids. <c>SearchContentRepository.GetSearchContentWordsAsync</c> stitches the
    /// <c>SearchWord</c> member back on from a second query, exactly as D45's
    /// <c>PopulateSiteGroups</c> does — and it is load-bearing, because
    /// <c>DatabaseSearchProvider.AnalyzeSearchContentAsync</c> reads <c>existing.SearchWord?.Word</c> to decide
    /// insert-vs-update for every word of every indexed document.
    /// </para>
    /// <para>
    /// <b>Why the search read cannot be a session query.</b> The read filters <see cref="SearchWordAggregate.Word"/>
    /// by prefix and sums <see cref="Count"/> per content — an aggregation over a subset chosen at RUNTIME by
    /// the caller's keywords. RavenDB serves a dynamic <c>GroupBy</c>+<c>Sum</c> only while every filter arm
    /// touches the group-by key, and this one does not. Hence <see cref="ISearchQueryStore"/>: EF keeps the
    /// <c>GROUP BY</c>/<c>SUM</c> in SQL, RavenDB does the same work with a query-time FACET
    /// (<c>AggregateBy</c>/<c>SumOn</c>) over a static index. Same numbers, different mechanism. See D50.
    /// </para>
    /// </remarks>
    public class SearchContentWordAggregate : AggregateRoot<int>
    {
        /// <summary>The indexed content. A reference to another aggregate BY ID (D7/D19) — see the remarks.</summary>
        public int SearchContentId { get; set; }

        /// <summary>The word. A reference to another aggregate BY ID (D7/D19) — see the remarks.</summary>
        public int SearchWordId { get; set; }

        /// <summary>
        /// The TERM FREQUENCY: how many times this word occurs in this content. Summed across the MATCHED
        /// words to produce <c>SearchContent.Count</c>, which becomes <c>SearchResult.Score</c> and sorts the
        /// results. This is the number both providers must agree on exactly.
        /// </summary>
        public int Count { get; set; }

        /// <summary>A PLAIN column, not an audit stamp.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>A PLAIN column, not an audit stamp.</summary>
        public DateTime ModifiedOn { get; set; }

        /// <summary>Binds the aggregate to an existing row's identity for an update or a delete.</summary>
        internal void AssignId(int searchContentWordId) => Id = searchContentWordId;
    }
}
