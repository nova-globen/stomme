namespace Stomme.Models.Notifications
{
    /// <summary>
    /// The transport a producer asks a notification to be delivered over - the published mirror of the module's
    /// channel kinds. The numeric values are the WIRE contract (lifted from the predecessor platform) and never change.
    /// </summary>
    public enum NotificationChannelKind
    {
        /// <summary>E-mail: written to the site's existing notification queue and sent by the SMTP job.</summary>
        Email = 0,

        /// <summary>In-app: the notification center, pushed live when SignalR is enabled.</summary>
        InApp = 1,

        /// <summary>Outbound HTTP POST to a configured, SSRF-guarded endpoint.</summary>
        Webhook = 2,

        /// <summary>SMS - an enum entry only; no provider ships (a dispatch over it is skipped).</summary>
        Sms = 3,
    }

    /// <summary>How a <see cref="DispatchRecipient"/> is addressed - the published mirror of the module's recipient kinds.</summary>
    public enum NotificationRecipientKind
    {
        /// <summary>A site user, by id.</summary>
        User = 0,

        /// <summary>A literal e-mail address - accepted only when the site allows external recipients.</summary>
        Email = 1,

        /// <summary>
        /// A literal endpoint (phone number, webhook URL) addressed directly. Kept for wire stability with the predecessor platform's
        /// published enum; the dispatcher REJECTS it (an unguarded literal endpoint is an open relay and bypasses
        /// the per-channel SSRF guard).
        /// </summary>
        Endpoint = 2,
    }

    /// <summary>How urgent a dispatch is. <see cref="Critical"/> bypasses quiet hours.</summary>
    public enum NotificationSeverity
    {
        /// <summary>Routine, informational.</summary>
        Information = 0,

        /// <summary>A warning or error condition.</summary>
        Warning = 1,

        /// <summary>Urgent and time-critical - bypasses quiet hours.</summary>
        Critical = 2,
    }
}