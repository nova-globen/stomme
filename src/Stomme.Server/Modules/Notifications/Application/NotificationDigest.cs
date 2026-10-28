#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Modules.Notifications.Domain;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>One user's batch of digest-held requests (int user id - Stomme users, BL-78 F10).</summary>
    public sealed record DigestBatch(int UserId, IReadOnlyList<NotificationRequestAggregate> Requests);

    /// <summary>
    /// The digest-grouping primitive the hourly flush consumes, lifted from the predecessor platform
    /// <c>the predecessor platform.Notifications.Application/NotificationPreferences.cs</c> (<c>NotificationDigest.GroupForDigest</c>)
    /// with the user id adapted from Guid to int: requests grouped per user, users in ascending id order, each user's
    /// requests in input order.
    /// </summary>
    public static class NotificationDigest
    {
        public static IReadOnlyList<DigestBatch> GroupForDigest(IEnumerable<(int UserId, NotificationRequestAggregate Request)> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            return entries
                .GroupBy(entry => entry.UserId)
                .OrderBy(group => group.Key)
                .Select(group => new DigestBatch(group.Key, group.Select(entry => entry.Request).ToList()))
                .ToList();
        }
    }
}
