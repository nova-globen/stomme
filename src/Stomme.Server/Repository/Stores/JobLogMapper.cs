using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="JobLogAggregate"/> (the persisted aggregate) and <see cref="JobLog"/> (the
    /// client/server wire contract) at the <see cref="JobLogRepository"/> boundary (D52).
    /// </summary>
    /// <remarks>
    /// <see cref="JobLog.Job"/> is deliberately absent — it is <c>[NotMapped]</c> view state that
    /// <see cref="JobLogRepository"/> populates from a second master-session query, exactly as
    /// <see cref="SiteGroupMemberMapper"/> drops <c>SiteGroupMember.SiteGroup</c> (D45). Round-tripping a JobLog
    /// through this mapper therefore DROPS <c>Job</c> by design; the repository puts it back, the mapper does
    /// not. There are no audit stamps to copy — the JobLog table has none (see <see cref="JobLogAggregate"/>).
    /// </remarks>
    public static class JobLogMapper
    {
        public static JobLog ToModel(JobLogAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new JobLog
            {
                JobLogId = aggregate.Id,
                JobId = aggregate.JobId,
                StartDate = aggregate.StartDate,
                FinishDate = aggregate.FinishDate,
                Succeeded = aggregate.Succeeded,
                Notes = aggregate.Notes,
                Server = aggregate.Server,
                Instance = aggregate.Instance
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete. Any <c>Job</c> the caller carries
        /// is ignored — see the class remarks.
        /// </summary>
        public static JobLogAggregate ToAggregate(JobLog jobLog, bool assignId)
        {
            var aggregate = new JobLogAggregate
            {
                JobId = jobLog.JobId,
                StartDate = jobLog.StartDate,
                FinishDate = jobLog.FinishDate,
                Succeeded = jobLog.Succeeded,
                Notes = jobLog.Notes,
                Server = jobLog.Server,
                Instance = jobLog.Instance
            };

            if (assignId)
            {
                aggregate.AssignId(jobLog.JobLogId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the identity the database assigned. There are no audit stamps to copy — the JobLog table
        /// has no audit columns (see <see cref="JobLogAggregate"/>). <c>HostedServiceBase</c> relies on this: it
        /// updates the log's <c>FinishDate</c> straight after <c>AddJobLogAsync</c>, which needs the generated id.
        /// </summary>
        public static void CopyPersistedValues(JobLogAggregate aggregate, JobLog jobLog)
        {
            jobLog.JobLogId = aggregate.Id;
        }
    }
}
