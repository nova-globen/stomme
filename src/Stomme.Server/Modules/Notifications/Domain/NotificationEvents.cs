using System;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>Raised when a <see cref="NotificationRequestAggregate"/> is delivered successfully on a channel.</summary>
    public sealed record NotificationSentEvent(Guid RequestId, ChannelKind Channel) : DomainEvent;

    /// <summary>Raised when an attempt to deliver a <see cref="NotificationRequestAggregate"/> on a channel fails.</summary>
    public sealed record NotificationFailedEvent(Guid RequestId, ChannelKind Channel, string Error) : DomainEvent;

    /// <summary>Raised when a <see cref="NotificationChannelAggregate"/> is created or reconfigured (incl. enable/disable).</summary>
    public sealed record ChannelConfiguredEvent(Guid ChannelId) : DomainEvent;

    /// <summary>Raised when a <see cref="NotificationTemplateAggregate"/> is created or updated.</summary>
    public sealed record NotificationTemplateSavedEvent(Guid TemplateId) : DomainEvent;

    /// <summary>Raised when a user's <see cref="NotificationPreferenceAggregate"/> is created or reconfigured (int user id - F10).</summary>
    public sealed record NotificationPreferencesUpdatedEvent(int UserId) : DomainEvent;
}
