using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="NotificationAggregate"/> (the persisted aggregate) and
    /// <see cref="Notification"/> (the client/server wire contract) at the
    /// <see cref="NotificationRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Notification"/> has no <c>[NotMapped]</c> members: every public property is a column and
    /// every column is copied here — including the three <see cref="IDeletable"/> members, which are ordinary
    /// columns on this table rather than view state.
    /// </remarks>
    public static class NotificationMapper
    {
        public static Notification ToModel(NotificationAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Notification
            {
                NotificationId = aggregate.Id,
                SiteId = aggregate.SiteId,
                FromUserId = aggregate.FromUserId,
                FromDisplayName = aggregate.FromDisplayName,
                FromEmail = aggregate.FromEmail,
                ToUserId = aggregate.ToUserId,
                ToDisplayName = aggregate.ToDisplayName,
                ToEmail = aggregate.ToEmail,
                ParentId = aggregate.ParentId,
                Subject = aggregate.Subject,
                Body = aggregate.Body,
                CreatedOn = aggregate.CreatedOn,
                IsDelivered = aggregate.IsDelivered,
                DeliveredOn = aggregate.DeliveredOn,
                SendOn = aggregate.SendOn,
                IsRead = aggregate.IsRead,
                DeletedBy = aggregate.DeletedBy,
                DeletedOn = aggregate.DeletedOn,
                IsDeleted = aggregate.IsDeleted
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for an update or a delete.
        /// </summary>
        public static NotificationAggregate ToAggregate(Notification notification, bool assignId)
        {
            var aggregate = new NotificationAggregate
            {
                SiteId = notification.SiteId,
                FromUserId = notification.FromUserId,
                FromDisplayName = notification.FromDisplayName,
                FromEmail = notification.FromEmail,
                ToUserId = notification.ToUserId,
                ToDisplayName = notification.ToDisplayName,
                ToEmail = notification.ToEmail,
                ParentId = notification.ParentId,
                Subject = notification.Subject,
                Body = notification.Body,
                CreatedOn = notification.CreatedOn,
                IsDelivered = notification.IsDelivered,
                DeliveredOn = notification.DeliveredOn,
                SendOn = notification.SendOn,
                IsRead = notification.IsRead,
                DeletedBy = notification.DeletedBy,
                DeletedOn = notification.DeletedOn,
                IsDeleted = notification.IsDeleted
            };

            if (assignId)
            {
                aggregate.AssignId(notification.NotificationId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the values the store assigned during a write: the generated identity, and the
        /// <see cref="IDeletable"/> stamps <see cref="DbContextUtils"/> applies on a soft delete. There are no
        /// audit stamps to copy — the Notification table has no audit columns (see
        /// <see cref="NotificationAggregate"/>).
        /// </summary>
        public static void CopyPersistedValues(NotificationAggregate aggregate, Notification notification)
        {
            notification.NotificationId = aggregate.Id;
            notification.DeletedBy = aggregate.DeletedBy;
            notification.DeletedOn = aggregate.DeletedOn;
            notification.IsDeleted = aggregate.IsDeleted;
        }
    }
}
