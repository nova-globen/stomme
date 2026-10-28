#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Domain
{
    public sealed record NoteCreatedEvent(Guid NoteId, int SiteId, int AuthorUserId) : DomainEvent;

    /// <summary>A note on one site of a tenant. Storage-blind and tenant-owned.</summary>
    public sealed class Note : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxTitleLength = 120;

        public const int MaxBodyLength = 4000;

        // Every store builds the aggregate through this constructor and then sets the properties. Not for application code.
        private Note()
        {
            TenantId = null!;
            Title = string.Empty;
            TitleKey = string.Empty;
            Body = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Title { get; private set; }

        /// <summary>The title normalised for lookups: trimmed, upper-case invariant. Set with Title, never alone.</summary>
        public string TitleKey { get; private set; }

        public string Body { get; private set; }

        public int AuthorUserId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public DateTime UpdatedUtc { get; private set; }

        public static string KeyOf(string title) => (title ?? string.Empty).Trim().ToUpperInvariant();

        public static Note Create(Guid id, TenantId tenantId, int siteId, int authorUserId, string title, string body, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A note belongs to a site.");
            }

            if (authorUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorUserId), "A note is written by a signed-in user.");
            }

            var note = new Note
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                AuthorUserId = authorUserId,
                CreatedUtc = nowUtc,
            };
            note.Apply(title, body, nowUtc);
            note.RaiseDomainEvent(new NoteCreatedEvent(note.Id, note.SiteId, note.AuthorUserId));
            return note;
        }

        public void Edit(string title, string body, DateTime nowUtc) => Apply(title, body, nowUtc);

        private void Apply(string title, string body, DateTime nowUtc)
        {
            var trimmedTitle = (title ?? string.Empty).Trim();
            if (trimmedTitle.Length == 0 || trimmedTitle.Length > MaxTitleLength)
            {
                throw new ArgumentException($"A title is 1 to {MaxTitleLength} characters.", nameof(title));
            }

            var trimmedBody = (body ?? string.Empty).Trim();
            if (trimmedBody.Length > MaxBodyLength)
            {
                throw new ArgumentException($"A note is at most {MaxBodyLength} characters.", nameof(body));
            }

            Title = trimmedTitle;
            TitleKey = KeyOf(trimmedTitle);
            Body = trimmedBody;
            UpdatedUtc = nowUtc;
        }
    }
}
