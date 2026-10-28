#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Collaboration;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Collaboration.Domain
{
    // ============================================================================================================
    // The Collaboration domain (BL-97 increment 11, D114), lifted from the predecessor platform's Collaboration at 183f5c52: an advisory
    // ResourceLock living on a lease, and an EditSession of who is present on one resource and what they changed. What
    // CHANGED for both: they belong to a SITE of their tenant (every Stomme page-owned aggregate does), a user is a
    // Stomme user id (an int), a resource id is a string (a pipeline's is the engine's definition id), and times are
    // UTC DateTimes. Storage-blind: the participants and the change stream ride the module store's JSON collection columns.
    // ============================================================================================================

    /// <summary>
    /// The resource a lock or a session coordinates on: a lowercase type token (<c>pipeline</c>, <c>dashboard</c>) and the
    /// resource's id. The scalar is <c>{type}/{id}</c>. <b>CHANGED:</b> the id is a string - the predecessor platform's was a Guid, which a
    /// pipeline's definition id is not.
    /// </summary>
    public sealed class ResourceRef : ValueObject, IStringValueObject
    {
        public const int MaxIdLength = 100;

        private ResourceRef(string resourceType, string resourceId)
        {
            ResourceType = resourceType;
            ResourceId = resourceId;
        }

        public string ResourceType { get; }

        public string ResourceId { get; }

        public string Value => ResourceType + "/" + ResourceId;

        /// <summary>
        /// A reference to the resource of the given type and id. The type is a lowercase token - a leading ASCII letter, then
        /// letters, digits or hyphens; the id is non-blank, at most <see cref="MaxIdLength"/> characters, without a slash
        /// or whitespace.
        /// </summary>
        /// <exception cref="InvalidResourceRefException">The type is not a lowercase token, or the id is blank, too long, or holds a slash or whitespace.</exception>
        public static ResourceRef Of(string resourceType, string resourceId)
        {
            var type = resourceType?.Trim() ?? string.Empty;
            if (type.Length == 0 || type.Length > 50 || !IsLowercaseToken(type))
            {
                throw new InvalidResourceRefException($"Resource type '{resourceType}' is not a lowercase token (a leading letter followed by letters, digits, or hyphens).", nameof(resourceType));
            }

            var id = resourceId?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                throw new InvalidResourceRefException("A resource reference requires a resource id.", nameof(resourceId));
            }

            if (id.Length > MaxIdLength || id.Any(character => character == '/' || char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                throw new InvalidResourceRefException($"A resource id is at most {MaxIdLength} characters, with no slash or whitespace.", nameof(resourceId));
            }

            return new ResourceRef(type, id);
        }

        public static ResourceRef Of(string resourceType, Guid resourceId) =>
            resourceId == Guid.Empty
                ? throw new InvalidResourceRefException("A resource reference requires a non-empty resource id.", nameof(resourceId))
                : Of(resourceType, resourceId.ToString("D"));

        public static ResourceRef Create(string value)
        {
            var slash = (value ?? string.Empty).IndexOf('/', StringComparison.Ordinal);
            return slash <= 0
                ? throw new InvalidResourceRefException($"'{value}' is not a resource reference ({{type}}/{{id}}).", nameof(value))
                : Of(value![..slash], value[(slash + 1)..]);
        }

        public override string ToString() => Value;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return ResourceType;
            yield return ResourceId;
        }

        private static bool IsLowercaseToken(string value) =>
            char.IsAsciiLetterLower(value[0]) && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
    }

    /// <summary>A resource reference that does not follow the rules - an input error, written to be shown.</summary>
    public sealed class InvalidResourceRefException(string message, string paramName) : ArgumentException(message, paramName);

    /// <summary>
    /// A lock or a session refused an act in its current state - released, lapsed, closed, not the holder, not a
    /// participant. The message is written to be shown; nothing changed.
    /// </summary>
    public sealed class CollaborationConflictException(string message) : InvalidOperationException(message), Stomme.Mediator.IExpectedRefusal;

    public sealed record LockAcquiredEvent(Guid LockId, int SiteId, string Resource, LockScope Scope, int HolderUserId, DateTime ExpiresUtc) : DomainEvent;

    public sealed record LockReleasedEvent(Guid LockId, int SiteId, string Resource, LockScope Scope, int HolderUserId) : DomainEvent;

    /// <summary>
    /// An ADVISORY lock on a resource (D114): one holder per resource and scope among the live locks (the lock service's
    /// invariant), living on a lease that lapses without renewal so an abandoned browser never holds a resource forever.
    /// Only the holder renews or releases; a lapsed lease is treated as released and cannot be revived. Holding it is shown
    /// to the other editors - it never blocks their save, which the owning module's optimistic concurrency still decides.
    /// </summary>
    public sealed class ResourceLockAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        /// <summary>A lease is at most this long; the editor renews it while it stays open.</summary>
        public static readonly TimeSpan MaxLease = TimeSpan.FromHours(1);

        // Materialisation constructor. Not for application use.
        private ResourceLockAggregate()
        {
            TenantId = null!;
            Resource = null!;
            HolderDisplayName = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public ResourceRef Resource { get; private set; }

        public LockScope Scope { get; private set; }

        public int HolderUserId { get; private set; }

        public string HolderDisplayName { get; private set; }

        /// <summary>The lease's length in seconds - each renewal extends it this far from the renewal instant.</summary>
        public int LeaseSeconds { get; private set; }

        public DateTime AcquiredUtc { get; private set; }

        public DateTime ExpiresUtc { get; private set; }

        public bool Released { get; private set; }

        /// <summary>Who released the lock when it was not its holder - an administrator taking it over (D114); null otherwise.</summary>
        public int? ReleasedByUserId { get; private set; }

        public static ResourceLockAggregate Acquire(Guid id, TenantId tenantId, int siteId, ResourceRef resource, LockScope scope, int holderId, string holderName, TimeSpan lease, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(resource);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A lock belongs to a site.");
            }

            if (holderId <= 0)
            {
                throw new ArgumentException("A lock requires a holder.", nameof(holderId));
            }

            var seconds = LeaseSeconds_(lease);
            var now = Utc(nowUtc);
            var resourceLock = new ResourceLockAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Resource = resource,
                Scope = scope,
                HolderUserId = holderId,
                HolderDisplayName = Name(holderName),
                LeaseSeconds = seconds,
                AcquiredUtc = now,
                ExpiresUtc = now.AddSeconds(seconds),
            };
            resourceLock.RaiseDomainEvent(new LockAcquiredEvent(id, siteId, resource.Value, scope, holderId, resourceLock.ExpiresUtc));
            return resourceLock;
        }

        /// <summary>Whether the lease lapsed at <paramref name="nowUtc"/> without a release first.</summary>
        public bool IsExpired(DateTime nowUtc) => !Released && Utc(nowUtc) >= ExpiresUtc;

        /// <summary>Whether the lock is held at <paramref name="nowUtc"/>: neither released nor lapsed.</summary>
        public bool IsHeld(DateTime nowUtc) => !Released && Utc(nowUtc) < ExpiresUtc;

        /// <summary>
        /// Extends the lease from <paramref name="nowUtc"/> - by the current length, or by <paramref name="lease"/> when the
        /// renewal asks for another. Holder-only; a lapsed lease cannot be revived.
        /// </summary>
        public void Renew(int holderId, DateTime nowUtc, TimeSpan? lease = null)
        {
            EnsureLive(nowUtc);
            EnsureHolder(holderId, "renew");
            if (lease is { } requested)
            {
                LeaseSeconds = LeaseSeconds_(requested);
            }

            ExpiresUtc = Utc(nowUtc).AddSeconds(LeaseSeconds);
        }

        /// <summary>Releases the lock (holder-only) and raises <see cref="LockReleasedEvent"/>.</summary>
        public void Release(int holderId)
        {
            EnsureNotReleased();
            EnsureHolder(holderId, "release");
            Released = true;
            RaiseDomainEvent(new LockReleasedEvent(Id, SiteId, Resource.Value, Scope, HolderUserId));
        }

        /// <summary>
        /// <b>ADDED (D114):</b> an administrator releases another user's live lock - the take-over the decision allows. It is
        /// recorded (<see cref="ReleasedByUserId"/>) and raises <see cref="LockReleasedEvent"/> like any release.
        /// </summary>
        public void ReleaseByAdministrator(int administratorId)
        {
            EnsureNotReleased();
            if (administratorId <= 0)
            {
                throw new ArgumentException("A take-over names the administrator.", nameof(administratorId));
            }

            Released = true;
            ReleasedByUserId = administratorId;
            RaiseDomainEvent(new LockReleasedEvent(Id, SiteId, Resource.Value, Scope, HolderUserId));
        }

        /// <summary>Marks a lapsed lease released and raises <see cref="LockReleasedEvent"/> - refused while it is live.</summary>
        public void Expire(DateTime nowUtc)
        {
            EnsureNotReleased();
            if (!IsExpired(nowUtc))
            {
                throw new CollaborationConflictException($"Lock '{Id}' on '{Resource}' has not lapsed; its lease runs until {ExpiresUtc:O}.");
            }

            Released = true;
            RaiseDomainEvent(new LockReleasedEvent(Id, SiteId, Resource.Value, Scope, HolderUserId));
        }

        private void EnsureLive(DateTime nowUtc)
        {
            EnsureNotReleased();
            if (IsExpired(nowUtc))
            {
                throw new CollaborationConflictException($"Lock '{Id}' on '{Resource}' lapsed at {ExpiresUtc:O} and is treated as released.");
            }
        }

        private void EnsureNotReleased()
        {
            if (Released)
            {
                throw new CollaborationConflictException($"Lock '{Id}' on '{Resource}' was already released.");
            }
        }

        private void EnsureHolder(int holderId, string action)
        {
            if (holderId != HolderUserId)
            {
                throw new CollaborationConflictException($"Only the holder may {action} lock '{Id}' on '{Resource}'; user {holderId} is not the holder.");
            }
        }

        // the predecessor platform's Lease value object, as the one number the lease is: its length, positive and bounded.
        private static int LeaseSeconds_(TimeSpan lease)
        {
            if (lease <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lease), "A lease duration must be positive.");
            }

            if (lease > MaxLease)
            {
                throw new ArgumentOutOfRangeException(nameof(lease), $"A lease is at most {MaxLease.TotalMinutes:0} minutes; the editor renews it.");
            }

            return Math.Max(1, (int)Math.Ceiling(lease.TotalSeconds));
        }

        internal static string Name(string? displayName)
        {
            var clean = (displayName ?? string.Empty).Trim();
            return clean.Length == 0 ? "?" : clean.Length <= 100 ? clean : clean[..100];
        }

        internal static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }

    public enum PresenceChangeKind
    {
        Joined = 0,
        Moved = 1,
        Left = 2,
    }

    public enum EditSessionStatus
    {
        /// <summary>Participants can join, move and broadcast changes.</summary>
        Open = 0,

        /// <summary>Ended: every presence released; nothing more is accepted.</summary>
        Closed = 1,
    }

    public sealed record PresenceChangedEvent(Guid SessionId, int SiteId, string Resource, int UserId, PresenceChangeKind Kind) : DomainEvent;

    public sealed record ChangeBroadcastEvent(Guid SessionId, int SiteId, string Resource, int AuthorUserId, string Descriptor, DateTime AtUtc) : DomainEvent;

    /// <summary>
    /// A live editing session over ONE resource (the predecessor platform's <c>EditSession</c>): who is present, where they are, and the
    /// stream of changes that tells the others to reload. Closing it releases every presence. Whether a change may be
    /// broadcast also needs the author to hold a live lock - the service's cross-aggregate check; the session requires a
    /// joined participant. <b>ADDED:</b> the change stream keeps the last <see cref="MaxChanges"/> entries (the predecessor platform's grew
    /// without bound inside one row).
    /// </summary>
    public sealed class EditSessionAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaxChanges = 200;

        public const int MaxDescriptorLength = 500;

        private readonly List<SessionParticipant> _participants = new();
        private readonly List<SessionChange> _changes = new();

        // Materialisation constructor. Not for application use.
        private EditSessionAggregate()
        {
            TenantId = null!;
            Resource = null!;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public ResourceRef Resource { get; private set; }

        public DateTime StartedUtc { get; private set; }

        public EditSessionStatus Status { get; private set; }

        public IReadOnlyList<SessionParticipant> Participants => _participants;

        public IReadOnlyList<SessionChange> Changes => _changes;

        public static EditSessionAggregate Open(Guid id, TenantId tenantId, int siteId, ResourceRef resource, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(resource);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A session belongs to a site.");
            }

            return new EditSessionAggregate { Id = id, TenantId = tenantId, SiteId = siteId, Resource = resource, StartedUtc = ResourceLockAggregate.Utc(nowUtc), Status = EditSessionStatus.Open };
        }

        /// <summary>Adds a participant; a user already present is refused, and so is a closed session.</summary>
        public void Join(int userId, string displayName, DateTime nowUtc)
        {
            EnsureOpen();
            if (Find(userId) is not null)
            {
                throw new CollaborationConflictException($"User {userId} is already a participant of session '{Id}'.");
            }

            _participants.Add(SessionParticipant.Join(userId, displayName, nowUtc));
            RaiseDomainEvent(new PresenceChangedEvent(Id, SiteId, Resource.Value, userId, PresenceChangeKind.Joined));
        }

        /// <summary>
        /// Where the participant is editing (the element, a cursor); they must be present in an open session. Moving is also
        /// being seen, when the instant is given.
        /// </summary>
        public void UpdatePresence(int userId, string? activeRegion, string? cursorPosition, DateTime? nowUtc = null)
        {
            EnsureOpen();
            var participant = Get(userId);
            participant.Move(activeRegion, cursorPosition);
            if (nowUtc is { } now)
            {
                participant.Seen(now);
            }

            RaiseDomainEvent(new PresenceChangedEvent(Id, SiteId, Resource.Value, userId, PresenceChangeKind.Moved));
        }

        /// <summary>
        /// <b>ADDED (increment 12):</b> the participant's heartbeat - still here. Raises nothing: the others are told of a
        /// presence that CHANGES, not of one that stays. the predecessor platform released a crashed editor's presence when its hub connection
        /// dropped; here the editor talks through the API, so a presence lives on its heartbeat and the sweep forgets one not
        /// heard from (<see cref="ForgetAbsent"/>).
        /// </summary>
        public void Touch(int userId, DateTime nowUtc)
        {
            EnsureOpen();
            Get(userId).Seen(nowUtc);
        }

        /// <summary>
        /// <b>ADDED (increment 12):</b> removes every participant last seen before <paramref name="cutoffUtc"/> - a browser that
        /// crashed or lost its network, which never said it left - with a Left for each; a session they leave empty closes.
        /// Returns who was removed; a closed session has nobody to remove.
        /// </summary>
        public IReadOnlyList<int> ForgetAbsent(DateTime cutoffUtc)
        {
            if (Status != EditSessionStatus.Open)
            {
                return Array.Empty<int>();
            }

            var cutoff = ResourceLockAggregate.Utc(cutoffUtc);
            var absent = _participants.Where(participant => participant.LastSeenUtc < cutoff).ToList();
            foreach (var participant in absent)
            {
                _participants.Remove(participant);
                RaiseDomainEvent(new PresenceChangedEvent(Id, SiteId, Resource.Value, participant.UserId, PresenceChangeKind.Left));
            }

            if (absent.Count > 0 && _participants.Count == 0)
            {
                Status = EditSessionStatus.Closed;
            }

            return absent.Select(participant => participant.UserId).ToList();
        }

        public void Leave(int userId)
        {
            EnsureOpen();
            _participants.Remove(Get(userId));
            RaiseDomainEvent(new PresenceChangedEvent(Id, SiteId, Resource.Value, userId, PresenceChangeKind.Left));
        }

        /// <summary>Appends a change by a present participant; the oldest falls off past <see cref="MaxChanges"/>.</summary>
        public void BroadcastChange(int authorId, string descriptor, DateTime nowUtc)
        {
            EnsureOpen();
            _ = Get(authorId);
            var change = SessionChange.Of(authorId, nowUtc, descriptor);
            _changes.Add(change);
            if (_changes.Count > MaxChanges)
            {
                _changes.RemoveRange(0, _changes.Count - MaxChanges);
            }

            RaiseDomainEvent(new ChangeBroadcastEvent(Id, SiteId, Resource.Value, authorId, change.Descriptor, change.AtUtc));
        }

        /// <summary>Ends the session: a Left for each participant, the roster cleared. A closed session cannot close again.</summary>
        public void Close()
        {
            EnsureOpen();
            foreach (var participant in _participants)
            {
                RaiseDomainEvent(new PresenceChangedEvent(Id, SiteId, Resource.Value, participant.UserId, PresenceChangeKind.Left));
            }

            _participants.Clear();
            Status = EditSessionStatus.Closed;
        }

        /// <summary>
        /// <b>ADDED (D112):</b> removes a user from the session and from its change stream - the erasure of their presence.
        /// Works on a closed session too; returns whether anything of theirs was there.
        /// </summary>
        public bool Forget(int userId)
        {
            var removed = _participants.RemoveAll(participant => participant.UserId == userId) + _changes.RemoveAll(change => change.AuthorUserId == userId);
            return removed > 0;
        }

        private void EnsureOpen()
        {
            if (Status != EditSessionStatus.Open)
            {
                throw new CollaborationConflictException($"Edit session '{Id}' is closed.");
            }
        }

        private SessionParticipant? Find(int userId) => _participants.FirstOrDefault(participant => participant.UserId == userId);

        private SessionParticipant Get(int userId) =>
            Find(userId) ?? throw new CollaborationConflictException($"User {userId} is not a participant of session '{Id}'.");
    }

    /// <summary>A user present in a session, and where: the element they are editing and a cursor position.</summary>
    public sealed class SessionParticipant
    {
        // Materialisation constructor. Not for application use.
        private SessionParticipant()
        {
            DisplayName = string.Empty;
        }

        public int UserId { get; private set; }

        public string DisplayName { get; private set; }

        public DateTime JoinedUtc { get; private set; }

        /// <summary>When the participant was last heard from: joining, moving, their heartbeat.</summary>
        public DateTime LastSeenUtc { get; private set; }

        public string? ActiveRegion { get; private set; }

        public string? CursorPosition { get; private set; }

        internal static SessionParticipant Join(int userId, string displayName, DateTime nowUtc)
        {
            if (userId <= 0)
            {
                throw new ArgumentException("A participant requires a user id.", nameof(userId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            var now = ResourceLockAggregate.Utc(nowUtc);
            return new SessionParticipant { UserId = userId, DisplayName = ResourceLockAggregate.Name(displayName), JoinedUtc = now, LastSeenUtc = now };
        }

        internal void Seen(DateTime nowUtc)
        {
            var now = ResourceLockAggregate.Utc(nowUtc);
            if (now > LastSeenUtc)
            {
                LastSeenUtc = now;
            }
        }

        internal void Move(string? activeRegion, string? cursorPosition)
        {
            ActiveRegion = Bound(activeRegion);
            CursorPosition = Bound(cursorPosition);
        }

        private static string? Bound(string? value)
        {
            var clean = value?.Trim();
            return string.IsNullOrEmpty(clean) ? null : clean.Length <= 200 ? clean : clean[..200];
        }
    }

    /// <summary>One broadcast change: who, when, and a short descriptor (<c>step:3 renamed</c>) - never the content.</summary>
    public sealed class SessionChange
    {
        // Materialisation constructor. Not for application use.
        private SessionChange()
        {
            Descriptor = string.Empty;
        }

        public int AuthorUserId { get; private set; }

        public DateTime AtUtc { get; private set; }

        public string Descriptor { get; private set; }

        internal static SessionChange Of(int authorId, DateTime nowUtc, string descriptor)
        {
            if (authorId <= 0)
            {
                throw new ArgumentException("A change entry requires an author.", nameof(authorId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
            var clean = descriptor.Trim();
            if (clean.Length > EditSessionAggregate.MaxDescriptorLength)
            {
                throw new ArgumentException($"A change descriptor is at most {EditSessionAggregate.MaxDescriptorLength} characters.", nameof(descriptor));
            }

            return new SessionChange { AuthorUserId = authorId, AtUtc = ResourceLockAggregate.Utc(nowUtc), Descriptor = clean };
        }
    }
}
