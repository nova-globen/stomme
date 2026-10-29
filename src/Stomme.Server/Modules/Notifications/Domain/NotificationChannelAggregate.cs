#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Notifications.Domain
{
    /// <summary>
    /// A configured delivery medium for a SITE - e-mail, in-app, webhook or SMS - owning its kind-specific
    /// <see cref="TransportSettings"/> (a credential only as a site secret store reference key), an optional
    /// <see cref="RateLimit"/>, and an enablement flag. A disabled channel is skipped at delivery. Every create /
    /// reconfigure / toggle raises <see cref="ChannelConfiguredEvent"/>. Tenant-owned + <see cref="SiteId"/>,
    /// with <see cref="NameNormalized"/> maintained for the normalized lookup.
    /// </summary>
    public sealed class NotificationChannelAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private NotificationChannelAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            NameNormalized = string.Empty;
            Transport = null!;
        }

        public TenantId TenantId { get; private set; }

        /// <summary>The site this channel belongs to.</summary>
        public int SiteId { get; private set; }

        public ChannelKind Kind { get; private set; }

        public string Name { get; private set; }

        /// <summary><see cref="Name"/> upper-invariant - the normalized lookup column.</summary>
        public string NameNormalized { get; private set; }

        public bool Enabled { get; private set; }

        public TransportSettings Transport { get; private set; }

        public RateLimit? RateLimit { get; private set; }

        /// <summary>The normalization of a channel name: trimmed, upper-invariant. Apply to BOTH sides of a lookup.</summary>
        public static string NormalizeName(string name) => (name ?? string.Empty).Trim().ToUpperInvariant();

        public static NotificationChannelAggregate Create(
            Guid id, TenantId tenantId, int siteId, ChannelKind kind, string name, TransportSettings transport, RateLimit? rateLimit = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A notification channel must belong to a site.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(transport);
            EnsureTransportMatchesKind(kind, transport);

            var channel = new NotificationChannelAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Kind = kind,
                Name = name.Trim(),
                NameNormalized = NormalizeName(name),
                Enabled = true,
                Transport = transport,
                RateLimit = rateLimit,
            };

            channel.RaiseDomainEvent(new ChannelConfiguredEvent(id));
            return channel;
        }

        /// <summary>Replaces the channel's name, transport, and rate limit (the kind is immutable).</summary>
        public void Reconfigure(string name, TransportSettings transport, RateLimit? rateLimit)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(transport);
            EnsureTransportMatchesKind(Kind, transport);

            Name = name.Trim();
            NameNormalized = NormalizeName(name);
            Transport = transport;
            RateLimit = rateLimit;
            RaiseDomainEvent(new ChannelConfiguredEvent(Id));
        }

        public void Enable()
        {
            if (Enabled)
            {
                return;
            }

            Enabled = true;
            RaiseDomainEvent(new ChannelConfiguredEvent(Id));
        }

        public void Disable()
        {
            if (!Enabled)
            {
                return;
            }

            Enabled = false;
            RaiseDomainEvent(new ChannelConfiguredEvent(Id));
        }

        private static void EnsureTransportMatchesKind(ChannelKind kind, TransportSettings transport)
        {
            if (transport.Kind != kind)
            {
                throw new ArgumentException(
                    $"Transport settings are for a {transport.Kind} channel but the channel is {kind}.", nameof(transport));
            }
        }
    }
}
