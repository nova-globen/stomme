#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// The outcome of one delivery pass over a request (lifted from the predecessor platform <c>DeliveryOutcome</c>): processed (the attempts
    /// reflect the result), deferred for quiet hours (<see cref="DeferFor"/>), or held for the digest (<see cref="IsDigestQueued"/>).
    /// </summary>
    public sealed record DeliveryOutcome(TimeSpan? DeferFor, bool IsDigestQueued = false)
    {
        public static readonly DeliveryOutcome Completed = new((TimeSpan?)null);

        public static readonly DeliveryOutcome ForDigest = new(null, IsDigestQueued: true);

        public static DeliveryOutcome Deferred(TimeSpan delay) => new(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    /// <summary>A request recipient paired with that recipient's preferences (null for a non-user or an unconfigured user - permissive).</summary>
    public sealed record RecipientPreference(Recipient Recipient, NotificationPreferenceAggregate? Preference);

    /// <summary>
    /// A pure, server-side preference plan for one request (lifted from the predecessor platform <c>NotificationPreferencePlan</c>, instants as
    /// UTC <see cref="DateTime"/>): per channel, which recipients remain after their channel/severity/topic preferences; whether
    /// the whole request is held for the digest; whether it is deferred for quiet hours.
    /// </summary>
    public sealed class NotificationPreferencePlan
    {
        private readonly NotificationRequestAggregate _request;
        private readonly IReadOnlyList<RecipientPreference> _recipients;

        public NotificationPreferencePlan(NotificationRequestAggregate request, IReadOnlyList<RecipientPreference> recipients)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        }

        /// <summary>True when the recipient accepts <paramref name="kind"/> under their severity/topic preferences (no preferences: accepts everything).</summary>
        public bool IsEligible(RecipientPreference recipient, ChannelKind kind)
        {
            var preference = recipient.Preference;
            return preference is null
                || (preference.AcceptsChannel(kind)
                    && preference.AllowsSeverity(_request.Severity)
                    && preference.IsSubscribedTo(_request.Topic));
        }

        public IReadOnlyList<Recipient> EligibleRecipients(ChannelKind kind) =>
            _recipients.Where(recipient => IsEligible(recipient, kind)).Select(recipient => recipient.Recipient).ToList();

        /// <summary>The preferred locale of the first recipient that configured one, or null for the template default.</summary>
        public string? PreferredLocale() =>
            _recipients.Select(recipient => recipient.Preference?.PreferredLocale)
                .FirstOrDefault(locale => !string.IsNullOrWhiteSpace(locale));

        /// <summary>True when EVERY targeted recipient is a user in digest mode; one non-user recipient bypasses the digest.</summary>
        public bool ShouldDeferToDigest()
        {
            if (_recipients.Any(recipient => recipient.Recipient.Kind != RecipientKind.User))
            {
                return false;
            }

            return _recipients.Count > 0 && _recipients.All(recipient => recipient.Preference?.Digest == DigestMode.Digest);
        }

        /// <summary>
        /// How long to defer the whole request for quiet hours, or null to deliver now. Critical bypasses. Deferred only when
        /// EVERY recipient still eligible for a pending channel is a user inside their quiet window; anyone reachable now wins.
        /// </summary>
        public TimeSpan? QuietDeferral(IReadOnlyList<ChannelKind> pendingChannels, DateTime nowUtc)
        {
            if (_request.Severity == NotificationSeverity.Critical)
            {
                return null;
            }

            var active = _recipients.Where(recipient => pendingChannels.Any(kind => IsEligible(recipient, kind))).ToList();
            if (active.Count == 0)
            {
                return null;
            }

            var windowEnds = new List<DateTime>();
            foreach (var recipient in active)
            {
                if (recipient.Preference?.QuietHours is { } quietHours && quietHours.IsQuietAt(nowUtc))
                {
                    windowEnds.Add(quietHours.NextEnd(nowUtc));
                }
                else
                {
                    return null; // someone is reachable right now -> deliver immediately
                }
            }

            return DeliveryOutcome.Deferred(windowEnds.Min() - nowUtc).DeferFor;
        }
    }

    /// <summary>Loads each user recipient's preferences (per SITE, int user id - F10) and builds the pure plan.</summary>
    public sealed class NotificationPreferenceResolver
    {
        private readonly INotificationPreferenceRepository _preferences;

        public NotificationPreferenceResolver(INotificationPreferenceRepository preferences) =>
            _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

        public async Task<NotificationPreferencePlan> ResolveAsync(NotificationRequestAggregate request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var resolved = new List<RecipientPreference>(request.Recipients.Count);
            foreach (var recipient in request.Recipients)
            {
                NotificationPreferenceAggregate? preference = null;
                if (recipient.Kind == RecipientKind.User
                    && int.TryParse(recipient.Address, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
                {
                    preference = await _preferences.GetByUserAsync(request.SiteId, userId, ct);
                }

                resolved.Add(new RecipientPreference(recipient, preference));
            }

            return new NotificationPreferencePlan(request, resolved);
        }
    }
}
