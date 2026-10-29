using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Job aggregate root — the persistence-side model of the framework store's <c>Job</c> table,
    /// reachable only through <see cref="IJobStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads).
    /// It belongs to the master-bookkeeping cluster (Job and JobLog).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A MASTER-database aggregate, like the master half of Setting.</b> Job is installation-wide, not
    /// per-tenant: <see cref="MasterDBContext"/> mapped it and the master migration set creates the table.
    /// <see cref="IJobStore"/> is bound to <see cref="MasterDBContext"/> and its reads go through
    /// <see cref="IMasterStoreSessionFactory"/>, NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>. The
    /// job scheduler (<c>HostedServiceBase</c>) reads and writes it from a background scope with no request and
    /// no tenant, which the master session factory serves because it never reads <c>SiteState.Alias</c>.
    /// </para>
    /// <para>
    /// <b>Columns verified against the live migrated PostgreSQL database</b> (stomme-pg-44357, host 45432),
    /// not the EntityBuilder: the builder omits <see cref="MaximumDuration"/>, which <c>10020002</c> adds later
    /// (a NULLABLE integer column behind a non-nullable CLR property, harvested and reproduced rather than
    /// tidied). The audit quartet exists — <c>JobEntityBuilder</c> extends <c>AuditableBaseEntityBuilder</c> and
    /// calls <c>AddAuditableColumns</c> — so this aggregate IS <see cref="IAuditable"/> and
    /// <c>DbContextUtils</c> stamps it. Dropping the interface would compile and then FAIL every insert, because
    /// <c>created_by</c>/<c>modified_by</c> are NOT NULL.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class JobAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>Human-readable name for simple identification.</summary>
        public string Name { get; set; }

        /// <summary>
        /// The fully-qualified .NET type name of the job. This is the scheduler's lookup key
        /// (<c>GetJobAsync(jobType)</c>) and it is also fed to <c>Type.GetType</c>, so its CASE is load-bearing
        /// and must never be normalized — see <see cref="JobRepository"/> for the provider-equality note.
        /// </summary>
        public string JobType { get; set; }

        /// <summary>The frequency unit: a single character ('m' minutes, 'H' hours, 'd' days, 'w' weeks, 'M' months, 'O' one-time).</summary>
        public string Frequency { get; set; }

        /// <summary>How often the job runs, in <see cref="Frequency"/> units.</summary>
        public int Interval { get; set; }

        /// <summary>When the job first runs. Nullable.</summary>
        public DateTime? StartDate { get; set; }

        /// <summary>When the job last runs. Nullable.</summary>
        public DateTime? EndDate { get; set; }

        /// <summary>Whether the job is enabled.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>Whether the job has ever started running.</summary>
        public bool IsStarted { get; set; }

        /// <summary>Whether the job is executing right now — the concurrency guard the scheduler flips.</summary>
        public bool IsExecuting { get; set; }

        /// <summary>When the job will next run. Nullable.</summary>
        public DateTime? NextExecution { get; set; }

        /// <summary>The number of log entries to retain for this job.</summary>
        public int RetentionHistory { get; set; }

        /// <summary>
        /// The maximum minutes the job is expected to run before the scheduler assumes it terminated
        /// unexpectedly and resets it. A NULLABLE column behind this non-nullable property (a later migration);
        /// the application always sets it, and <c>10020002</c> back-filled every existing row to 30.
        /// </summary>
        public int MaximumDuration { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Job"/> for an
        /// update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int jobId) => Id = jobId;
    }
}
