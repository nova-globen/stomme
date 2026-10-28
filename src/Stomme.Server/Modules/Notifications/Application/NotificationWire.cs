#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Domain;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// The one place the module's domain types become its Shared wire contract (and back) - BL-78 step 5. The enum casts are
    /// safe by construction: the Shared mirrors carry the domain enums' numeric values, pinned by
    /// <c>NotificationValueObjectCanonicalFormTests</c> and <c>NotificationPreferenceCqrsTests.The_wire_enums_mirror_the_domain_enums_by_name_and_value</c>.
    /// </summary>
    public static class NotificationWire
    {
        public static ChannelKind ToDomain(NotificationChannelKind kind)
        {
            var domain = (ChannelKind)(int)kind;
            if (!Enum.IsDefined(domain))
            {
                throw new InvalidOperationException($"'{(int)kind}' is not a notification channel kind.");
            }

            return domain;
        }

        public static NotificationChannelKind ToWire(ChannelKind kind) => (NotificationChannelKind)(int)kind;

        public static IReadOnlyList<ChannelKind> ToDomain(IReadOnlyList<NotificationChannelKind>? kinds) =>
            (kinds ?? Array.Empty<NotificationChannelKind>()).Select(ToDomain).ToList();

        public static IReadOnlyList<NotificationChannelKind> ToWire(IEnumerable<ChannelKind> kinds) => kinds.Select(ToWire).ToList();

        public static NotificationSeverityFilter ToDomain(NotificationSeverityPreference filter)
        {
            var domain = (NotificationSeverityFilter)(int)filter;
            if (!Enum.IsDefined(domain))
            {
                throw new InvalidOperationException($"'{(int)filter}' is not a severity preference.");
            }

            return domain;
        }

        public static NotificationSeverityPreference ToWire(NotificationSeverityFilter filter) => (NotificationSeverityPreference)(int)filter;

        public static DigestMode ToDomain(NotificationDigestMode mode)
        {
            var domain = (DigestMode)(int)mode;
            if (!Enum.IsDefined(domain))
            {
                throw new InvalidOperationException($"'{(int)mode}' is not a digest mode.");
            }

            return domain;
        }

        public static NotificationDigestMode ToWire(DigestMode mode) => (NotificationDigestMode)(int)mode;

        public static NotificationChannelSummary ToSummary(NotificationChannelAggregate channel) =>
            new(channel.Id, ToWire(channel.Kind), channel.Name, channel.Enabled);

        public static NotificationChannelDetail ToDetail(NotificationChannelAggregate channel) =>
            new(channel.Id,
                ToWire(channel.Kind),
                channel.Name,
                channel.Enabled,
                channel.Transport.Endpoint,
                channel.Transport.SecretReference,
                channel.Transport.Options.Select(option => new NotificationChannelOption(option.Key, option.Value)).ToList(),
                channel.RateLimit is { } limit ? new NotificationRateLimitInput(limit.MaxPerWindow, (int)limit.Window.TotalSeconds) : null);

        public static NotificationTemplateSummary ToSummary(NotificationTemplateAggregate template) =>
            new(template.Id, template.Key, ToWire(template.SupportedChannels), template.Contents.Select(content => content.Locale.Code).ToList());

        public static NotificationTemplateDetail ToDetail(NotificationTemplateAggregate template) =>
            new(template.Id,
                template.Key,
                ToWire(template.SupportedChannels),
                template.Contents.Select(content => new NotificationTemplateContentInput(content.Locale.Code, content.Content.Subject, content.Content.Body)).ToList(),
                template.Placeholders.Placeholders.ToList());

        public static NotificationRequestSummary ToSummary(NotificationRequestAggregate request) =>
            new(request.Id,
                request.TemplateKey,
                (NotificationRequestStatus)(int)request.Status,
                request.Severity,
                request.Topic,
                ToWire(request.RequestedChannels),
                request.Recipients.Count,
                request.CreatedOn,
                LastAttemptOn(request));

        public static NotificationRequestDetail ToDetail(NotificationRequestAggregate request) =>
            new(request.Id,
                request.TemplateKey,
                (NotificationRequestStatus)(int)request.Status,
                request.Severity,
                request.Topic,
                request.Recipients.Select(recipient => new NotificationRecipientView((NotificationRecipientKind)(int)recipient.Kind, recipient.Address)).ToList(),
                ToWire(request.RequestedChannels),
                request.Attempts
                    .OrderBy(attempt => attempt.AttemptedOn)
                    .Select(attempt => new NotificationDeliveryAttemptView(ToWire(attempt.Channel), (NotificationDeliveryStatus)(int)attempt.Status, attempt.AttemptedOn, attempt.Error))
                    .ToList(),
                request.AttemptCount,
                request.CreatedOn,
                request.NextAttemptOn,
                request.CompletedOn);

        public static DateTime? LastAttemptOn(NotificationRequestAggregate request) =>
            request.Attempts.Count == 0 ? null : request.Attempts.Max(attempt => attempt.AttemptedOn);

        /// <summary>True when <paramref name="request"/> addresses the site user <paramref name="userId"/> directly.</summary>
        public static bool Targets(NotificationRequestAggregate request, int userId)
        {
            var id = userId.ToString(CultureInfo.InvariantCulture);
            return request.Recipients.Any(recipient => recipient.Kind == RecipientKind.User && string.Equals(recipient.Address, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// The resolved site of the current scope - the request's alias, never a caller-supplied id. Throws when there is none
        /// (a handler reached outside a site-resolved scope).
        /// </summary>
        public static int RequireSiteId(SiteState siteState)
        {
            var siteId = siteState?.Alias?.SiteId ?? -1;
            if (siteId <= 0)
            {
                throw new InvalidOperationException("No site is resolved in this scope.");
            }

            return siteId;
        }
    }
}