using Stomme.Tenancy.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Shared;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries.Facets;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// <b>The RavenDB static index behind the search read.</b> One entry per
    /// <see cref="SearchContentWordAggregate"/>, with the word and the owning content's filterable columns
    /// pulled across by <c>LoadDocument</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an index at all.</b> The harvested query is a two-level JOIN — it filters
    /// <c>SearchContent.SiteId</c> THROUGH <c>SearchContentWord.SearchContent</c>. A document store has no
    /// joins, and a dynamic RavenDB query cannot <c>LoadDocument</c>. Without this index the site arm would
    /// have to run in memory, which D22 rule 9 forbids. With it, every filter arm stays in the store —
    /// measured: the other site's row is excluded by the server, not by the client.
    /// </para>
    /// <para>
    /// <b><see cref="Entry.Word"/> USES THE DEFAULT ANALYZER, AND THAT IS LOAD-BEARING.</b> Do not add
    /// <c>Index(x =&gt; x.Word, FieldIndexing.Search)</c>. A full-text analyzer tokenizes on punctuation that
    /// <c>DatabaseSearchProvider.FormatContent</c> deliberately does NOT split — hyphens — so
    /// <c>state-of-the-art</c> is ONE row in <c>search_word</c> but FOUR tokens to an analyzer. Measured
    /// against a real <c>FieldIndexing.Search</c> index, in both directions: it MISSES <c>state-of*</c> (0 docs
    /// where <c>StartsWith</c> finds 1) and INVENTS <c>art*</c> (1 doc where <c>StartsWith</c> finds 0). The
    /// default analyzer indexes the whole value, so <c>StartsWith</c> means the same thing here as it does in
    /// <c>WHERE word LIKE 'run%'</c>. See D50 §3 and <see cref="SearchWordAggregate"/>.
    /// </para>
    /// <para>
    /// The word is always lowercase (<c>GetWords</c> lowercases on write, <c>SearchUtils.GetKeywords</c> on
    /// read), which is what makes RavenDB's case-INSENSITIVE prefix match and PostgreSQL's case-SENSITIVE one
    /// agree. See <see cref="SearchWordAggregate"/> for why that is the whole cluster's correctness argument.
    /// </para>
    /// </remarks>
    public sealed class SearchContentWordIndex : AbstractIndexCreationTask<SearchContentWordAggregate>
    {
        /// <summary>The index entry: the word, the term frequency, and the owning content's filter columns.</summary>
        public sealed class Entry
        {
            public string Word { get; set; }

            public int SearchContentId { get; set; }

            public int Count { get; set; }

            public int SiteId { get; set; }

            public string EntityName { get; set; }

            public DateTime ContentModifiedOn { get; set; }
        }

        public SearchContentWordIndex()
        {
            Map = words => from word in words
                           let vocabulary = LoadDocument<SearchWordAggregate>("SearchWordAggregate/" + word.SearchWordId)
                           let content = LoadDocument<SearchContentAggregate>("SearchContentAggregate/" + word.SearchContentId)
                           select new Entry
                           {
                               Word = vocabulary.Word,
                               SearchContentId = word.SearchContentId,
                               Count = word.Count,
                               SiteId = content.SiteId,
                               EntityName = content.EntityName,
                               ContentModifiedOn = content.ContentModifiedOn
                           };

            // The facet reads Count off the index entry, and the second query keys on SearchContentId.
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary>
    /// The RavenDB <see cref="ISearchQueryStore"/> — <b>the same numbers as the EF half, by a
    /// deliberately different mechanism.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism is a QUERY-TIME FACET.</b> <c>AggregateBy(f =&gt; f.ByField(SearchContentId)
    /// .SumOn(Count))</c> aggregates over the MATCHED RESULT SET, so the sum is taken over exactly the words
    /// the caller's keywords selected — which is what today's <c>GROUP BY ... SUM(...)</c> does, and what
    /// RavenDB's LINQ <c>GroupBy</c> cannot express (it rejects any filter arm outside the group-by key).
    /// Measured against the harvested query's own numbers: content 1 = 5, content 2 = 5, content 3 = 1, with
    /// another site's row excluded by the server.
    /// </para>
    /// <para>
    /// <b>Why NOT a map-reduce index</b> — the obvious RavenDB answer, and the wrong one. Map-reduce
    /// aggregates at INDEX time under a reduce key fixed in the index definition, so it cannot sum over a
    /// subset chosen at runtime. Reduce by <c>(SearchContentId, Word)</c> and you have re-derived
    /// <c>SearchContentWord</c>; reduce by <c>SearchContentId</c> and you get the total frequency of every word
    /// in the document, which is the wrong number. Facets are the only RavenDB aggregation that runs at query
    /// time.
    /// </para>
    /// <para>
    /// <b>Cost, named.</b> TWO round trips where EF issues one: the facet returns
    /// <c>SearchContentId -&gt; Sum</c> with no documents, so the matched <see cref="SearchContentAggregate"/>s
    /// are loaded in a second query. Bounded by the result set — not N+1, and not a cliff: the facet itself is
    /// one request, and the load is one request for all matched ids. It is the same bound the EF half has,
    /// because the harvested query has no <c>Take</c> either (<c>SearchService</c> pages in memory).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>It takes the raw <see cref="IDocumentStore"/>, and that is forced rather than chosen.</b> Every other
    /// read on this seam goes through <c>IQuerySession</c>, which hands back a bare
    /// <see cref="IQueryable{T}"/> so the caller cannot know the provider. <c>AggregateBy</c> is a RavenDB
    /// extension over <c>IRavenQueryable</c> and does not exist on <see cref="IQueryable{T}"/> — the seam's
    /// read surface cannot express this query, which is the entire reason <see cref="ISearchQueryStore"/> is a
    /// contract of its own. This type is INTERNAL to the RavenDB registration, so the coupling stops here:
    /// nothing outside it names RavenDB.
    /// </para>
    /// <para>
    /// The database comes from <c>RavenFrameworkStoreOptions.FrameworkDatabase</c> — the same source
    /// <c>RavenFrameworkStoreSessionFactory</c> uses for <c>StoreNames.Main</c>, so the facet and the document
    /// load always address one database. Resolving it any other way is how the two halves silently diverge.
    /// </para>
    /// </remarks>
    internal sealed class RavenSearchQueryStore(
        IFrameworkStoreSessionFactory sessions,
        IDocumentStore store,
        RavenFrameworkStoreOptions options,
        ITenantContext tenantContext,
        IRavenTenantDatabaseResolver databaseResolver) : ISearchQueryStore
    {
        public async Task<IReadOnlyList<SearchContent>> SearchAsync(SearchQuery searchQuery, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(searchQuery);

            var keywords = SearchUtils.GetKeywords(searchQuery.Keywords);
            if (keywords.Count == 0)
            {
                // FilterByItems composes `false` for an empty keyword set, which the relational half turns into
                // WHERE 1=0. Matched here rather than issued, so the two halves agree without a round trip.
                return [];
            }

            // The tenant's database - the one RavenFrameworkStoreSessionFactory reads, so the facet and the load agree.
            var database = RavenTenantRouting.DatabaseOf(databaseResolver, tenantContext, options);
            using var session = string.IsNullOrEmpty(database)
                ? store.OpenSession()
                : store.OpenSession(database);

            var query = session
                .Query<SearchContentWordIndex.Entry, SearchContentWordIndex>()
                .Customize(customization => customization.WaitForNonStaleResults())
                .Where(entry => entry.SiteId == searchQuery.SiteId);

            // The FilterByItems OR-chain, in RavenDB's own vocabulary. Composed as an expression so every arm
            // reaches the server: prefix matching is what this cluster's whole read is.
            query = query.Where(BuildKeywordPredicate(keywords));

            query = ApplyEntityAndDateArms(query, searchQuery);

            // THE AGGREGATION, IN THE STORE. See the remarks: this is what map-reduce cannot do.
            var facets = query
                .AggregateBy(builder => builder.ByField(entry => entry.SearchContentId).SumOn(entry => entry.Count))
                .Execute();

            var sums = facets.Values
                .SelectMany(facet => facet.Values)
                .Where(value => value.Sum.HasValue)
                .ToDictionary(
                    value => int.Parse(value.Range, CultureInfo.InvariantCulture),
                    value => (int)value.Sum.Value);

            if (sums.Count == 0)
            {
                return [];
            }

            // The second round trip: the facet yields ids and sums, not documents.
            await using var readSession = sessions.OpenQuerySession();
            var contents = new List<SearchContent>(sums.Count);

            foreach (var (searchContentId, count) in sums)
            {
                var aggregate = await readSession
                    .LoadAsync<SearchContentAggregate>(searchContentId, ct)
                    .ConfigureAwait(false);

                if (aggregate is null)
                {
                    continue;
                }

                var model = SearchContentMapper.ToModel(aggregate);
                model.Count = count;
                contents.Add(model);
            }

            await SearchQueryComposition
                .ApplyPropertyFilterAsync(contents, searchQuery, readSession, ct)
                .ConfigureAwait(false);

            return contents;
        }

        /// <summary>
        /// The OR-chain of prefix arms — <c>FilterByItems(keywords, (item, keyword) =&gt;
        /// item.Word.StartsWith(keyword), true)</c>, built directly because this half composes against
        /// RavenDB's own provider rather than the seam's <see cref="IQueryable{T}"/>.
        /// </summary>
        private static System.Linq.Expressions.Expression<Func<SearchContentWordIndex.Entry, bool>> BuildKeywordPredicate(
            IReadOnlyList<string> keywords)
        {
            var parameter = System.Linq.Expressions.Expression.Parameter(typeof(SearchContentWordIndex.Entry), "entry");
            var word = System.Linq.Expressions.Expression.Property(parameter, nameof(SearchContentWordIndex.Entry.Word));
            var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)]);

            System.Linq.Expressions.Expression predicate = null;
            foreach (var keyword in keywords)
            {
                System.Linq.Expressions.Expression arm = System.Linq.Expressions.Expression.Call(
                    word, startsWith!, System.Linq.Expressions.Expression.Constant(keyword));

                predicate = predicate is null
                    ? arm
                    : System.Linq.Expressions.Expression.OrElse(predicate, arm);
            }

            return System.Linq.Expressions.Expression.Lambda<Func<SearchContentWordIndex.Entry, bool>>(predicate!, parameter);
        }

        /// <summary>
        /// The include/exclude and date arms. They filter columns the index carries across from
        /// <see cref="SearchContentAggregate"/>, so they stay in the store — where the relational half puts
        /// them in <c>HAVING</c>.
        /// </summary>
        private static IRavenQueryable<SearchContentWordIndex.Entry> ApplyEntityAndDateArms(
            IRavenQueryable<SearchContentWordIndex.Entry> query,
            SearchQuery searchQuery)
        {
            if (!string.IsNullOrEmpty(searchQuery.IncludeEntities))
            {
                var include = searchQuery.IncludeEntities.Split(',', StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(entry => entry.EntityName.In(include));
            }

            if (!string.IsNullOrEmpty(searchQuery.ExcludeEntities))
            {
                var exclude = searchQuery.ExcludeEntities.Split(',', StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(entry => !entry.EntityName.In(exclude));
            }

            if (searchQuery.FromDate.Date != DateTime.MinValue.Date)
            {
                query = query.Where(entry => entry.ContentModifiedOn >= searchQuery.FromDate);
            }

            // ToDate defaults to DateTime.MinValue, so a query without one found NOTHING; the default means "no end".
            if (searchQuery.ToDate.Date != DateTime.MaxValue.Date && searchQuery.ToDate.Date != DateTime.MinValue.Date)
            {
                query = query.Where(entry => entry.ContentModifiedOn <= searchQuery.ToDate);
            }

            return query;
        }
    }
}
