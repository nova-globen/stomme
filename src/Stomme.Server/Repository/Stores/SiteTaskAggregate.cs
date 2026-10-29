using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The SiteTask aggregate root — the persistence-side model of the framework store's <c>SiteTask</c>
    /// table, reachable only through <see cref="ISiteTaskStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SiteTask is a queued unit of work for one <see cref="Site"/>, executed by <c>SiteTaskJob</c> and
    /// purged once complete. A leaf: <see cref="SiteId"/> is a reference BY ID and nothing navigates
    /// in.
    /// </para>
    /// <para>
    /// <see cref="Type"/> is a fully-qualified CLR type name that <c>SiteTaskJob</c> reflects over. That makes
    /// this table a code-execution surface, which is worth knowing but is unchanged by this increment — the
    /// same string went through the same reflection before it moved onto the seam.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class SiteTaskAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The <see cref="Site"/> the task runs for, by id.</summary>
        public int SiteId { get; set; }

        /// <summary>A human-readable name for the task.</summary>
        public string Name { get; set; }

        /// <summary>The fully-qualified type name <c>SiteTaskJob</c> instantiates. See the remarks.</summary>
        public string Type { get; set; }

        /// <summary>The task's parameters, opaque to this cluster. Nullable.</summary>
        public string Parameters { get; set; }

        /// <summary>
        /// Whether the task has run to completion.
        /// <para>
        /// NON-NULLABLE against a NULLABLE <c>is_completed</c> column — harvested and reproduced rather than
        /// tidied, exactly as <see cref="SettingAggregate.IsPrivate"/> is. Every insert writes an explicit
        /// false, so no NULL is ever produced for the non-nullable mapping to choke on.
        /// </para>
        /// <para>
        /// It is also the discriminator of BOTH of this cluster's queries, in opposite directions:
        /// <c>GetSiteTasks</c> returns only <c>!IsCompleted</c> (the work queue) and
        /// <c>DeleteSiteTasks</c> purges only <c>IsCompleted</c> (the finished work). Invert it and the job
        /// re-runs completed tasks while the purge deletes pending ones.
        /// </para>
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>Whatever status text the task reported. Nullable.</summary>
        public string Status { get; set; }

        public string CreatedBy { get; set; }

        /// <summary>
        /// When the task was queued. Stamped by <see cref="DbContextUtils"/> on insert like every other
        /// <see cref="IAuditable"/>, and ALSO this cluster's sort key (<c>GetSiteTasks</c> orders by it) and
        /// retention key (<c>DeleteSiteTasks</c> purges by it). That dual role is why an aggregate that failed
        /// to stamp it would not merely lose an audit trail — it would scramble the queue's order and make
        /// every completed task look infinitely old to the purge.
        /// </summary>
        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="SiteTask"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int siteTaskId) => Id = siteTaskId;
    }
}
