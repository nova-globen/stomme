using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SearchContent aggregate root — the persistence-side model of the framework store's
    /// <c>SearchContent</c> table. Reachable only through <see cref="ISearchContentStore"/> (writes),
    /// <see cref="ISearchQueryStore"/> (the search read) and <c>IQuerySession.Query</c> (other reads).
    /// See <c>src/Stomme.Server/README.md (persistence seam)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One indexed document: a page, a file, or anything else implementing <c>ISearchable</c>. It is the head
    /// of a four-table cluster — <c>SearchContentProperty</c> hangs off it, and <c>SearchContentWord</c> /
    /// <c>SearchWord</c> form the hand-rolled inverted index that ranks it. Tenant-side only, like the leaf
    /// clusters and unlike Setting, so one store and no <c>IsMaster</c> routing.
    /// </para>
    /// <para>
    /// <b>NEITHER <see cref="IAuditable"/> NOR <see cref="IDeletable"/>, and that is MEASURED, not inherited.</b>
    /// <c>SearchContentEntityBuilder</c> extends <c>BaseEntityBuilder</c> — not
    /// <c>DeletableAuditableBaseEntityBuilder</c> — and the live PostgreSQL <c>search_content</c> has exactly
    /// THIRTEEN columns with zero dropped stubs in <c>pg_attribute</c>. This is the first cluster on the seam
    /// whose EntityBuilder tells the truth (contrast Language, Folder and File). It follows
    /// <c>LogAggregate</c>'s shape, not <c>FileAggregate</c>'s.
    /// </para>
    /// <para>
    /// <b>Do not "restore consistency" by adding the audit quartet.</b> <c>created_on</c> exists below and is a
    /// PLAIN column the application sets — <c>created_by</c>, <c>modified_by</c> and <c>modified_on</c> do NOT
    /// exist. <c>DbContextUtils</c> decides <see cref="IAuditable"/> by RUNTIME TYPE TEST against the tracked
    /// entity, so declaring it here would compile, register, render, and then write three columns that are not
    /// there — failing every search-index update, i.e. every content save. There is no compiler error
    /// available. Mutation-checked.
    /// </para>
    /// <para>
    /// <b>NOTE WHAT IS ABSENT.</b> The wire contract's <c>Count</c>, <c>IsDeleted</c>, <c>TenantId</c> and
    /// <c>UniqueKey</c> are all <c>[NotMapped]</c> view state with no column. <c>Count</c> is the load-bearing
    /// one: it is the per-content term frequency the SEARCH READ computes at query time
    /// (<c>Sum(SearchContentWord.Count)</c> over the MATCHED words), never a stored value — which is precisely
    /// why the read needs <see cref="ISearchQueryStore"/> rather than a session query. It is not on this
    /// aggregate. <c>SearchContentProperties</c> is likewise absent: it is a separate aggregate reached through
    /// <see cref="ISearchContentPropertyStore"/>, because a mapped navigation into it would resurrect the
    /// contract and break the model.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class SearchContentAggregate : AggregateRoot<int>
    {
        /// <summary>The <see cref="Site"/> this indexed content belongs to. A reference BY ID.</summary>
        public int SiteId { get; set; }

        /// <summary>The entity type indexed ("Page", "File", ...). Half of the natural key.</summary>
        public string EntityName { get; set; }

        /// <summary>
        /// The indexed entity's id, as a STRING — harvested and reproduced. <c>ix_search_content</c> is UNIQUE
        /// on <c>(site_id, entity_name, entity_id)</c>.
        /// </summary>
        public string EntityId { get; set; }

        public string Title { get; set; }

        public string Description { get; set; }

        public string Body { get; set; }

        /// <summary>The site-relative URL. <c>SearchService</c> groups its results by this.</summary>
        public string Url { get; set; }

        /// <summary>Comma-delimited <c>EntityName:EntityId</c> pairs, or role names — security-trimmed per request.</summary>
        public string Permissions { get; set; }

        public string ContentModifiedBy { get; set; }

        public DateTime ContentModifiedOn { get; set; }

        /// <summary>Extra text folded into the word index but never displayed.</summary>
        public string AdditionalContent { get; set; }

        /// <summary>
        /// A PLAIN column, not an audit stamp — see the remarks on this type. Nothing stamps it; the
        /// application sets it.
        /// </summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="SearchContent"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int searchContentId) => Id = searchContentId;
    }
}
