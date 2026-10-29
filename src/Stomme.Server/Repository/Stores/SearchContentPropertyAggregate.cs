using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SearchContentProperty aggregate root — the persistence-side model of the framework store's
    /// <c>SearchContentProperty</c> table. Arbitrary name/value metadata hung off one
    /// <see cref="SearchContentAggregate"/>. Reachable only through <see cref="ISearchContentPropertyStore"/>
    /// (writes) and <c>IQuerySession.Query</c> (reads).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It BELONGS to this cluster, which was checked rather than assumed.</b> It is a mapped navigation on
    /// the wire contract (<c>SearchContent.SearchContentProperties</c>), it is written by
    /// <c>SearchContentRepository.AddSearchContentAsync</c>, and <c>ix_search_content_property</c> is UNIQUE on
    /// <c>(search_content_id, name)</c> with an FK cascade from <c>search_content</c>. It is not separable.
    /// </para>
    /// <para>
    /// <b>The navigation is severed at the contract.</b> <c>SearchContent.SearchContentProperties</c> is
    /// <c>[NotMapped]</c>: leaving it mapped would let EF rediscover the <c>SearchContentProperty</c> contract
    /// through it and claim this aggregate's table. The member is view state the repository populates.
    /// </para>
    /// <para>
    /// Neither <see cref="IAuditable"/> nor <see cref="IDeletable"/>, measured:
    /// <c>SearchContentPropertyEntityBuilder</c> extends <c>BaseEntityBuilder</c> and live
    /// <c>search_content_property</c> has exactly FOUR columns — <c>property_id</c>, <c>search_content_id</c>,
    /// <c>name</c>, <c>value</c> — with zero dropped stubs. It has no timestamp of any kind.
    /// </para>
    /// <para>
    /// <b>The read path that consumes this is BROKEN TODAY and is fixed, not ported.</b>
    /// <c>SearchQuery.Properties</c> drove two arms that threw on EF Core: <c>.Include()</c> after a
    /// <c>GroupBy().Select()</c> projection, and <c>.Any()</c> on the projected entity. Nothing in-tree
    /// populates <c>Properties</c>, but <c>SearchResultsController.Post</c> binds it <c>[FromBody]</c>, so a
    /// client that posted one got a 500. See <see cref="ISearchQueryStore"/>.
    /// </para>
    /// </remarks>
    public class SearchContentPropertyAggregate : AggregateRoot<int>
    {
        /// <summary>The content this property describes. A reference to another aggregate BY ID.</summary>
        public int SearchContentId { get; set; }

        public string Name { get; set; }

        public string Value { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity for an update or a delete. NOTE the wire
        /// contract calls this key <c>PropertyId</c>, not <c>SearchContentPropertyId</c> — harvested, not tidied.
        /// </summary>
        internal void AssignId(int propertyId) => Id = propertyId;
    }
}
