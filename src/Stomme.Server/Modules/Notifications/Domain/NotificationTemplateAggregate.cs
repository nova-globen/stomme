#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>
    /// A reusable, parameterized message keyed uniquely per SITE: the channel kinds it supports, a localized
    /// <see cref="Content"/> per <see cref="Locale"/>, and the <see cref="PlaceholderSchema"/> its content may
    /// reference. Tenant-owned + <see cref="SiteId"/>, with
    /// <see cref="KeyNormalized"/> maintained here for the normalized lookup (key uniqueness is enforced by the application
    /// layer through that column, both sides normalized).
    /// </summary>
    public sealed class NotificationTemplateAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private readonly List<ChannelKind> _supportedChannels = [];
        private readonly List<LocalizedContent> _contents = [];

        // Materialisation constructor. Not for application use.
        private NotificationTemplateAggregate()
        {
            TenantId = null!;
            Key = string.Empty;
            KeyNormalized = string.Empty;
            Placeholders = null!;
        }

        public TenantId TenantId { get; private set; }

        /// <summary>The site this template belongs to.</summary>
        public int SiteId { get; private set; }

        /// <summary>The site-unique template key producers reference (e.g. <c>generic.message</c>).</summary>
        public string Key { get; private set; }

        /// <summary><see cref="Key"/> upper-invariant - the normalized lookup column.</summary>
        public string KeyNormalized { get; private set; }

        public IReadOnlyList<ChannelKind> SupportedChannels => _supportedChannels;

        public IReadOnlyList<LocalizedContent> Contents => _contents;

        public PlaceholderSchema Placeholders { get; private set; }

        /// <summary>The normalization of a template key: trimmed, upper-invariant. Apply to BOTH sides of a lookup.</summary>
        public static string NormalizeKey(string key) => NotificationRequestAggregate.NormalizeTemplateKey(key);

        public static NotificationTemplateAggregate Create(
            Guid id,
            TenantId tenantId,
            int siteId,
            string key,
            IReadOnlyList<ChannelKind> supportedChannels,
            IReadOnlyList<LocalizedContent> contents,
            PlaceholderSchema placeholders)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A notification template must belong to a site.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(placeholders);

            var (channels, locales) = Validate(supportedChannels, contents);
            var trimmedKey = key.Trim();

            var template = new NotificationTemplateAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Key = trimmedKey,
                KeyNormalized = NormalizeKey(trimmedKey),
                Placeholders = placeholders,
            };

            template._supportedChannels.AddRange(channels);
            template._contents.AddRange(locales);
            template.RaiseDomainEvent(new NotificationTemplateSavedEvent(id));
            return template;
        }

        /// <summary>Replaces the supported channels, localized content, and placeholder schema (the key is immutable).</summary>
        public void Update(
            IReadOnlyList<ChannelKind> supportedChannels, IReadOnlyList<LocalizedContent> contents, PlaceholderSchema placeholders)
        {
            ArgumentNullException.ThrowIfNull(placeholders);
            var (channels, locales) = Validate(supportedChannels, contents);

            _supportedChannels.Clear();
            _supportedChannels.AddRange(channels);
            _contents.Clear();
            _contents.AddRange(locales);
            Placeholders = placeholders;
            RaiseDomainEvent(new NotificationTemplateSavedEvent(Id));
        }

        private static (IReadOnlyList<ChannelKind> Channels, IReadOnlyList<LocalizedContent> Contents) Validate(
            IReadOnlyList<ChannelKind> supportedChannels, IReadOnlyList<LocalizedContent> contents)
        {
            ArgumentNullException.ThrowIfNull(supportedChannels);
            ArgumentNullException.ThrowIfNull(contents);

            if (supportedChannels.Count == 0)
            {
                throw new ArgumentException("A template must support at least one channel kind.", nameof(supportedChannels));
            }

            if (contents.Count == 0)
            {
                throw new ArgumentException("A template must define content for at least one locale.", nameof(contents));
            }

            foreach (var content in contents)
            {
                ArgumentNullException.ThrowIfNull(content, nameof(contents));
            }

            var seenLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var content in contents)
            {
                if (!seenLocales.Add(content.Locale.Code))
                {
                    throw new ArgumentException($"Locale '{content.Locale.Code}' is defined more than once.", nameof(contents));
                }
            }

            return (supportedChannels.Distinct().ToList(), contents);
        }
    }
}
