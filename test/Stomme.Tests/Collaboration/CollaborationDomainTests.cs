#nullable enable
using System;
using System.Linq;
using Stomme.Collaboration;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// The <c>ResourceRefTests</c>, <c>ResourceLockTests</c> and <c>EditSessionTests</c> cases, by name.
    /// <b>Note:</b> a resource id is a string (a pipeline's is the engine's definition id), a user an int, a lock and a
    /// session belong to a site, and a refusal is a <see cref="CollaborationConflictException"/> (a bare
    /// InvalidOperationException) or an <see cref="InvalidResourceRefException"/> for a malformed reference. Also covered: the
    /// administrator's take-over, the lease's upper bound, the change stream's bound, and the erasure of a user's presence.
    /// </summary>
    public sealed class CollaborationDomainTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TenantId Tenant = TenantId.Default;
        private static readonly ResourceRef Board = ResourceRef.Of("dashboard", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
        private const int Site = 3;
        private const int Ada = 11;
        private const int Bo = 12;

        private static ResourceLockAggregate Lock(TimeSpan? lease = null) =>
            ResourceLockAggregate.Acquire(Guid.NewGuid(), Tenant, Site, Board, LockScope.Resource, Ada, "Ada", lease ?? TimeSpan.FromMinutes(2), Now);

        private static EditSessionAggregate Session() => EditSessionAggregate.Open(Guid.NewGuid(), Tenant, Site, Board, Now);

        // ---- ResourceRefTests

        [Fact]
        public void Of_CreatesTheReference_AndTrimsTheTypeToken()
        {
            var reference = ResourceRef.Of("  pipeline ", " def-1 ");

            Assert.Equal(("pipeline", "def-1", "pipeline/def-1"), (reference.ResourceType, reference.ResourceId, reference.Value));
            Assert.Equal(reference, ResourceRef.Create(reference.Value));
        }

        [Theory]
        [InlineData("Pipeline")]
        [InlineData("pipe line")]
        [InlineData("1pipeline")]
        [InlineData("pipe_line")]
        public void Of_RejectsANonLowercaseToken(string type) =>
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of(type, "x"));

        [Fact]
        public void Of_AllowsDigitsAndHyphensAfterTheLeadingLetter() =>
            Assert.Equal("landing-page2", ResourceRef.Of("landing-page2", "x").ResourceType);

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Of_RejectsABlankType(string type) =>
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of(type, "x"));

        [Fact]
        public void Of_RejectsAnEmptyResourceId()
        {
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of("dashboard", Guid.Empty));
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of("dashboard", "  "));

            // An id is a path segment of the scalar - no slash, no whitespace, bounded.
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of("dashboard", "a/b"));
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of("dashboard", "a b"));
            Assert.Throws<InvalidResourceRefException>(() => ResourceRef.Of("dashboard", new string('x', ResourceRef.MaxIdLength + 1)));
        }

        [Fact]
        public void References_UseValueEquality()
        {
            var id = Guid.NewGuid();
            Assert.Equal(ResourceRef.Of("dashboard", id), ResourceRef.Of("dashboard", id));
            Assert.NotEqual(ResourceRef.Of("dashboard", id), ResourceRef.Of("pipeline", id));
        }

        // ---- ResourceLockTests

        [Fact]
        public void Acquire_SetsTheLease_AndRaisesLockAcquired()
        {
            var resourceLock = Lock();

            Assert.Equal((Site, Ada, "Ada", 120, Now, Now.AddMinutes(2), false), (resourceLock.SiteId, resourceLock.HolderUserId, resourceLock.HolderDisplayName, resourceLock.LeaseSeconds, resourceLock.AcquiredUtc, resourceLock.ExpiresUtc, resourceLock.Released));
            var acquired = Assert.IsType<LockAcquiredEvent>(Assert.Single(resourceLock.DomainEvents));
            Assert.Equal((Board.Value, Ada, Now.AddMinutes(2)), (acquired.Resource, acquired.HolderUserId, acquired.ExpiresUtc));
        }

        [Fact]
        public void Acquire_RequiresAHolder() =>
            Assert.Throws<ArgumentException>(() => ResourceLockAggregate.Acquire(Guid.NewGuid(), Tenant, Site, Board, LockScope.Resource, 0, "nobody", TimeSpan.FromMinutes(1), Now));

        [Fact]
        public void Lease_RequiresAPositiveDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Lock(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => Lock(TimeSpan.FromSeconds(-1)));

            // A lease is bounded too - it is renewed, never parked for a day.
            Assert.Throws<ArgumentOutOfRangeException>(() => Lock(ResourceLockAggregate.MaxLease + TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void Lease_CarriesTheAutoRenewPolicy()
        {
            // The lease is its length; renewing is the editor's heartbeat, each renewal extending it that far again.
            var resourceLock = Lock(TimeSpan.FromSeconds(90));
            resourceLock.Renew(Ada, Now.AddSeconds(60));

            Assert.Equal((90, Now.AddSeconds(150)), (resourceLock.LeaseSeconds, resourceLock.ExpiresUtc));
        }

        [Fact]
        public void IsExpired_LapsesExactlyAtTheLeaseEnd()
        {
            var resourceLock = Lock();

            Assert.False(resourceLock.IsExpired(Now.AddMinutes(2).AddTicks(-1)));
            Assert.True(resourceLock.IsHeld(Now.AddMinutes(2).AddTicks(-1)));
            Assert.True(resourceLock.IsExpired(Now.AddMinutes(2)));
            Assert.False(resourceLock.IsHeld(Now.AddMinutes(2)));
        }

        [Fact]
        public void Renew_ExtendsTheLeaseFromTheRenewalInstant()
        {
            var resourceLock = Lock();
            resourceLock.Renew(Ada, Now.AddMinutes(1), TimeSpan.FromMinutes(5));

            Assert.Equal((300, Now.AddMinutes(6)), (resourceLock.LeaseSeconds, resourceLock.ExpiresUtc));
        }

        [Fact]
        public void Renew_IsHolderOnly() =>
            Assert.Throws<CollaborationConflictException>(() => Lock().Renew(Bo, Now.AddSeconds(10)));

        [Fact]
        public void Renew_CannotReviveALapsedLease() =>
            Assert.Throws<CollaborationConflictException>(() => Lock().Renew(Ada, Now.AddMinutes(3)));

        [Fact]
        public void Release_ByTheHolder_RaisesLockReleased()
        {
            var resourceLock = Lock();
            resourceLock.ClearDomainEvents();

            resourceLock.Release(Ada);

            Assert.True(resourceLock.Released);
            Assert.Equal(Ada, Assert.IsType<LockReleasedEvent>(Assert.Single(resourceLock.DomainEvents)).HolderUserId);
        }

        [Fact]
        public void Release_IsHolderOnly() =>
            Assert.Throws<CollaborationConflictException>(() => Lock().Release(Bo));

        [Fact]
        public void Release_Twice_IsRejected()
        {
            var resourceLock = Lock();
            resourceLock.Release(Ada);

            Assert.Throws<CollaborationConflictException>(() => resourceLock.Release(Ada));
        }

        [Fact]
        public void Expire_AfterTheLeaseLapses_RaisesLockReleased()
        {
            var resourceLock = Lock();
            resourceLock.ClearDomainEvents();

            resourceLock.Expire(Now.AddMinutes(2));

            Assert.True(resourceLock.Released);
            Assert.IsType<LockReleasedEvent>(Assert.Single(resourceLock.DomainEvents));
        }

        [Fact]
        public void Expire_WhileTheLeaseIsLive_IsRejected() =>
            Assert.Throws<CollaborationConflictException>(() => Lock().Expire(Now.AddMinutes(1)));

        [Fact]
        public void ReleasedLocks_ReportNeitherHeldNorExpired()
        {
            var resourceLock = Lock();
            resourceLock.Release(Ada);

            Assert.False(resourceLock.IsHeld(Now));
            Assert.False(resourceLock.IsExpired(Now.AddHours(1)));
        }

        [Fact]
        public void An_administrator_takes_a_live_lock_over_and_the_take_over_is_recorded()
        {
            var resourceLock = Lock();
            resourceLock.ClearDomainEvents();

            resourceLock.ReleaseByAdministrator(Bo);

            Assert.Equal((true, (int?)Bo), (resourceLock.Released, resourceLock.ReleasedByUserId));
            Assert.Equal(Ada, Assert.IsType<LockReleasedEvent>(Assert.Single(resourceLock.DomainEvents)).HolderUserId);
            Assert.Throws<CollaborationConflictException>(() => resourceLock.ReleaseByAdministrator(Bo));
        }

        // ---- EditSessionTests

        [Fact]
        public void Open_TargetsExactlyOneResource_AndStartsOpen()
        {
            var session = Session();

            Assert.Equal((Board, Site, EditSessionStatus.Open, Now), (session.Resource, session.SiteId, session.Status, session.StartedUtc));
            Assert.Empty(session.Participants);
            Assert.Empty(session.Changes);
        }

        [Fact]
        public void Join_AddsTheParticipant_AndRaisesPresenceChanged()
        {
            var session = Session();

            session.Join(Ada, " Ada ", Now);

            var participant = Assert.Single(session.Participants);
            Assert.Equal((Ada, "Ada", Now), (participant.UserId, participant.DisplayName, participant.JoinedUtc));
            Assert.Equal(PresenceChangeKind.Joined, Assert.IsType<PresenceChangedEvent>(Assert.Single(session.DomainEvents)).Kind);
        }

        [Fact]
        public void Join_RejectsADuplicateParticipant()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);

            Assert.Throws<CollaborationConflictException>(() => session.Join(Ada, "Ada", Now));
        }

        [Fact]
        public void UpdatePresence_MovesTheParticipant_AndRaisesPresenceChanged()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.ClearDomainEvents();

            session.UpdatePresence(Ada, " widget:3 ", "12");

            Assert.Equal(("widget:3", "12"), (session.Participants[0].ActiveRegion, session.Participants[0].CursorPosition));
            Assert.Equal(PresenceChangeKind.Moved, Assert.IsType<PresenceChangedEvent>(Assert.Single(session.DomainEvents)).Kind);
        }

        [Fact]
        public void UpdatePresence_RequiresTheUserToBeAParticipant() =>
            Assert.Throws<CollaborationConflictException>(() => Session().UpdatePresence(Ada, "x", null));

        [Fact]
        public void Leave_RemovesTheParticipant_AndRaisesPresenceChanged()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.ClearDomainEvents();

            session.Leave(Ada);

            Assert.Empty(session.Participants);
            Assert.Equal(PresenceChangeKind.Left, Assert.IsType<PresenceChangedEvent>(Assert.Single(session.DomainEvents)).Kind);
        }

        [Fact]
        public void Leave_RequiresTheUserToBeAParticipant() =>
            Assert.Throws<CollaborationConflictException>(() => Session().Leave(Ada));

        [Fact]
        public void BroadcastChange_AppendsTheEntry_AndRaisesChangeBroadcast()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.ClearDomainEvents();

            session.BroadcastChange(Ada, " widget:3 moved ", Now.AddSeconds(5));

            var change = Assert.Single(session.Changes);
            Assert.Equal((Ada, Now.AddSeconds(5), "widget:3 moved"), (change.AuthorUserId, change.AtUtc, change.Descriptor));
            Assert.Equal("widget:3 moved", Assert.IsType<ChangeBroadcastEvent>(Assert.Single(session.DomainEvents)).Descriptor);
        }

        [Fact]
        public void BroadcastChange_RequiresTheAuthorToBeAParticipant() =>
            Assert.Throws<CollaborationConflictException>(() => Session().BroadcastChange(Ada, "x", Now));

        [Fact]
        public void BroadcastChange_RejectsABlankDescriptor()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);

            Assert.ThrowsAny<ArgumentException>(() => session.BroadcastChange(Ada, "  ", Now));
            Assert.ThrowsAny<ArgumentException>(() => session.BroadcastChange(Ada, new string('x', EditSessionAggregate.MaxDescriptorLength + 1), Now));
        }

        [Fact]
        public void Close_ReleasesPresenceForAllParticipants()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.Join(Bo, "Bo", Now);
            session.ClearDomainEvents();

            session.Close();

            Assert.Equal(EditSessionStatus.Closed, session.Status);
            Assert.Empty(session.Participants);
            Assert.Equal(new[] { Ada, Bo }, session.DomainEvents.OfType<PresenceChangedEvent>().Where(e => e.Kind == PresenceChangeKind.Left).Select(e => e.UserId));
        }

        [Fact]
        public void AClosedSession_RejectsAllActivity()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.Close();

            Assert.Throws<CollaborationConflictException>(() => session.Join(Bo, "Bo", Now));
            Assert.Throws<CollaborationConflictException>(() => session.UpdatePresence(Ada, "x", null));
            Assert.Throws<CollaborationConflictException>(() => session.Leave(Ada));
            Assert.Throws<CollaborationConflictException>(() => session.BroadcastChange(Ada, "x", Now));
            Assert.Throws<CollaborationConflictException>(() => session.Close());
        }

        [Fact]
        public void The_change_stream_keeps_its_last_entries_and_a_user_can_be_forgotten()
        {
            var session = Session();
            session.Join(Ada, "Ada", Now);
            session.Join(Bo, "Bo", Now);
            for (var index = 0; index < EditSessionAggregate.MaxChanges + 5; index++)
            {
                session.BroadcastChange(index % 2 == 0 ? Ada : Bo, "change " + index, Now.AddSeconds(index));
            }

            Assert.Equal(EditSessionAggregate.MaxChanges, session.Changes.Count);
            Assert.Equal("change 5", session.Changes[0].Descriptor);

            Assert.True(session.Forget(Ada));
            Assert.Equal(Bo, Assert.Single(session.Participants).UserId);
            Assert.All(session.Changes, change => Assert.Equal(Bo, change.AuthorUserId));
            Assert.False(session.Forget(Ada));
        }
    }
}
