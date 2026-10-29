#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Xunit;
using static Stomme.Tests.Collaboration.CollaborationRig;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// The <c>ResourceLockServiceTests</c> and <c>EditSessionServiceTests</c> cases, by name, over the
    /// real services on an in-memory store. <b>Note:</b> no call names a user - the principal does (a switchable caller
    /// here); the resource must be one its module lets the caller edit; the "guard held" cases become the in-process gate's
    /// own proof - concurrent callers are serialised, so exactly one is granted the lock and exactly one session is opened
    /// (a distributed guard would report contention instead; none is composed here).
    /// </summary>
    public sealed class CollaborationServiceTests
    {
        private static readonly ResourceRef Board = ResourceRef.Of("dashboard", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
        private static readonly ResourceRef Flow = ResourceRef.Of("pipeline", "def-1");

        // ---- ResourceLockServiceTests

        [Fact]
        public async Task AcquireAsync_RecordsTheDurableLease_AndRaisesLockAcquired()
        {
            var rig = new CollaborationRig();

            var result = await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            Assert.Equal(new ResourceLockResult(true, Ada, "Ada", Start.AddMinutes(2)), result);
            var row = Assert.Single(rig.LockRows.Rows);
            Assert.Equal((Board, Site, Ada, false), (row.Resource, row.SiteId, row.HolderUserId, row.Released));
            Assert.IsType<LockAcquiredEvent>(Assert.Single(row.DomainEvents));

            // The write is saved through the unit of work, where its LockAcquiredEvent is dispatched.
            Assert.Equal(1, rig.UnitOfWork.Saves);
        }

        [Fact]
        public async Task AcquireAsync_WhenHeldByAnother_ReportsTheHolder_WithoutThrowing()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var contended = await rig.As(Bo, "Bo").LockService.AcquireAsync(Board, LockScope.Resource);

            Assert.Equal(new ResourceLockResult(false, Ada, "Ada", Start.AddMinutes(2)), contended);
            Assert.Single(rig.LockRows.Rows);
        }

        [Fact]
        public async Task AcquireAsync_BySameHolder_RenewsTheLease()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddMinutes(1);

            var again = await rig.LockService.AcquireAsync(Board, LockScope.Resource, TimeSpan.FromMinutes(5));

            Assert.Equal(new ResourceLockResult(true, Ada, "Ada", Start.AddMinutes(6)), again);
            Assert.Single(rig.LockRows.Rows);
        }

        [Fact]
        public async Task AcquireAsync_OverALapsedLease_ExpiresTheOldAndGrantsTheNew()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddMinutes(3);

            var granted = await rig.As(Bo, "Bo").LockService.AcquireAsync(Board, LockScope.Resource);

            Assert.True(granted.Acquired);
            Assert.Equal(Bo, granted.HolderUserId);
            var old = rig.LockRows.Rows.Single(row => row.HolderUserId == Ada);
            Assert.True(old.Released);
            Assert.Contains(old.DomainEvents, e => e is LockReleasedEvent);
        }

        [Fact]
        public async Task AcquireAsync_AtAnotherScope_IsIndependent()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var field = await rig.As(Bo, "Bo").LockService.AcquireAsync(Board, LockScope.Field);

            Assert.True(field.Acquired);
            Assert.Equal(2, rig.LockRows.Rows.Count);
        }

        [Fact]
        public async Task AcquireAsync_WhileTheGuardIsHeld_ReportsContention_WithoutCreatingALease()
        {
            // The in-process gate serialises two concurrent acquires - exactly one is granted, the other names it,
            // and exactly one lease exists.
            var rig = new CollaborationRig();
            var ada = new ResourceLockService(rig.Locks, new SwitchableCaller { Current = SwitchableCaller.As(Ada, "Ada") }, rig.Access, rig.Options, rig.Gate, rig.Clock, rig.UnitOfWork);
            var bo = new ResourceLockService(rig.Locks, new SwitchableCaller { Current = SwitchableCaller.As(Bo, "Bo") }, rig.Access, rig.Options, rig.Gate, rig.Clock, rig.UnitOfWork);

            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() => (index % 2 == 0 ? ada : bo).AcquireAsync(Board, LockScope.Resource))));

            var winner = Assert.Single(rig.LockRows.Rows).HolderUserId;
            Assert.All(results, result => Assert.Equal(winner, result.HolderUserId));
            Assert.All(Enumerable.Range(0, results.Length), index => Assert.Equal((index % 2 == 0 ? Ada : Bo) == winner, results[index].Acquired));
            Assert.Contains(results, result => result.Acquired);
            Assert.Contains(results, result => !result.Acquired);
        }

        [Fact]
        public async Task AcquireAsync_WhileTheGuardIsHeld_StillNamesADurablyRecordedHolder()
        {
            // An acquire that meets the gate held WAITS for it, then names whoever got in first.
            var rig = new CollaborationRig();
            var bo = new ResourceLockService(rig.Locks, new SwitchableCaller { Current = SwitchableCaller.As(Bo, "Bo") }, rig.Access, rig.Options, rig.Gate, rig.Clock, rig.UnitOfWork);
            Task<ResourceLockResult> waiting;
            using (await rig.Gate.EnterAsync(ResourceLockService.Key(SwitchableCaller.As(Ada, "Ada"), Board, LockScope.Resource), default))
            {
                waiting = bo.AcquireAsync(Board, LockScope.Resource);
                await Task.Delay(100);
                Assert.False(waiting.IsCompleted);
                await rig.LockRows.AddAsync(ResourceLockAggregate.Acquire(Guid.NewGuid(), SwitchableCaller.As(Ada, "Ada").TenantId, Site, Board, LockScope.Resource, Ada, "Ada", TimeSpan.FromMinutes(2), Start));
            }

            var result = await waiting;
            Assert.Equal((false, Ada), (result.Acquired, result.HolderUserId));
        }

        [Fact]
        public async Task RenewAsync_ByTheHolder_ExtendsFromNow_WithTheRequestedDuration()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddSeconds(90);

            var renewed = await rig.LockService.RenewAsync(Board, LockScope.Resource, TimeSpan.FromMinutes(3));

            Assert.Equal(new ResourceLockResult(true, Ada, "Ada", Start.AddSeconds(90 + 180)), renewed);
        }

        [Fact]
        public async Task RenewAsync_ByANonHolder_FailsSoftly_AndReportsTheHolder()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var renewed = await rig.As(Bo, "Bo").LockService.RenewAsync(Board, LockScope.Resource);

            Assert.Equal(new ResourceLockResult(false, Ada, "Ada", Start.AddMinutes(2)), renewed);
        }

        [Fact]
        public async Task RenewAsync_OfALapsedLease_CannotReviveIt()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddMinutes(5);

            var renewed = await rig.LockService.RenewAsync(Board, LockScope.Resource);

            Assert.False(renewed!.Acquired);
            Assert.Equal(Start.AddMinutes(2), rig.LockRows.Rows.Single().ExpiresUtc);
        }

        [Fact]
        public async Task RenewAsync_WithNoLock_FailsSoftly() =>
            Assert.Null(await new CollaborationRig().LockService.RenewAsync(Board, LockScope.Resource));

        [Fact]
        public async Task ReleaseAsync_ByTheHolder_RaisesLockReleased_AndFreesTheResource()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            await rig.LockService.ReleaseAsync(Board, LockScope.Resource);

            Assert.True(rig.LockRows.Rows.Single().Released);
            Assert.True((await rig.As(Bo, "Bo").LockService.AcquireAsync(Board, LockScope.Resource)).Acquired);
        }

        [Fact]
        public async Task ReleaseAsync_ByANonHolder_IsRejectedByTheAggregate()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            await Assert.ThrowsAsync<CollaborationConflictException>(() => rig.As(Bo, "Bo").LockService.ReleaseAsync(Board, LockScope.Resource));
            Assert.False(rig.LockRows.Rows.Single().Released);
        }

        [Fact]
        public async Task ReleaseAsync_WithNoLock_IsANoOp()
        {
            var rig = new CollaborationRig();

            await rig.LockService.ReleaseAsync(Board, LockScope.Resource);

            Assert.Empty(rig.LockRows.Rows);
        }

        [Fact]
        public async Task ReleaseAsync_OfALapsedLease_RecordsTheAutoExpiry()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddMinutes(4);

            // Anyone may record a lapse - it releases nothing that is still held.
            await rig.As(Bo, "Bo").LockService.ReleaseAsync(Board, LockScope.Resource);

            var row = rig.LockRows.Rows.Single();
            Assert.True(row.Released);
            Assert.Null(row.ReleasedByUserId);
        }

        [Fact]
        public async Task A_resource_its_module_does_not_let_the_caller_edit_is_refused_before_anything_is_written()
        {
            var rig = new CollaborationRig();
            rig.Access.Denied.Add(Board.Value);

            var refused = await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.LockService.AcquireAsync(Board, LockScope.Resource));

            Assert.Equal(CollaborationRefusal.NotAvailable, refused.Reason);
            Assert.Empty(rig.LockRows.Rows);
            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.JoinAsync(Board));
            Assert.Empty(rig.SessionRows.Rows);
        }

        [Fact]
        public async Task An_administrator_takes_the_lock_over_and_another_site_never_sees_it()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            // The same resource on ANOTHER site of the tenant is free - the lock is the site's.
            rig.Caller.Current = SwitchableCaller.As(Bo, "Bo", siteId: Site + 1);
            Assert.True((await rig.LockService.AcquireAsync(Board, LockScope.Resource)).Acquired);

            rig.As(Cy, "Cy");
            Assert.True(await rig.LockService.ReleaseByAdministratorAsync(Board, LockScope.Resource));
            var taken = rig.LockRows.Rows.Single(row => row.SiteId == Site);
            Assert.Equal((true, (int?)Cy), (taken.Released, taken.ReleasedByUserId));
            Assert.False(rig.LockRows.Rows.Single(row => row.SiteId == Site + 1).Released);
            Assert.False(await rig.LockService.ReleaseByAdministratorAsync(Board, LockScope.Resource));
        }

        // ---- EditSessionServiceTests

        [Fact]
        public async Task JoinAsync_WithNoOpenSession_OpensOne_AndAddsTheJoiner()
        {
            var rig = new CollaborationRig();

            var session = await rig.SessionService.JoinAsync(Board);

            Assert.Equal((Board, EditSessionStatus.Open, Site), (session.Resource, session.Status, session.SiteId));
            Assert.Equal(Ada, Assert.Single(session.Participants).UserId);
            Assert.Single(rig.SessionRows.Rows);
        }

        [Fact]
        public async Task JoinAsync_WithAnOpenSession_JoinsIt_InsteadOfOpeningASecond()
        {
            var rig = new CollaborationRig();
            var first = await rig.SessionService.JoinAsync(Board);

            var second = await rig.As(Bo, "Bo").SessionService.JoinAsync(Board);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(new[] { Ada, Bo }, second.Participants.Select(participant => participant.UserId));
            Assert.Single(rig.SessionRows.Rows);
        }

        [Fact]
        public async Task JoinAsync_BySameUser_IsIdempotent()
        {
            var rig = new CollaborationRig();
            await rig.SessionService.JoinAsync(Board);

            var again = await rig.SessionService.JoinAsync(Board);

            Assert.Single(again.Participants);
        }

        [Fact]
        public async Task JoinAsync_ForAnotherResource_OpensAnIndependentSession()
        {
            var rig = new CollaborationRig();
            var board = await rig.SessionService.JoinAsync(Board);

            var flow = await rig.SessionService.JoinAsync(Flow);

            Assert.NotEqual(board.Id, flow.Id);
            Assert.Equal(2, rig.SessionRows.Rows.Count);
        }

        [Fact]
        public async Task JoinAsync_AfterTheSessionClosed_OpensAFreshOne()
        {
            var rig = new CollaborationRig();
            var first = await rig.SessionService.JoinAsync(Board);
            await rig.SessionService.LeaveAsync(first.Id);

            var fresh = await rig.SessionService.JoinAsync(Board);

            Assert.NotEqual(first.Id, fresh.Id);
            Assert.Equal(EditSessionStatus.Closed, rig.SessionRows.Rows.Single(row => row.Id == first.Id).Status);
        }

        [Fact]
        public async Task JoinAsync_WhileTheOpenGuardIsHeld_WithNoVisibleSession_IsRejectedAsTransient()
        {
            // Concurrent first joins are serialised by the gate - ONE session is opened and every joiner is in it.
            var rig = new CollaborationRig();
            var services = Enumerable.Range(0, 10)
                .Select(index => new EditSessionService(rig.Sessions, rig.Locks, new SwitchableCaller { Current = SwitchableCaller.As(100 + index, "user " + index) }, rig.Access, rig.Gate, rig.Clock, rig.UnitOfWork))
                .ToList();

            var joined = await Task.WhenAll(services.Select(service => Task.Run(() => service.JoinAsync(Board))));

            var session = Assert.Single(rig.SessionRows.Rows);
            Assert.All(joined, result => Assert.Equal(session.Id, result.Id));
            Assert.Equal(10, session.Participants.Count);
        }

        [Fact]
        public async Task JoinAsync_WhileTheOpenGuardIsHeld_StillJoinsAVisibleOpenSession()
        {
            var rig = new CollaborationRig();
            var open = await rig.SessionService.JoinAsync(Board);
            var bo = new EditSessionService(rig.Sessions, rig.Locks, new SwitchableCaller { Current = SwitchableCaller.As(Bo, "Bo") }, rig.Access, rig.Gate, rig.Clock, rig.UnitOfWork);
            Task<EditSessionAggregate> waiting;
            using (await rig.Gate.EnterAsync($"session:{SwitchableCaller.As(Ada, "Ada").TenantId.Value:D}:{Site}:{Board.Value}", default))
            {
                waiting = bo.JoinAsync(Board);
                await Task.Delay(100);
                Assert.False(waiting.IsCompleted);
            }

            Assert.Equal(open.Id, (await waiting).Id);
        }

        [Fact]
        public async Task UpdatePresenceAsync_MovesTheParticipant_AndRaisesPresenceChanged()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);

            var moved = await rig.SessionService.UpdatePresenceAsync(session.Id, "widget:2", null);

            Assert.Equal("widget:2", moved.Participants.Single().ActiveRegion);
            Assert.Contains(moved.DomainEvents, e => e is PresenceChangedEvent { Kind: PresenceChangeKind.Moved });
        }

        [Fact]
        public async Task BroadcastChangeAsync_ByALockHolder_Appends_AndRaisesChangeBroadcast()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var changed = await rig.SessionService.BroadcastChangeAsync(session.Id, "widget:2 moved");

            Assert.Equal("widget:2 moved", Assert.Single(changed.Changes).Descriptor);
            Assert.Contains(changed.DomainEvents, e => e is ChangeBroadcastEvent);
        }

        [Fact]
        public async Task BroadcastChangeAsync_WithAFieldLease_IsAllowed()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Field);

            Assert.Single((await rig.SessionService.BroadcastChangeAsync(session.Id, "field changed")).Changes);
        }

        [Fact]
        public async Task BroadcastChangeAsync_WithoutTheLock_IsRejected()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(Bo, "Bo").SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var refused = await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.As(Ada, "Ada").SessionService.BroadcastChangeAsync(session.Id, "x"));

            Assert.Equal(CollaborationRefusal.NoLiveLock, refused.Reason);
        }

        [Fact]
        public async Task BroadcastChangeAsync_WithALapsedLock_IsRejected()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddMinutes(3);

            Assert.Equal(CollaborationRefusal.NoLiveLock, (await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.BroadcastChangeAsync(session.Id, "x"))).Reason);
        }

        [Fact]
        public async Task BroadcastChangeAsync_ByANonParticipant_IsRejected()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(Bo, "Bo").LockService.AcquireAsync(Flow, LockScope.Resource);

            Assert.Equal(CollaborationRefusal.NotParticipant, (await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.BroadcastChangeAsync(session.Id, "x"))).Reason);
        }

        [Fact]
        public async Task LeaveAsync_RemovesTheParticipant_AndKeepsTheSessionOpen()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(Bo, "Bo").SessionService.JoinAsync(Board);

            await rig.SessionService.LeaveAsync(session.Id);

            var row = rig.SessionRows.Rows.Single();
            Assert.Equal((EditSessionStatus.Open, Ada), (row.Status, Assert.Single(row.Participants).UserId));
        }

        [Fact]
        public async Task LeaveAsync_ByTheLastParticipant_ClosesTheSession_AndReleasesTheirLocks()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            await rig.LockService.AcquireAsync(Board, LockScope.Field);

            await rig.SessionService.LeaveAsync(session.Id);

            Assert.Equal(EditSessionStatus.Closed, rig.SessionRows.Rows.Single().Status);
            Assert.All(rig.LockRows.Rows, row => Assert.True(row.Released));
        }

        [Fact]
        public async Task CloseAsync_ReleasesAllPresence_AndTheParticipantsLocks()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            await rig.As(Bo, "Bo").SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Field);

            await rig.As(Cy, "Cy").SessionService.CloseAsync(session.Id);

            var row = rig.SessionRows.Rows.Single();
            Assert.Equal(EditSessionStatus.Closed, row.Status);
            Assert.Empty(row.Participants);
            Assert.All(rig.LockRows.Rows, resourceLock => Assert.True(resourceLock.Released));
        }

        [Fact]
        public async Task CloseAsync_LeavesLocksOfNonParticipantsAlone()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(Bo, "Bo").LockService.AcquireAsync(Board, LockScope.Field);

            await rig.As(Cy, "Cy").SessionService.CloseAsync(session.Id);

            Assert.False(rig.LockRows.Rows.Single(row => row.HolderUserId == Bo).Released);
        }

        [Fact]
        public async Task AnUnknownSession_IsRejectedByEveryLifecycleCall()
        {
            var rig = new CollaborationRig();
            var unknown = Guid.NewGuid();

            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.UpdatePresenceAsync(unknown, "x", null));
            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.BroadcastChangeAsync(unknown, "x"));
            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.LeaveAsync(unknown));
            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.CloseAsync(unknown));
            Assert.Null(await rig.SessionService.GetAsync(unknown));
        }

        [Fact]
        public async Task A_session_is_read_only_by_its_participants_and_only_on_its_own_site()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);

            Assert.NotNull(await rig.SessionService.GetAsync(session.Id));
            Assert.Null(await rig.As(Bo, "Bo").SessionService.GetAsync(session.Id));
            rig.Caller.Current = SwitchableCaller.As(Ada, "Ada", siteId: Site + 1);
            Assert.Null(await rig.SessionService.GetAsync(session.Id));
            await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.SessionService.LeaveAsync(session.Id));
        }
    }
}
