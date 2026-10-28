using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between the four Search aggregates and their wire contracts at the
    /// <see cref="SearchContentRepository"/> boundary (D50). Only the columns the harvested schema actually
    /// has are mapped — verified against live PostgreSQL at 13/4/6/3 columns.
    /// </summary>
    /// <remarks>
    /// <see cref="SearchContent"/>'s <c>[NotMapped]</c> members are deliberately absent. <c>Count</c> is the
    /// load-bearing one: it is computed by the SEARCH READ (<see cref="ISearchQueryStore"/>) at query time and
    /// never stored, so round-tripping a SearchContent through this mapper LOSES it by design — which is
    /// correct everywhere except the search read, and the search read does not use this method to set it.
    /// <c>SearchContentProperties</c> is likewise absent: it is a separate aggregate, and the repository
    /// stitches it back on.
    /// </remarks>
    public static class SearchContentMapper
    {
        public static SearchContent ToModel(SearchContentAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SearchContent
            {
                SearchContentId = aggregate.Id,
                SiteId = aggregate.SiteId,
                EntityName = aggregate.EntityName,
                EntityId = aggregate.EntityId,
                Title = aggregate.Title,
                Description = aggregate.Description,
                Body = aggregate.Body,
                Url = aggregate.Url,
                Permissions = aggregate.Permissions,
                ContentModifiedBy = aggregate.ContentModifiedBy,
                ContentModifiedOn = aggregate.ContentModifiedOn,
                AdditionalContent = aggregate.AdditionalContent,
                CreatedOn = aggregate.CreatedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static SearchContentAggregate ToAggregate(SearchContent searchContent, bool assignId)
        {
            var aggregate = new SearchContentAggregate
            {
                SiteId = searchContent.SiteId,
                EntityName = searchContent.EntityName,
                EntityId = searchContent.EntityId,
                Title = searchContent.Title,
                Description = searchContent.Description,
                Body = searchContent.Body,
                Url = searchContent.Url,
                Permissions = searchContent.Permissions,
                ContentModifiedBy = searchContent.ContentModifiedBy,
                ContentModifiedOn = searchContent.ContentModifiedOn,
                AdditionalContent = searchContent.AdditionalContent,
                CreatedOn = searchContent.CreatedOn
            };

            if (assignId)
            {
                aggregate.AssignId(searchContent.SearchContentId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the identity the store assigned during a write, so the caller's
        /// <see cref="SearchContent"/> reflects the persisted row. <c>DatabaseSearchProvider.SaveSearchContent</c>
        /// relies on this: it reads <c>searchContent.SearchContentId</c> immediately afterwards to key every
        /// <c>SearchContentWord</c> it then writes. There are no audit stamps to copy back — this cluster has
        /// none (see <see cref="SearchContentAggregate"/>).
        /// </summary>
        public static void CopyPersistedValues(SearchContentAggregate aggregate, SearchContent searchContent)
        {
            searchContent.SearchContentId = aggregate.Id;
        }
    }

    /// <summary>Translates between <see cref="SearchWordAggregate"/> and <see cref="SearchWord"/> (D50).</summary>
    public static class SearchWordMapper
    {
        public static SearchWord ToModel(SearchWordAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SearchWord
            {
                SearchWordId = aggregate.Id,
                Word = aggregate.Word,
                CreatedOn = aggregate.CreatedOn
            };
        }

        public static SearchWordAggregate ToAggregate(SearchWord searchWord, bool assignId)
        {
            var aggregate = new SearchWordAggregate
            {
                Word = searchWord.Word,
                CreatedOn = searchWord.CreatedOn
            };

            if (assignId)
            {
                aggregate.AssignId(searchWord.SearchWordId);
            }

            return aggregate;
        }

        /// <summary>
        /// <c>DatabaseSearchProvider.AnalyzeSearchContentAsync</c> reads <c>searchWord.SearchWordId</c> straight
        /// after the add, to key the <c>SearchContentWord</c> it then writes.
        /// </summary>
        public static void CopyPersistedValues(SearchWordAggregate aggregate, SearchWord searchWord)
        {
            searchWord.SearchWordId = aggregate.Id;
        }
    }

    /// <summary>
    /// Translates between <see cref="SearchContentWordAggregate"/> and <see cref="SearchContentWord"/> (D50).
    /// </summary>
    /// <remarks>
    /// The contract's <c>SearchWord</c> and <c>SearchContent</c> navigations are <c>[NotMapped]</c> view state
    /// (D45) and are NOT round-tripped here. <c>SearchContentRepository.GetSearchContentWordsAsync</c>
    /// populates the <c>SearchWord</c> member by hand from a second query, because
    /// <c>DatabaseSearchProvider</c> reads <c>existing.SearchWord?.Word</c> to decide insert-vs-update.
    /// </remarks>
    public static class SearchContentWordMapper
    {
        public static SearchContentWord ToModel(SearchContentWordAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SearchContentWord
            {
                SearchContentWordId = aggregate.Id,
                SearchContentId = aggregate.SearchContentId,
                SearchWordId = aggregate.SearchWordId,
                Count = aggregate.Count,
                CreatedOn = aggregate.CreatedOn,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        public static SearchContentWordAggregate ToAggregate(SearchContentWord searchContentWord, bool assignId)
        {
            var aggregate = new SearchContentWordAggregate
            {
                SearchContentId = searchContentWord.SearchContentId,
                SearchWordId = searchContentWord.SearchWordId,
                Count = searchContentWord.Count,
                CreatedOn = searchContentWord.CreatedOn,
                ModifiedOn = searchContentWord.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(searchContentWord.SearchContentWordId);
            }

            return aggregate;
        }

        public static void CopyPersistedValues(SearchContentWordAggregate aggregate, SearchContentWord searchContentWord)
        {
            searchContentWord.SearchContentWordId = aggregate.Id;
        }
    }

    /// <summary>
    /// Translates between <see cref="SearchContentPropertyAggregate"/> and <see cref="SearchContentProperty"/>
    /// (D50). NOTE the contract's key is <c>PropertyId</c>, not <c>SearchContentPropertyId</c>.
    /// </summary>
    public static class SearchContentPropertyMapper
    {
        public static SearchContentProperty ToModel(SearchContentPropertyAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SearchContentProperty
            {
                PropertyId = aggregate.Id,
                SearchContentId = aggregate.SearchContentId,
                Name = aggregate.Name,
                Value = aggregate.Value
            };
        }

        public static SearchContentPropertyAggregate ToAggregate(SearchContentProperty property, bool assignId)
        {
            var aggregate = new SearchContentPropertyAggregate
            {
                SearchContentId = property.SearchContentId,
                Name = property.Name,
                Value = property.Value
            };

            if (assignId)
            {
                aggregate.AssignId(property.PropertyId);
            }

            return aggregate;
        }

        public static void CopyPersistedValues(SearchContentPropertyAggregate aggregate, SearchContentProperty property)
        {
            property.PropertyId = aggregate.Id;
        }
    }
}
