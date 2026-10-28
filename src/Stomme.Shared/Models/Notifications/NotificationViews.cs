#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Models.Notifications
{
    // The WIRE contract of the Notifications module's admin + self-service surface (BL-78 step 5): what the controller
    // accepts and returns, what the Client IService and its controls bind to, and what the mediator requests carry. In
    // Stomme.Shared because the Client cannot see the Server's domain types. Enum numeric values mirror the domain enums
    // exactly (Stomme.Modules.Notifications.Domain) and never change - the handlers cast between them.

    /// <summary>The published mirror of the domain's request status (derived from the attempts).</summary>
    public enum NotificationRequestStatus
    {
        Queued = 0,
        Sent = 1,
        PartiallyFailed = 2,
        Failed = 3,
    }

    /// <summary>The published mirror of the domain's per-attempt outcome.</summary>
    public enum NotificationDeliveryStatus
    {
        Succeeded = 0,
        Failed = 1,
        Skipped = 2,
        DigestQueued = 3,
    }

    /// <summary>The published mirror of a user's severity preference.</summary>
    public enum NotificationSeverityPreference
    {
        All = 0,
        ErrorsOnly = 1,
        CriticalOnly = 2,
        None = 3,
    }

    /// <summary>The published mirror of a user's digest preference.</summary>
    public enum NotificationDigestMode
    {
        Immediate = 0,
        Digest = 1,
    }

    // ---- channels ---------------------------------------------------------------------------------------------------

    /// <summary>A channel transport option (<c>provider</c>, <c>from</c>, ...). Never a secret - the domain rejects secret-like keys.</summary>
    public sealed record NotificationChannelOption(string Key, string Value);

    /// <summary>A channel's rate limit on the wire: at most <see cref="MaxPerWindow"/> sends per <see cref="WindowSeconds"/>.</summary>
    public sealed record NotificationRateLimitInput(int MaxPerWindow, int WindowSeconds);

    /// <summary>A catalogue row of the channel list.</summary>
    public sealed record NotificationChannelSummary(Guid Id, NotificationChannelKind Kind, string Name, bool Enabled);

    /// <summary>
    /// A channel's detail for the editor. <see cref="SecretReference"/> is the site secret store REFERENCE KEY the channel
    /// holds (never the secret, which is write-only and never leaves the server).
    /// </summary>
    public sealed record NotificationChannelDetail(
        Guid Id,
        NotificationChannelKind Kind,
        string Name,
        bool Enabled,
        string? Endpoint,
        string? SecretReference,
        IReadOnlyList<NotificationChannelOption> Options,
        NotificationRateLimitInput? RateLimit);

    /// <summary>
    /// Create (null <see cref="Id"/>) or reconfigure a channel. <see cref="Secret"/> is WRITE-ONLY: when supplied it is stored
    /// in the site secret store under the channel's derived reference key and never echoed back. A caller cannot name an
    /// arbitrary reference key (that would let a channel present ANOTHER feature's secret - an MCP API key, say - to a
    /// webhook endpoint).
    /// </summary>
    public sealed record NotificationChannelInput(
        Guid? Id,
        NotificationChannelKind Kind,
        string Name,
        string? Endpoint,
        string? Secret,
        IReadOnlyList<NotificationChannelOption>? Options,
        NotificationRateLimitInput? RateLimit);

    /// <summary>Replace (a non-empty <see cref="Secret"/>) or remove (null/empty) a channel's credential. Write-only.</summary>
    public sealed record NotificationChannelCredentialInput(string? Secret);

    // ---- templates --------------------------------------------------------------------------------------------------

    /// <summary>One locale's content of a template.</summary>
    public sealed record NotificationTemplateContentInput(string LocaleCode, string Subject, string Body);

    /// <summary>A catalogue row of the template list.</summary>
    public sealed record NotificationTemplateSummary(Guid Id, string Key, IReadOnlyList<NotificationChannelKind> SupportedChannels, IReadOnlyList<string> Locales);

    /// <summary>A template's detail for the editor.</summary>
    public sealed record NotificationTemplateDetail(
        Guid Id,
        string Key,
        IReadOnlyList<NotificationChannelKind> SupportedChannels,
        IReadOnlyList<NotificationTemplateContentInput> Contents,
        IReadOnlyList<string> Placeholders);

    /// <summary>Create (null <see cref="Id"/>) or replace a template. The key is immutable once created.</summary>
    public sealed record NotificationTemplateInput(
        Guid? Id,
        string Key,
        IReadOnlyList<NotificationChannelKind> SupportedChannels,
        IReadOnlyList<NotificationTemplateContentInput> Contents,
        IReadOnlyList<string> Placeholders);

    /// <summary>Render a template's locale with sample parameters.</summary>
    public sealed record NotificationTemplatePreviewInput(Guid Id, string LocaleCode, IReadOnlyList<DispatchParameter>? Parameters);

    /// <summary>
    /// A rendered preview, HTML-encoded exactly as the e-mail and in-app channels encode parameter values. A render failure
    /// (a runaway loop, a parse error) is <see cref="Error"/>, not an exception.
    /// </summary>
    public sealed record NotificationTemplatePreview(string Locale, string? Subject, string? Body, string? Error);

    // ---- the send log -----------------------------------------------------------------------------------------------

    /// <summary>One recorded delivery attempt.</summary>
    public sealed record NotificationDeliveryAttemptView(NotificationChannelKind Channel, NotificationDeliveryStatus Status, DateTime AttemptedOn, string? Error);

    /// <summary>A recipient on a request - operator view only (the send log detail).</summary>
    public sealed record NotificationRecipientView(NotificationRecipientKind Kind, string Address);

    /// <summary>A catalogue row of the send log, and of the caller's own notification list (no recipients, no attempts).</summary>
    public sealed record NotificationRequestSummary(
        Guid Id,
        string TemplateKey,
        NotificationRequestStatus Status,
        NotificationSeverity Severity,
        string? Topic,
        IReadOnlyList<NotificationChannelKind> RequestedChannels,
        int RecipientCount,
        DateTime CreatedOn,
        DateTime? LastAttemptOn);

    /// <summary>One request's full delivery detail for the send log.</summary>
    public sealed record NotificationRequestDetail(
        Guid Id,
        string TemplateKey,
        NotificationRequestStatus Status,
        NotificationSeverity Severity,
        string? Topic,
        IReadOnlyList<NotificationRecipientView> Recipients,
        IReadOnlyList<NotificationChannelKind> RequestedChannels,
        IReadOnlyList<NotificationDeliveryAttemptView> Attempts,
        int AttemptCount,
        DateTime CreatedOn,
        DateTime NextAttemptOn,
        DateTime? CompletedOn);

    // ---- self-service preferences -----------------------------------------------------------------------------------

    /// <summary>A daily quiet window: local start/end at a fixed UTC offset (minute precision).</summary>
    public sealed record NotificationQuietHoursInput(TimeOnly Start, TimeOnly End, TimeSpan UtcOffset);

    /// <summary>The caller's own preferences - read (permissive defaults when none are saved) and saved in this one shape.</summary>
    public sealed record NotificationPreferencesView(
        IReadOnlyList<NotificationChannelKind> AcceptedChannels,
        NotificationSeverityPreference SeverityFilter,
        IReadOnlyList<string> MutedTopics,
        NotificationQuietHoursInput? QuietHours,
        NotificationDigestMode Digest,
        string? PreferredLocale);

    // ---- sending ----------------------------------------------------------------------------------------------------

    /// <summary>Send a test notification TO THE CALLER through <c>generic.message</c> over the given channels.</summary>
    public sealed record NotificationSendTestInput(IReadOnlyList<NotificationChannelKind> Channels, string Subject, string Body);
}