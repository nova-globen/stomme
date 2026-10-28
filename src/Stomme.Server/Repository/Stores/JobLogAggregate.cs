using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The JobLog aggregate root — the persistence-side model of the framework store's <c>JobLog</c> table
    /// (D52), reachable only through <see cref="IJobLogStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). A MASTER-database aggregate, like <see cref="JobAggregate"/>: bound to <see cref="MasterDBContext"/>
    /// and read through <see cref="IMasterStoreSessionFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NOT <see cref="IAuditable"/>, and it must stay that way.</b> <c>JobLogEntityBuilder</c> extends
    /// <c>BaseEntityBuilder</c>, not the auditable one, and never calls <c>AddAuditableColumns</c> — the live
    /// PostgreSQL <c>job_log</c> table has 8 columns and NONE of the audit quartet (VERIFIED on stomme-pg-44357,
    /// host 45432). <c>DbContextUtils</c> decides by RUNTIME TYPE TEST, so adding <see cref="IAuditable"/> here
    /// would compile and then try to stamp four columns that do not exist — every job-log write would fail. A
    /// JobLog is append-then-finalize: the scheduler inserts it when a run starts and updates it once when the
    /// run finishes; there is no audit trail on it.
    /// </para>
    /// <para>
    /// <b>NOTE WHAT IS ABSENT: <c>Job</c>.</b> The contract's <c>JobLog.Job</c> was a MAPPED navigation the
    /// harvested repository eager-loaded with <c>.Include(item =&gt; item.Job)</c>. This aggregate references the
    /// job BY ID only (<see cref="JobId"/>, D7/D19). The <c>job_id</c> column still carries its cascading foreign
    /// key to <c>job</c> in the database; what is gone is the EF NAVIGATION, not the constraint.
    /// <see cref="JobLogRepository"/> re-populates the (now <c>[NotMapped]</c>) <c>JobLog.Job</c> member from a
    /// second master-session query — the Admin &gt; Jobs &gt; Log screen dereferences <c>log.Job.Name</c> and
    /// <c>log.Job.IsExecuting</c>, so severing the navigation was not free.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class JobLogAggregate : AggregateRoot<int>
    {
        /// <summary>
        /// The <see cref="Job"/> this entry logs, by id (D7/D19) — the reference that replaces the severed
        /// navigation. The <c>job_id</c> column keeps its cascading foreign key to <c>job</c>.
        /// </summary>
        public int JobId { get; set; }

        /// <summary>When the run started. NOT NULL.</summary>
        public DateTime StartDate { get; set; }

        /// <summary>When the run finished. Nullable — null while the run is in progress.</summary>
        public DateTime? FinishDate { get; set; }

        /// <summary>Whether the run succeeded. Nullable.</summary>
        public bool? Succeeded { get; set; }

        /// <summary>Any log text produced while the run executed. Nullable.</summary>
        public string Notes { get; set; }

        /// <summary>Machine name of the server that executed the run. Nullable (a later column).</summary>
        public string Server { get; set; }

        /// <summary>The scale-out instance id (WEBSITE_INSTANCE_ID), or blank. Nullable (a later column).</summary>
        public string Instance { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="JobLog"/> for
        /// an update or a delete. The store never calls this for an insert — the database assigns the identity.
        /// </summary>
        internal void AssignId(int jobLogId) => Id = jobLogId;
    }
}
