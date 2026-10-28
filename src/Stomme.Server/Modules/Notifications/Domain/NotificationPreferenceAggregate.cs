#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Models.Notifications;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>
    /// A single site user's notification preferences, resolved server-side before fan-out: accepted channel kinds, a
    /// severity filter, muted topics, a quiet-hours window, a digest preference, and a preferred locale. One per
    /// (site, user); absence of a record means "accept everything". Lifted from the predecessor platform <c>NotificationPreference</c>
    /// (BL-78) with <see cref="UserId"/> an int (Stomme users - F10; the predecessor platform used a Guid), tenant-owned +
    /// <see cref="SiteId"/>.
    /// </summary>
    public sealed class NotificationPreferenceAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<ChannelKind> _acceptedChannels = [];
        private readonly List<string> _mutedTopics = [];

        // Materialisation constructor. Not for application use.
        private NotificationPreferenceAggregate()
        {
            TenantId = null!;
        }

        public TenantId TenantId { get; private set; }

        /// <summary>The site these preferences apply on.</summary>
        public int SiteId { get; private set; }

        /// <summary>The site user these preferences belong to.</summary>
        public int UserId { get; private set; }

        /// <summary>The channel kinds the user accepts. <strong>Empty means "accept all"</strong> (the default).</summary>
        public IReadOnlyList<ChannelKind> AcceptedChannels => _acceptedChannels;

        public NotificationSeverityFilter SeverityFilter { get; private set; }

        /// <summary>Topics the user has unsubscribed from. A request whose topic is muted is suppressed.</summary>
        public IReadOnlyList<string> MutedTopics => _mutedTopics;

        /// <summary>An optional daily quiet window during which non-critical notifications are deferred.</summary>
        public QuietHours? QuietHours { get; private set; }

        public DigestMode Digest { get; private set; }

        /// <summary>An optional preferred BCP-47 locale used to select template content for this user.</summary>
        public string? PreferredLocale { get; private set; }

        public static NotificationPreferenceAggregate Create(Guid id, TenantId tenantId, int siteId, int userId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A notification preference must belong to a site.");
            }

            if (userId <= 0)
            {
                throw new ArgumentException("A user id is required.", nameof(userId));
            }

            var preference = new NotificationPreferenceAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                SeverityFilter = NotificationSeverityFilter.All,
                Digest = DigestMode.Immediate,
            };

            preference.RaiseDomainEvent(new NotificationPreferencesUpdatedEvent(userId));
            return preference;
        }

        /// <summary>Replaces every preference setting, validating and normalizing the inputs, and raises an update event.</summary>
        public void Configure(
            IReadOnlyList<ChannelKind> acceptedChannels,
            NotificationSeverityFilter severityFilter,
            IReadOnlyList<string> mutedTopics,
            QuietHours? quietHours,
            DigestMode digest,
            string? preferredLocale)
        {
            ArgumentNullException.ThrowIfNull(acceptedChannels);
            ArgumentNullException.ThrowIfNull(mutedTopics);

            _acceptedChannels.Clear();
            _acceptedChannels.AddRange(acceptedChannels.Distinct());

            _mutedTopics.Clear();
            foreach (var topic in mutedTopics)
            {
                var trimmed = topic?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !_mutedTopics.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    _mutedTopics.Add(trimmed);
                }
            }

            SeverityFilter = severityFilter;
            QuietHours = quietHours;
            Digest = digest;
            PreferredLocale = string.IsNullOrWhiteSpace(preferredLocale) ? null : preferredLocale.Trim();

            RaiseDomainEvent(new NotificationPreferencesUpdatedEvent(UserId));
        }

        /// <summary>True when the user accepts the given channel kind (an empty accepted set accepts all kinds).</summary>
        public bool AcceptsChannel(ChannelKind kind) =>
            _acceptedChannels.Count == 0 || _acceptedChannels.Contains(kind);

        /// <summary>True when a request of the given severity passes this user's severity filter.</summary>
        public bool AllowsSeverity(NotificationSeverity severity) => SeverityFilter switch
        {
            NotificationSeverityFilter.All => true,
            NotificationSeverityFilter.ErrorsOnly => severity >= NotificationSeverity.Warning,
            NotificationSeverityFilter.CriticalOnly => severity == NotificationSeverity.Critical,
            NotificationSeverityFilter.None => false,
            _ => true,
        };

        /// <summary>True when the user has not muted the given topic (a null/blank topic is never muted).</summary>
        public bool IsSubscribedTo(string? topic) =>
            string.IsNullOrWhiteSpace(topic) || !_mutedTopics.Contains(topic.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
