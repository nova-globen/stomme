using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="UrlMappingAggregate"/> (the persisted aggregate) and
    /// <see cref="UrlMapping"/> (the client/server wire contract) at the <see cref="UrlMappingRepository"/>
    /// boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="UrlMapping"/> carries no <c>[NotMapped]</c> members at all — it is the plainest contract in
    /// the framework store, and this mapper is total in both directions.
    /// </remarks>
    public static class UrlMappingMapper
    {
        public static UrlMapping ToModel(UrlMappingAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new UrlMapping
            {
                UrlMappingId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Url = aggregate.Url,
                MappedUrl = aggregate.MappedUrl,
                Requests = aggregate.Requests,
                Referrer = aggregate.Referrer,
                CreatedOn = aggregate.CreatedOn,
                RequestedOn = aggregate.RequestedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static UrlMappingAggregate ToAggregate(UrlMapping urlMapping, bool assignId)
        {
            var aggregate = new UrlMappingAggregate
            {
                SiteId = urlMapping.SiteId,
                Url = urlMapping.Url,
                MappedUrl = urlMapping.MappedUrl,
                Requests = urlMapping.Requests,
                Referrer = urlMapping.Referrer,
                CreatedOn = urlMapping.CreatedOn,
                RequestedOn = urlMapping.RequestedOn
            };

            if (assignId)
            {
                aggregate.AssignId(urlMapping.UrlMappingId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write — the generated identity, and nothing else
        /// (UrlMapping carries no audit stamps). <c>UrlMappingController.Post</c> returns the mapping straight
        /// to the client and the admin UI keys the grid on the id.
        /// </summary>
        public static void CopyPersistedValues(UrlMappingAggregate aggregate, UrlMapping urlMapping)
        {
            urlMapping.UrlMappingId = aggregate.Id;
        }
    }
}
