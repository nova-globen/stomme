using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ISearchContentRepository
    {
        Task<IEnumerable<SearchContent>> GetSearchContentsAsync(SearchQuery searchQuery);
        Task<SearchContent> AddSearchContentAsync(SearchContent searchContent);
        Task DeleteSearchContentAsync(int searchContentId);
        Task DeleteSearchContentAsync(string entityName, string entryId);
        Task DeleteSearchContentAsync(string uniqueKey);
        Task DeleteAllSearchContentAsync(int siteId);

        Task<SearchWord> GetSearchWordAsync(string word);
        Task<SearchWord> AddSearchWordAsync(SearchWord searchWord);

        Task<IEnumerable<SearchContentWord>> GetSearchContentWordsAsync(int searchContentId);
        Task<SearchContentWord> AddSearchContentWordAsync(SearchContentWord searchContentWord);
        Task<SearchContentWord> UpdateSearchContentWordAsync(SearchContentWord searchContentWord);
    }

    /// <summary>
    /// The SearchContent cluster on the persistence seam — Phase 3's last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four stores for the writes, <see cref="IFrameworkStoreSessionFactory"/> for the ordinary reads,
    /// and <see cref="ISearchQueryStore"/> for the ONE read the seam's LINQ surface cannot express — the
    /// keyword search. See <see cref="ISearchQueryStore"/> for why that contract exists and why its two
    /// implementations are deliberately unalike.
    /// </para>
    /// <para>
    /// <b>Stores are constructor-injected, not resolved per write (not D47).</b> Nothing here reads a key
    /// and then writes it twice through one instance: <c>DatabaseSearchProvider.SaveSearchContent</c> deletes
    /// by (entityName, entityId) and then ADDs a fresh row, and <c>AnalyzeSearchContentAsync</c>'s per-word
    /// loop either adds or updates a given key exactly once. UrlMapping remains the sole cluster needing the
    /// per-write workaround, because it is the sole cluster whose READ writes. Verified by mutation check
    /// rather than reasoned from the call graph, as D49 requires.
    /// </para>
    /// </remarks>
    public class SearchContentRepository : ISearchContentRepository
    {
        private readonly ISearchContentStore _searchContents;
        private readonly ISearchContentPropertyStore _properties;
        private readonly ISearchContentWordStore _contentWords;
        private readonly ISearchWordStore _words;
        private readonly ISearchQueryStore _searchQueries;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public SearchContentRepository(
            ISearchContentStore searchContents,
            ISearchContentPropertyStore properties,
            ISearchContentWordStore contentWords,
            ISearchWordStore words,
            ISearchQueryStore searchQueries,
            IFrameworkStoreSessionFactory sessions)
        {
            _searchContents = searchContents;
            _properties = properties;
            _contentWords = contentWords;
            _words = words;
            _searchQueries = searchQueries;
            _sessions = sessions;
        }

        /// <summary>
        /// The search read. Delegates wholesale to <see cref="ISearchQueryStore"/> — see its remarks: this is
        /// the one read on the seam whose implementation is provider-specific BY DECISION, because the
        /// portable spelling does not exist.
        /// </summary>
        public async Task<IEnumerable<SearchContent>> GetSearchContentsAsync(SearchQuery searchQuery)
            => await _searchQueries.SearchAsync(searchQuery).ConfigureAwait(false);

        public async Task<SearchContent> AddSearchContentAsync(SearchContent searchContent)
        {
            var aggregate = SearchContentMapper.ToAggregate(searchContent, assignId: false);
            await _searchContents.AddAsync(aggregate).ConfigureAwait(false);
            SearchContentMapper.CopyPersistedValues(aggregate, searchContent);

            if (searchContent.SearchContentProperties != null && searchContent.SearchContentProperties.Any())
            {
                foreach (var property in searchContent.SearchContentProperties)
                {
                    // The harvested code assigned this AFTER Add and relied on EF's fixup to have populated
                    // the identity; CopyPersistedValues above makes the same value available explicitly.
                    property.SearchContentId = searchContent.SearchContentId;
                    var propertyAggregate = SearchContentPropertyMapper.ToAggregate(property, assignId: false);
                    await _properties.AddAsync(propertyAggregate).ConfigureAwait(false);
                    SearchContentPropertyMapper.CopyPersistedValues(propertyAggregate, property);
                }
            }

            return searchContent;
        }

        public async Task DeleteSearchContentAsync(int searchContentId)
        {
            var aggregate = await _searchContents.GetByIdAsync(searchContentId).ConfigureAwait(false);
            if (aggregate != null)
            {
                await DeleteWithDependentsAsync(aggregate).ConfigureAwait(false);
            }
        }

        public async Task DeleteSearchContentAsync(string entityName, string entryId)
        {
            SearchContentAggregate aggregate;
            await using (var session = _sessions.OpenQuerySession())
            {
                aggregate = session.Query<SearchContentAggregate>()
                    .FirstOrDefault(item => item.EntityName == entityName && item.EntityId == entryId);
            }

            if (aggregate != null)
            {
                await DeleteWithDependentsAsync(aggregate).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Deletes by the composite "EntityName:EntityId" key.
        /// </summary>
        /// <remarks>
        /// The harvested query filtered <c>(i.EntityName + ":" + i.EntityId) == uniqueKey</c> — string
        /// CONCATENATION inside the predicate. The key is split here and the two columns compared instead:
        /// same rows, and it removes a translation the seam has no reason to require of every provider. NOTE
        /// this is not the key <see cref="SearchContent.UniqueKey"/> builds (that one carries TenantId and
        /// SiteId); the harvested predicate did not match that either, and reproducing the mismatch is
        /// deliberate — no caller in-tree uses this overload.
        /// </remarks>
        public async Task DeleteSearchContentAsync(string uniqueKey)
        {
            var separator = uniqueKey?.IndexOf(':') ?? -1;
            if (separator < 0)
            {
                return;
            }

            var entityName = uniqueKey.Substring(0, separator);
            var entityId = uniqueKey.Substring(separator + 1);

            await DeleteSearchContentAsync(entityName, entityId).ConfigureAwait(false);
        }

        /// <summary>
        /// Purges a site's index.
        /// </summary>
        /// <remarks>
        /// The harvested code deleted in batches of 100 with <c>RemoveRange</c>. The seam has no batch delete
        /// (Phase 5 owns it), so this is one round trip per row — the cost D24/D44 already accepted for
        /// <c>DeletePermissions</c> and the three leaf purge loops. The batching loop goes with it: it existed
        /// to bound EF's change tracker, and a session read tracks nothing.
        /// </remarks>
        public async Task DeleteAllSearchContentAsync(int siteId)
        {
            List<SearchContentAggregate> aggregates;
            await using (var session = _sessions.OpenQuerySession())
            {
                aggregates = session.Query<SearchContentAggregate>()
                    .Where(item => item.SiteId == siteId)
                    .ToList();
            }

            foreach (var aggregate in aggregates)
            {
                await DeleteWithDependentsAsync(aggregate).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Deletes a SearchContent and the rows that used to follow it by FK CASCADE.
        /// </summary>
        /// <remarks>
        /// <b>This is a behaviour ADDITION, and it is deliberate.</b> <c>search_content_property</c> and
        /// <c>search_content_word</c> both declare <c>ReferentialAction.Cascade</c> onto <c>search_content</c>,
        /// so on the relational providers the database removes them and these extra deletes are
        /// redundant-but-harmless. RavenDB has no foreign keys and no cascade, so without this the word index
        /// and the properties would ORPHAN on every re-index — silently, and every re-index runs this path
        /// (<c>DatabaseSearchProvider.SaveSearchContent</c> deletes, then re-adds). Doing it in the repository
        /// keeps the two providers observably identical, which is the seam's whole contract.
        /// </remarks>
        private async Task DeleteWithDependentsAsync(SearchContentAggregate aggregate)
        {
            List<SearchContentPropertyAggregate> properties;
            List<SearchContentWordAggregate> contentWords;

            await using (var session = _sessions.OpenQuerySession())
            {
                properties = session.Query<SearchContentPropertyAggregate>()
                    .Where(item => item.SearchContentId == aggregate.Id)
                    .ToList();

                contentWords = session.Query<SearchContentWordAggregate>()
                    .Where(item => item.SearchContentId == aggregate.Id)
                    .ToList();
            }

            foreach (var property in properties)
            {
                await _properties.DeleteAsync(property).ConfigureAwait(false);
            }

            foreach (var contentWord in contentWords)
            {
                await _contentWords.DeleteAsync(contentWord).ConfigureAwait(false);
            }

            await _searchContents.DeleteAsync(aggregate).ConfigureAwait(false);
        }

        public async Task<SearchWord> GetSearchWordAsync(string word)
        {
            if (string.IsNullOrEmpty(word))
            {
                return null;
            }

            await using var session = _sessions.OpenQuerySession();
            var aggregate = session.Query<SearchWordAggregate>().FirstOrDefault(item => item.Word == word);

            return SearchWordMapper.ToModel(aggregate);
        }

        public async Task<SearchWord> AddSearchWordAsync(SearchWord searchWord)
        {
            var aggregate = SearchWordMapper.ToAggregate(searchWord, assignId: false);
            await _words.AddAsync(aggregate).ConfigureAwait(false);
            SearchWordMapper.CopyPersistedValues(aggregate, searchWord);

            return searchWord;
        }

        /// <summary>
        /// The word rows for one content, each with its <see cref="SearchContentWord.SearchWord"/> populated.
        /// </summary>
        /// <remarks>
        /// The harvested code got the word by <c>.Include(i =&gt; i.SearchWord)</c>. That navigation is
        /// <c>[NotMapped]</c> now (D45's mechanism — a mapped one resurrects the SearchWord contract and breaks
        /// the model), so the member is stitched on from a SECOND query over the referenced words, exactly as
        /// D45's <c>PopulateSiteGroups</c> does. It is LOAD-BEARING, not decoration:
        /// <c>DatabaseSearchProvider.AnalyzeSearchContentAsync</c> reads <c>existing.SearchWord?.Word</c> to key
        /// its insert-vs-update decision for every word of every indexed document, and a null member there
        /// silently re-inserts every word on every save. Behaviour-equivalent because the <c>.Include</c>
        /// carried no join predicate (D22 rule 9 does not apply; there was no join to lose). One query for the
        /// whole set, not N+1.
        /// </remarks>
        public async Task<IEnumerable<SearchContentWord>> GetSearchContentWordsAsync(int searchContentId)
        {
            await using var session = _sessions.OpenQuerySession();

            var aggregates = session.Query<SearchContentWordAggregate>()
                .Where(item => item.SearchContentId == searchContentId)
                .ToList();

            var models = aggregates.ConvertAll(SearchContentWordMapper.ToModel);
            if (models.Count == 0)
            {
                return models;
            }

            var wordIds = aggregates.ConvertAll(item => item.SearchWordId);
            var words = session.Query<SearchWordAggregate>()
                .Where(item => wordIds.Contains(item.Id))
                .ToList()
                .ToDictionary(item => item.Id, SearchWordMapper.ToModel);

            foreach (var model in models)
            {
                if (words.TryGetValue(model.SearchWordId, out var word))
                {
                    model.SearchWord = word;
                }
            }

            return models;
        }

        public async Task<SearchContentWord> AddSearchContentWordAsync(SearchContentWord searchContentWord)
        {
            var aggregate = SearchContentWordMapper.ToAggregate(searchContentWord, assignId: false);
            await _contentWords.AddAsync(aggregate).ConfigureAwait(false);
            SearchContentWordMapper.CopyPersistedValues(aggregate, searchContentWord);

            return searchContentWord;
        }

        public async Task<SearchContentWord> UpdateSearchContentWordAsync(SearchContentWord searchContentWord)
        {
            var aggregate = SearchContentWordMapper.ToAggregate(searchContentWord, assignId: true);
            await _contentWords.UpdateAsync(aggregate).ConfigureAwait(false);

            return searchContentWord;
        }
    }
}
