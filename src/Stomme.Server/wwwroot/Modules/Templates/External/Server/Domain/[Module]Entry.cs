using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace [Owner].Module.[Module].Domain
{
    /// <summary>Raised when an entry is added - a handler reacts in the same unit of work.</summary>
    public sealed record [Module]EntryAddedEvent(Guid EntryId, int SiteId, int AuthorUserId) : DomainEvent;

    /// <summary>
    /// The module's aggregate: STORAGE-BLIND - the module writes no DbContext, no store and no provider branch; the
    /// host persists it on whichever store the installation chose - and TENANT-OWNED: the host's module store is one
    /// per installation, so every row carries its tenant and the store filters by it. It belongs to one SITE of that tenant.
    /// </summary>
    public sealed class [Module]Entry : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxTextLength = 500;

        // The materialisation constructor every store builds the aggregate through. Not for application use.
        private [Module]Entry()
        {
            TenantId = null!;
            Text = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Text { get; private set; }

        public int AuthorUserId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public static [Module]Entry Add(Guid id, TenantId tenantId, int siteId, string text, int authorUserId, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An entry belongs to a site.");
            }

            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxTextLength)
            {
                throw new ArgumentException($"An entry's text is 1 to {MaxTextLength} characters.", nameof(text));
            }

            var entry = new [Module]Entry
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Text = trimmed,
                AuthorUserId = authorUserId,
                CreatedUtc = nowUtc,
            };
            entry.RaiseDomainEvent(new [Module]EntryAddedEvent(entry.Id, entry.SiteId, entry.AuthorUserId));
            return entry;
        }
    }
}
