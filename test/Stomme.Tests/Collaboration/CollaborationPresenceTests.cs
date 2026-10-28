#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// BL-97 increment 12 - presence lives on a heartbeat. Found driving two browsers: a host restart killed one editor's
    /// circuit, it never said it left, and it stayed a participant - so its session could never close. the predecessor platform released a
    /// dropped editor from its hub's disconnect; here the editor talks through the API, so a participant carries when it was
    /// last heard from, its heartbeat tells nobody, and the sweep forgets one silent past the presence window, closing a
    /// session it leaves empty. Mutation-checked: a sweep that ignores the cutoff, and a heartbeat that does not touch.
    /// </summary>
    public sealed class CollaborationPresenceTests
    {
        private static readonly ResourceRef Board = ResourceRef.Of("dashboard", Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"));

        [Fact]
        public void A_participant_is_seen_when_joining_moving_and_on_its_heartbeat_and_the_heartbeat_tells_nobody()
        {
            var start = CollaborationRig.Start;
            var session = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, CollaborationRig.Site, Board, start);
            session.Join(CollaborationRig.Ada, "Ada", start);
            Assert.Equal(start, session.Participants.Single().LastSeenUtc);

            session.UpdatePresence(CollaborationRig.Ada, "widget:1", null, start.AddSeconds(20));
            Assert.Equal(start.AddSeconds(20), session.Participants.Single().LastSeenUtc);

            session.ClearDomainEvents();
            session.Touch(CollaborationRig.Ada, start.AddSeconds(50));
            Assert.Equal(start.AddSeconds(50), session.Participants.Single().LastSeenUtc);
            Assert.Empty(session.DomainEvents);
            Assert.Throws<CollaborationConflictException>(() => session.Touch(CollaborationRig.Bo, start.AddSeconds(50)));
        }

        [Fact]
        public void Forgetting_the_absent_removes_only_the_silent_with_a_Left_each_and_closes_an_emptied_session()
        {
            var start = CollaborationRig.Start;
            var session = EditSessionAggregate.Open(Guid.NewGuid(), TenantId.Default, CollaborationRig.Site, Board, start);
            session.Join(CollaborationRig.Ada, "Ada", start);
            session.Join(CollaborationRig.Bo, "Bo", start);
            session.Touch(CollaborationRig.Bo, start.AddMinutes(2));
            session.ClearDomainEvents();

            Assert.Equal(new[] { CollaborationRig.Ada }, session.ForgetAbsent(start.AddMinutes(1)));
            Assert.Equal(new[] { CollaborationRig.Bo }, session.Participants.Select(participant => participant.UserId));
            Assert.Equal(PresenceChangeKind.Left, Assert.IsType<PresenceChangedEvent>(Assert.Single(session.DomainEvents)).Kind);
            Assert.Equal(EditSessionStatus.Open, session.Status);

            Assert.Equal(new[] { CollaborationRig.Bo }, session.ForgetAbsent(start.AddMinutes(5)));
            Assert.Equal(EditSessionStatus.Closed, session.Status);
            Assert.Empty(session.ForgetAbsent(start.AddMinutes(9))); // a closed session has nobody to forget
        }

        [Fact]
        public async Task The_sweep_forgets_an_editor_whose_browser_went_silent_and_keeps_the_one_still_beating()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(CollaborationRig.Bo, "Bo").SessionService.JoinAsync(Board);
            var sweeper = new PresenceSweeper(rig.Sessions, rig.Options, rig.Clock, rig.UnitOfWork);

            // Within the window nobody is forgotten.
            rig.Clock.Now = CollaborationRig.Start.AddSeconds(rig.Options.PresenceSeconds - 1);
            Assert.Equal(0, await sweeper.SweepSiteAsync(CollaborationRig.Site));

            // Bo's browser keeps beating; Ada's crashed.
            rig.Clock.Now = CollaborationRig.Start.AddSeconds(90);
            await rig.As(CollaborationRig.Bo, "Bo").SessionService.HeartbeatAsync(session.Id);
            rig.Clock.Now = CollaborationRig.Start.AddSeconds(rig.Options.PresenceSeconds + 30);
            Assert.Equal(1, await sweeper.SweepSiteAsync(CollaborationRig.Site));

            var stored = (await rig.Sessions.GetByIdAsync(session.Id))!;
            Assert.Equal(new[] { CollaborationRig.Bo }, stored.Participants.Select(participant => participant.UserId));
            Assert.Equal(EditSessionStatus.Open, stored.Status);

            // Bo goes silent too: the session closes, and the next editor opens a new one.
            rig.Clock.Now = CollaborationRig.Start.AddSeconds(90 + rig.Options.PresenceSeconds + 1);
            Assert.Equal(1, await sweeper.SweepSiteAsync(CollaborationRig.Site));
            Assert.Equal(EditSessionStatus.Closed, (await rig.Sessions.GetByIdAsync(session.Id))!.Status);
            Assert.NotEqual(session.Id, (await rig.As(CollaborationRig.Cy, "Cy").SessionService.JoinAsync(Board)).Id);
        }

        [Fact]
        public async Task A_reconnecting_editor_is_seen_again_by_rejoining_and_only_a_participant_may_beat()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);

            rig.Clock.Now = CollaborationRig.Start.AddMinutes(1);
            await rig.SessionService.JoinAsync(Board);
            Assert.Equal(CollaborationRig.Start.AddMinutes(1), (await rig.Sessions.GetByIdAsync(session.Id))!.Participants.Single().LastSeenUtc);

            var refused = await Assert.ThrowsAsync<CollaborationRefusedException>(() => rig.As(CollaborationRig.Bo, "Bo").SessionService.HeartbeatAsync(session.Id));
            Assert.Equal(CollaborationRefusal.NotParticipant, refused.Reason);
        }
    }
}
