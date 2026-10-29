using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Log aggregate root — the persistence-side model of the framework store's <c>Log</c> table,
    /// reachable only through <see cref="ILogStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE FIRST FRAMEWORK-STORE AGGREGATE THAT IS NOT <see cref="IAuditable"/>, and it must stay that
    /// way.</b> <c>LogEntityBuilder</c> derives from <c>BaseEntityBuilder</c>, not
    /// <c>AuditableBaseEntityBuilder</c>, and never calls <c>AddAuditableColumns</c> — so the table has NO
    /// created_by/created_on/modified_by/modified_on. VERIFIED against the migrated PostgreSQL database: the
    /// <c>log</c> table's 18 columns contain none of the four. Every other migrated cluster
    /// carries the audit quartet, so adding it here "for consistency" is the obvious mistake: it would
    /// make <c>DbContextUtils</c> and <see cref="AuditStamp"/> stamp four columns that do not exist and every
    /// single log write would fail — i.e. logging, on every request. <see cref="Log"/> has its own
    /// <see cref="LogDate"/> instead, set by <c>LogManager</c> on the calling thread.
    /// </para>
    /// <para>
    /// A Log row is append-mostly: <see cref="ILogRepository"/> adds and purges, and never updates. It is a
    /// leaf — <see cref="SiteId"/>, <see cref="PageId"/>, <see cref="ModuleId"/> and <see cref="UserId"/> are
    /// all references BY ID and all nullable, because a log entry outlives the thing it is about and
    /// must survive it being deleted.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class LogAggregate : AggregateRoot<int>
    {
        /// <summary>The <see cref="Site"/> the entry was raised in. Nullable: installation-level entries have none.</summary>
        public int? SiteId { get; set; }

        /// <summary>
        /// The entry's timestamp, and this cluster's ONLY clock. Set by <c>LogManager</c> on the request
        /// thread before the entry is queued, never by an audit hook — there is no audit hook here.
        /// <see cref="ILogRepository.GetLogsAsync"/> orders by it descending and
        /// <see cref="ILogRepository.DeleteLogsAsync"/> purges by it, so it is both the sort key and the
        /// retention key.
        /// </summary>
        public DateTime LogDate { get; set; }

        /// <summary>The <see cref="Page"/> the entry was raised on, by id. Nullable.</summary>
        public int? PageId { get; set; }

        /// <summary>The <see cref="Module"/> the entry was raised in, by id. Nullable.</summary>
        public int? ModuleId { get; set; }

        /// <summary>The <see cref="User"/> the entry is attributed to, by id. Nullable — most entries are anonymous.</summary>
        public int? UserId { get; set; }

        /// <summary>The request url. Truncated to 2048 by <see cref="LogRepository.AddLogAsync"/>, matching the column.</summary>
        public string Url { get; set; }

        /// <summary>The machine name that raised the entry. Truncated to 200.</summary>
        public string Server { get; set; }

        /// <summary>The scale-out instance id (WEBSITE_INSTANCE_ID), or blank. Nullable — a later column.</summary>
        public string Instance { get; set; }

        /// <summary>The fully-qualified type name that raised the entry. Truncated to 200.</summary>
        public string Category { get; set; }

        /// <summary>The class or component the entry came from. Truncated to 200.</summary>
        public string Feature { get; set; }

        /// <summary>Create/Read/Update/Delete/Security/Other. Truncated to 20. A filter column on <c>GetLogs</c>.</summary>
        public string Function { get; set; }

        /// <summary>The severity. Truncated to 20. A filter column on <c>GetLogs</c>.</summary>
        public string Level { get; set; }

        /// <summary>The caller's IP. Nullable — a later column.</summary>
        public string RemoteIPAddress { get; set; }

        /// <summary>The rendered message.</summary>
        public string Message { get; set; }

        /// <summary>The message before its properties were substituted in.</summary>
        public string MessageTemplate { get; set; }

        /// <summary>Exception detail, when the entry reports one. Nullable.</summary>
        public string Exception { get; set; }

        /// <summary>The serialized structured-logging properties. Nullable.</summary>
        public string Properties { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Log"/> for
        /// a delete. The store never calls this for an insert — the database assigns the identity — and
        /// <see cref="ILogRepository"/> has no update at all.
        /// </summary>
        internal void AssignId(int logId) => Id = logId;
    }
}
