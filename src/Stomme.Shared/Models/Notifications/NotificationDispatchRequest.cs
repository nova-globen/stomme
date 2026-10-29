#nullable enable
using System.Collections.Generic;

namespace Stomme.Models.Notifications
{
    /// <summary>
    /// What a producer hands to <see cref="Stomme.Notifications.INotificationDispatcher"/>: the template to render,
    /// who to reach, the channels to reach them over, the parameters the template binds, and - optionally - the
    /// severity and a subscription topic used to honour per-user preferences. Lifted verbatim in shape from the predecessor platform's
    /// published DTO; <see cref="Severity"/> and <see cref="Topic"/> are appended optional
    /// members so producers stay source-compatible.
    /// </summary>
    public sealed record NotificationDispatchRequest(
        string TemplateKey,
        IReadOnlyList<DispatchRecipient> Recipients,
        IReadOnlyList<NotificationChannelKind> Channels,
        IReadOnlyList<DispatchParameter>? Parameters = null,
        NotificationSeverity Severity = NotificationSeverity.Information,
        string? Topic = null);

    /// <summary>A delivery target on a dispatch request; <see cref="Value"/> is interpreted per <see cref="Kind"/>.</summary>
    public sealed record DispatchRecipient(NotificationRecipientKind Kind, string Value);

    /// <summary>A single template-parameter binding supplied with a dispatch request.</summary>
    public sealed record DispatchParameter(string Name, string Value);
}