namespace Stomme.Modules.Notifications.Domain
{
    // Lifted from the predecessor platform src/Modules/Notifications/the predecessor platform.Notifications.Domain/NotificationEnums.cs (BL-78). The
    // numeric values are the WIRE contract shared with the published mirrors in Stomme.Shared
    // (Stomme.Models.Notifications.NotificationChannelKind / NotificationRecipientKind) and never change.
    // NotificationSeverity is NOT redeclared here: the domain uses the published Stomme.Models.Notifications
    // enum directly (identical members and values), so a request's severity needs no mapping at dispatch.

    /// <summary>
    /// The transport over which a notification is delivered. Each kind is backed by a channel provider;
    /// <see cref="Sms"/> is an enum entry only (no provider ships - a dispatch over it is skipped).
    /// </summary>
    public enum ChannelKind
    {
        /// <summary>E-mail: written to the site's existing notification queue and sent by the SMTP job.</summary>
        Email = 0,

        /// <summary>In-app: the notification center, pushed live when SignalR is enabled.</summary>
        InApp = 1,

        /// <summary>Outbound HTTP POST to a configured, SSRF-guarded endpoint.</summary>
        Webhook = 2,

        /// <summary>SMS - no provider ships.</summary>
        Sms = 3,
    }

    /// <summary>How a <see cref="Recipient"/> is addressed.</summary>
    public enum RecipientKind
    {
        /// <summary>A site user, by int user id.</summary>
        User = 0,

        /// <summary>A literal e-mail address (accepted at dispatch only when the site allows external recipients).</summary>
        Email = 1,

        /// <summary>
        /// A literal endpoint addressed directly. Kept for wire stability with the predecessor platform's published enum; the
        /// dispatcher REJECTS it (an open relay and a path around the per-channel SSRF guard - BL-78 F9).
        /// </summary>
        Endpoint = 2,
    }

    /// <summary>The aggregate delivery state of a <see cref="NotificationRequestAggregate"/>, derived from its attempts.</summary>
    public enum NotificationStatus
    {
        /// <summary>Persisted and awaiting dispatch; no (non-digest) attempts recorded yet.</summary>
        Queued = 0,

        /// <summary>Every requested channel is resolved (succeeded or deliberately skipped).</summary>
        Sent = 1,

        /// <summary>Some requested channels are resolved and others are not.</summary>
        PartiallyFailed = 2,

        /// <summary>Every requested channel was attempted and none is resolved.</summary>
        Failed = 3,
    }

    /// <summary>The outcome of a single <see cref="DeliveryAttempt"/> on one channel.</summary>
    public enum DeliveryStatus
    {
        /// <summary>The transport reported success.</summary>
        Succeeded = 0,

        /// <summary>The transport reported a failure (see <see cref="DeliveryAttempt.Error"/>).</summary>
        Failed = 1,

        /// <summary>
        /// Deliberately not sent because every targeted recipient's preferences suppressed it. A resolved, non-failure
        /// outcome: it counts toward "all channels attempted" and does not trigger a retry.
        /// </summary>
        Skipped = 2,

        /// <summary>Immediate delivery suppressed for a digest; a pending placeholder, neither resolved nor failed.</summary>
        DigestQueued = 3,
    }

    /// <summary>A user's severity preference ("All / Errors only / Critical only / None").</summary>
    public enum NotificationSeverityFilter
    {
        /// <summary>Deliver notifications of any severity.</summary>
        All = 0,

        /// <summary>Deliver only Warning and above.</summary>
        ErrorsOnly = 1,

        /// <summary>Deliver only Critical notifications.</summary>
        CriticalOnly = 2,

        /// <summary>Suppress all notifications for this user.</summary>
        None = 3,
    }

    /// <summary>Whether a user receives notifications immediately or batched into a periodic digest.</summary>
    public enum DigestMode
    {
        /// <summary>Deliver each notification as it is dispatched.</summary>
        Immediate = 0,

        /// <summary>Batch notifications into the hourly digest flush.</summary>
        Digest = 1,
    }
}
