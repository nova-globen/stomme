using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="JobAggregate"/> (the persisted aggregate) and <see cref="Job"/> (the
    /// client/server wire contract) at the <see cref="JobRepository"/> boundary (D52). <see cref="Job"/> has no
    /// <c>[NotMapped]</c> view state — every public property is a column — so this mapping is total.
    /// </summary>
    public static class JobMapper
    {
        public static Job ToModel(JobAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Job
            {
                JobId = aggregate.Id,
                Name = aggregate.Name,
                JobType = aggregate.JobType,
                Frequency = aggregate.Frequency,
                Interval = aggregate.Interval,
                StartDate = aggregate.StartDate,
                EndDate = aggregate.EndDate,
                IsEnabled = aggregate.IsEnabled,
                IsStarted = aggregate.IsStarted,
                IsExecuting = aggregate.IsExecuting,
                NextExecution = aggregate.NextExecution,
                RetentionHistory = aggregate.RetentionHistory,
                MaximumDuration = aggregate.MaximumDuration,
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
        public static JobAggregate ToAggregate(Job job, bool assignId)
        {
            var aggregate = new JobAggregate
            {
                Name = job.Name,
                JobType = job.JobType,
                Frequency = job.Frequency,
                Interval = job.Interval,
                StartDate = job.StartDate,
                EndDate = job.EndDate,
                IsEnabled = job.IsEnabled,
                IsStarted = job.IsStarted,
                IsExecuting = job.IsExecuting,
                NextExecution = job.NextExecution,
                RetentionHistory = job.RetentionHistory,
                MaximumDuration = job.MaximumDuration,
                CreatedBy = job.CreatedBy,
                CreatedOn = job.CreatedOn,
                ModifiedBy = job.ModifiedBy,
                ModifiedOn = job.ModifiedOn
            };

            if (assignId)
            {
                aggregate.AssignId(job.JobId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write (the generated identity and the audit stamps
        /// applied by <see cref="DbContextUtils"/>), so the caller's <see cref="Job"/> reflects the persisted
        /// row. <c>HostedServiceBase</c> relies on this: it reads <c>job.JobId</c> straight after
        /// <c>AddJobAsync</c> to attach the new job's log entries.
        /// </summary>
        public static void CopyPersistedValues(JobAggregate aggregate, Job job)
        {
            job.JobId = aggregate.Id;
            job.CreatedBy = aggregate.CreatedBy;
            job.CreatedOn = aggregate.CreatedOn;
            job.ModifiedBy = aggregate.ModifiedBy;
            job.ModifiedOn = aggregate.ModifiedOn;
        }
    }
}
