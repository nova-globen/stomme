using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="SiteTaskAggregate"/> (the persisted aggregate) and
    /// <see cref="SiteTask"/> (the client/server wire contract) at the <see cref="SiteTaskRepository"/>
    /// boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="SiteTask"/> has no <c>[NotMapped]</c> members: every public property is a column and every
    /// column is copied here.
    /// </remarks>
    public static class SiteTaskMapper
    {
        public static SiteTask ToModel(SiteTaskAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new SiteTask
            {
                SiteTaskId = aggregate.Id,
                SiteId = aggregate.SiteId,
                Name = aggregate.Name,
                Type = aggregate.Type,
                Parameters = aggregate.Parameters,
                IsCompleted = aggregate.IsCompleted,
                Status = aggregate.Status,
                CreatedBy = aggregate.CreatedBy,
                CreatedOn = aggregate.CreatedOn,
                ModifiedBy = aggregate.ModifiedBy,
                ModifiedOn = aggregate.ModifiedOn
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static SiteTaskAggregate ToAggregate(SiteTask siteTask, bool assignId)
        {
            var aggregate = new SiteTaskAggregate
            {
                SiteId = siteTask.SiteId,
                Name = siteTask.Name,
                Type = siteTask.Type,
                Parameters = siteTask.Parameters,
                IsCompleted = siteTask.IsCompleted,
                Status = siteTask.Status,
                CreatedBy = siteTask.CreatedBy,
                CreatedOn = siteTask.CreatedOn,
                ModifiedBy = siteTask.ModifiedBy,
                ModifiedOn = siteTask.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(siteTask.SiteTaskId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit
        /// stamps applied by <see cref="DbContextUtils"/>), so the caller's <see cref="SiteTask"/> reflects
        /// the persisted row. <c>SiteTaskController.Post</c> returns the task straight to the client, and
        /// <c>CreatedOn</c> is this cluster's sort key — see <see cref="SiteTaskAggregate.CreatedOn"/>.
        /// </summary>
        public static void CopyPersistedValues(SiteTaskAggregate aggregate, SiteTask siteTask)
        {
            siteTask.SiteTaskId = aggregate.Id;
            siteTask.CreatedBy = aggregate.CreatedBy;
            siteTask.CreatedOn = aggregate.CreatedOn;
            siteTask.ModifiedBy = aggregate.ModifiedBy;
            siteTask.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
