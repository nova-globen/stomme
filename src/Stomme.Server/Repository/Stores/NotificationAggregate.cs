using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Notification aggregate root — the persistence-side model of the framework store's
    /// <c>Notification</c> table, reachable only through <see cref="INotificationStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Notification is an outbound message, usually delivered as email by <c>NotificationJob</c>. It is a
    /// leaf: <see cref="FromUserId"/>, <see cref="ToUserId"/> and <see cref="ParentId"/> are all references BY
    /// ID (D7/D19), all nullable, and the display names and emails are DENORMALIZED onto the row on purpose —
    /// a notification must still be deliverable after the user it names is deleted.
    /// </para>
    /// <para>
    /// <b>THIS AGGREGATE IMPLEMENTS <see cref="IDeletable"/>, AND THAT IS LOAD-BEARING.</b>
    /// <see cref="DbContextUtils.SaveChanges"/> stamps <see cref="DeletedBy"/>/<see cref="DeletedOn"/> by
    /// testing <c>item.Entity is IDeletable</c> — a RUNTIME type test against the entity the context is
    /// tracking. Before this increment that entity was the <see cref="Notification"/> contract, which
    /// implements the interface; now it is this aggregate. Drop the interface here and everything still
    /// compiles, every test that does not assert the stamp still passes, and soft-deletes silently stop
    /// recording who deleted what and when. It is the one thing in this cluster the compiler cannot see.
    /// </para>
    /// <para>
    /// <b>NOT <see cref="IAuditable"/>, unlike most of the framework store.</b> There are no
    /// created_by/modified_by/modified_on columns — <c>NotificationEntityBuilder</c> derives from
    /// <c>DeletableBaseEntityBuilder</c> and calls <c>AddDeletableColumns</c>, never
    /// <c>AddAuditableColumns</c>. VERIFIED against the migrated PostgreSQL database. <see cref="CreatedOn"/>
    /// below is the notification's OWN column, set by the <see cref="Notification"/> constructors, not an
    /// audit stamp; <see cref="AuditStamp.IsAuditable"/> requires all four members and so correctly declines
    /// to stamp this type on both providers.
    /// </para>
    /// <para>
    /// <b>The <see cref="IDeletable"/> stamp now agrees across providers (D59).</b> It was formerly applied by
    /// the relational path only — <see cref="AuditStamp"/> did not port it, because <c>DbContextUtils</c>'s
    /// rule compares the incoming <c>IsDeleted</c> against the value in the database and a document provider
    /// cannot evaluate that without an extra load. D59 ported it WITHOUT that load: <see cref="AuditStamp.StampDeleted"/>
    /// (called from <c>RavenAuditListener</c>) stamps <see cref="DeletedBy"/>/<see cref="DeletedOn"/> on the first
    /// transition to deleted (<c>IsDeleted == true &amp;&amp; DeletedOn == null</c>) and clears them on restore, so
    /// a Notification soft-deleted through the RavenDB store now records the same DeletedBy/DeletedOn as PostgreSQL.
    /// This cluster was the first <see cref="IDeletable"/> aggregate on the seam and so the one that carried the gap;
    /// it is DRIVEN in <c>Step5DivergenceDriveTests</c> on both real providers.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class NotificationAggregate : AggregateRoot<int>, IDeletable
    {
        /// <summary>The <see cref="Site"/> the notification belongs to, by id (D7/D19).</summary>
        public int SiteId { get; set; }

        /// <summary>The sending <see cref="User"/>, by id, or null when the system sent it. Nullable — and the purge filters on it being null.</summary>
        public int? FromUserId { get; set; }

        /// <summary>The sender's display name, denormalized. Nullable — a later column.</summary>
        public string FromDisplayName { get; set; }

        /// <summary>The sender's email, denormalized. Nullable — a later column.</summary>
        public string FromEmail { get; set; }

        /// <summary>The recipient <see cref="User"/>, by id, or null when the recipient is not a user. Nullable.</summary>
        public int? ToUserId { get; set; }

        /// <summary>The recipient's display name, denormalized. Nullable — a later column.</summary>
        public string ToDisplayName { get; set; }

        /// <summary>The recipient's email, denormalized. This is what actually gets delivered to.</summary>
        public string ToEmail { get; set; }

        /// <summary>The parent <see cref="Notification"/> in a reply thread, by id. Nullable.</summary>
        public int? ParentId { get; set; }

        /// <summary>The message subject.</summary>
        public string Subject { get; set; }

        /// <summary>The message body.</summary>
        public string Body { get; set; }

        /// <summary>
        /// When the notification was created. The notification's OWN column, set by the
        /// <see cref="Notification"/> constructors — NOT an audit stamp (see the remarks). Also the sort key
        /// of <c>GetNotifications</c>.
        /// </summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Whether delivery has been attempted successfully.</summary>
        public bool IsDelivered { get; set; }

        /// <summary>When delivery succeeded. Nullable, and the purge's retention key.</summary>
        public DateTime? DeliveredOn { get; set; }

        /// <summary>When the notification SHOULD be sent — the queue's eligibility key. Nullable — a later column.</summary>
        public DateTime? SendOn { get; set; }

        /// <summary>
        /// Whether the recipient has read it.
        /// <para>
        /// NON-NULLABLE against a NULLABLE <c>is_read</c> column — harvested and reproduced rather than
        /// tidied, exactly as <see cref="SettingAggregate.IsPrivate"/> is.
        /// <c>04000101_AddNotificationIsRead</c> added the column nullable and then backfilled it
        /// (<c>UpdateData("IsRead", true)</c>), so no NULL survives for the non-nullable mapping to choke on.
        /// </para>
        /// </summary>
        public bool IsRead { get; set; }

        #region IDeletable

        /// <inheritdoc />
        public string DeletedBy { get; set; }

        /// <inheritdoc />
        public DateTime? DeletedOn { get; set; }

        /// <inheritdoc />
        public bool IsDeleted { get; set; }

        #endregion

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="Notification"/> for an update or a delete. The store never calls this for an insert —
        /// the database assigns the identity.
        /// </summary>
        internal void AssignId(int notificationId) => Id = notificationId;
    }
}
