using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Guestbook.Domain
{
    /// <summary>
    /// The AGGREGATE ROOT (<see cref="AggregateRoot{TId}"/>) for a single guestbook signature — the
    /// transactional consistency boundary the repository persists. It owns its invariants: a signature always
    /// has a validated <see cref="AuthorName"/> value object and a non-empty message, and it is created only
    /// through the <see cref="Sign"/> factory, which raises a <see cref="GuestbookEntrySignedDomainEvent"/>.
    /// Persisted through the clean-arch stack (<c>IRepository&lt;GuestbookEntryAggregate, Guid&gt;</c> +
    /// <c>IUnitOfWork</c>), the same seam FeatureManagement and the lifted Identity aggregates use.
    /// <para>
    /// The parameterless constructor exists ONLY for EF Core materialisation; application code must use
    /// <see cref="Sign"/> so the invariants and the domain event cannot be skipped.
    /// </para>
    /// <para>
    /// <b>Tenant-owned (D79's correction).</b> The shared module store is ONE store per installation and
    /// <c>ModuleId</c> is a per-tenant integer that repeats across tenants, so a module-scoped aggregate let module 5
    /// of tenant one read module 5 of tenant two on EF and RavenDB (measured; the File store partitions by tenant
    /// and did not). The signature therefore carries the tenant it was signed under and every provider's tenant
    /// filter isolates it. Rows written before this column existed hold no tenant: every tenant's filter hides
    /// them (fail-closed, nothing deleted; a one-line assignment restores them to their tenant).
    /// </para>
    /// </summary>
    public sealed class GuestbookEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxMessageLength = 2000;

        // EF Core materialisation constructor. Not for application use.
        private GuestbookEntryAggregate()
        {
            TenantId = null!;
        }

        private GuestbookEntryAggregate(Guid id, TenantId tenantId, int moduleId, AuthorName author, string message, DateTime signedOnUtc)
        {
            Id = id;
            TenantId = tenantId;
            ModuleId = moduleId;
            Author = author;
            Message = message;
            SignedOnUtc = signedOnUtc;
        }

        /// <summary>The tenant the guestbook was signed under; the provider's tenant filter keys on it.</summary>
        public TenantId TenantId { get; private set; }

        /// <summary>The module instance (guestbook) this signature belongs to.</summary>
        public int ModuleId { get; private set; }

        /// <summary>The signer's name, as a validated value object.</summary>
        public AuthorName Author { get; private set; }

        /// <summary>The guestbook message.</summary>
        public string Message { get; private set; }

        /// <summary>When the guestbook was signed (UTC).</summary>
        public DateTime SignedOnUtc { get; private set; }

        /// <summary>
        /// The only way to create a signature. Validates the message invariant, builds the aggregate, and
        /// raises the <see cref="GuestbookEntrySignedDomainEvent"/> that the unit of work will dispatch after
        /// the write. The <see cref="AuthorName"/> value object enforces the name invariant on its own.
        /// </summary>
        public static GuestbookEntryAggregate Sign(TenantId tenantId, int moduleId, string author, string message)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (moduleId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(moduleId), "A guestbook signature must belong to a module.");
            }

            var trimmedMessage = (message ?? string.Empty).Trim();
            if (trimmedMessage.Length == 0)
            {
                throw new ArgumentException("A guestbook message is required.", nameof(message));
            }

            if (trimmedMessage.Length > MaxMessageLength)
            {
                throw new ArgumentException($"A guestbook message must be at most {MaxMessageLength} characters.", nameof(message));
            }

            var entry = new GuestbookEntryAggregate(
                Guid.NewGuid(), tenantId, moduleId, AuthorName.Create(author), trimmedMessage, DateTime.UtcNow);

            entry.RaiseDomainEvent(new GuestbookEntrySignedDomainEvent(entry.Id, entry.ModuleId, entry.Author.Value));
            return entry;
        }
    }
}
