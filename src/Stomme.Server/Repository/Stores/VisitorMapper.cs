using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="VisitorAggregate"/> (the persisted aggregate) and <see cref="Visitor"/>
    /// (the client/server wire contract) at the <see cref="VisitorRepository"/> boundary. Only the columns the
    /// live schema actually has are mapped.
    /// </summary>
    /// <remarks>
    /// <see cref="Visitor.User"/> is deliberately absent: it is <c>[NotMapped]</c> view state that D22 severed
    /// from an EF navigation to an id, and <see cref="VisitorRepository.GetVisitorsAsync"/> re-populates it
    /// from the User aggregate after this mapper has run. Round-tripping a Visitor through here LOSES User by
    /// design — <see cref="Visitor.UserId"/> is the persisted reference. Pinned by
    /// <c>The_mapper_does_not_round_trip_the_unmapped_user</c>.
    /// </remarks>
    public static class VisitorMapper
    {
        public static Visitor ToModel(VisitorAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Visitor
            {
                VisitorId = aggregate.Id,
                SiteId = aggregate.SiteId,
                UserId = aggregate.UserId,
                Visits = aggregate.Visits,
                IPAddress = aggregate.IPAddress,
                UserAgent = aggregate.UserAgent,
                Language = aggregate.Language,
                Url = aggregate.Url,
                Referrer = aggregate.Referrer,
                CreatedOn = aggregate.CreatedOn,
                VisitedOn = aggregate.VisitedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static VisitorAggregate ToAggregate(Visitor visitor, bool assignId)
        {
            var aggregate = new VisitorAggregate
            {
                SiteId = visitor.SiteId,
                UserId = visitor.UserId,
                Visits = visitor.Visits,
                IPAddress = visitor.IPAddress,
                UserAgent = visitor.UserAgent,
                Language = visitor.Language,
                Url = visitor.Url,
                Referrer = visitor.Referrer,
                CreatedOn = visitor.CreatedOn,
                VisitedOn = visitor.VisitedOn
            };

            if (assignId)
            {
                aggregate.AssignId(visitor.VisitorId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write — for this cluster the generated identity
        /// and nothing else, since Visitor carries no audit stamps (see <see cref="VisitorAggregate"/>).
        /// </summary>
        /// <remarks>
        /// <b>The identity copy-back is load-bearing on the RENDER PATH.</b> <c>App.razor</c>'s
        /// <c>TrackVisitorAsync</c> does <c>visitor = await VisitorRepository.AddVisitorAsync(visitor);</c> and
        /// then reads <c>visitor.VisitorId</c> straight into the visitor cookie it writes to the response. Lose
        /// this and every visitor gets a cookie of id 0, is never correlated again, and the Visitor table grows
        /// a new row per request. Pinned by <c>Adding_a_visitor_returns_the_database_assigned_id</c>.
        /// </remarks>
        public static void CopyPersistedValues(VisitorAggregate aggregate, Visitor visitor)
        {
            visitor.VisitorId = aggregate.Id;
        }
    }
}
