using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Stomme.Collaboration;
using Stomme.Models;
using Stomme.Modules.Collaboration;
using Stomme.Modules.Collaboration.Services;
using Stomme.Shared;
using Stomme.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// The collaboration panel an editor hosts (the cases of <c>CollaborationPanelTests</c>, by what they
    /// proved): it joins the resource's session and takes the free lock; another holder is named and nothing is taken from
    /// them; a notice from this site about this resource re-reads the session and the lock - someone else's SAVE becomes a
    /// warning with Reload, someone else's announced change the last change shown, a lock lost the user is told of - while a
    /// notice about another resource or another site, or the user's own save, is ignored; a refused join or a user without the
    /// key renders nothing and calls nothing more; closing the editor gives the lock up and leaves. On a server circuit the
    /// panel is told over the host's in-process signal, which is what these tests drive.
    /// </summary>
    public sealed class CollaborationPanelTests : BunitContext
    {
        private const int Me = 7;
        private const int Bo = 9;
        private const string Board = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

        private readonly ScriptedCollaborationService _service = new();
        private readonly CollaborationSignal _signal = new();
        private readonly Alias _alias = new() { AliasId = 1, TenantId = 1, SiteId = 1, Name = "localhost" };

        public CollaborationPanelTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = _alias });
            Services.AddSingleton<ICollaborationService>(_service);
            Services.AddSingleton<ICollaborationSignal>(_signal);
        }

        [Fact]
        public void The_panel_joins_the_session_takes_the_free_lock_and_shows_who_is_here()
        {
            _service.Session = Session((Me, "Ada", null), (Bo, "Bo", "step 'Parse'"));

            var cut = Panel();

            cut.WaitForAssertion(() => Assert.Equal("You hold the edit lock.", cut.Find("#collaboration-lock").TextContent.Trim()));
            Assert.Equal(new[] { "join dashboard/" + Board, "acquire" }, _service.Calls.Take(2));
            var presence = cut.FindAll(".collaboration-participant").Select(element => element.TextContent.Trim()).ToList();
            Assert.Equal(new[] { "Ada (you)", "Bo · step 'Parse'" }, presence);
            Assert.Equal("live", cut.Find("#collaboration-mode").TextContent.Trim());
            Assert.Empty(cut.FindAll("#collaboration-take-lock"));
        }

        [Fact]
        public void A_lock_someone_else_holds_is_named_and_nothing_is_taken_from_them()
        {
            _service.Session = Session((Me, "Ada", null), (Bo, "Bo", null));
            _service.Holder = (Bo, "Bo");

            var cut = Panel();

            cut.WaitForAssertion(() => Assert.StartsWith("Bo holds the edit lock", cut.Find("#collaboration-lock").TextContent.Trim()));
            Assert.Empty(cut.FindAll("#collaboration-take-lock"));
            Assert.DoesNotContain("renew", _service.Calls);
        }

        [Fact]
        public void Someone_elses_save_is_a_warning_with_Reload_and_the_users_own_save_or_another_resource_is_ignored()
        {
            _service.Session = Session((Me, "Ada", null), (Bo, "Bo", null));
            var reloads = 0;
            var saved = new List<CollaborationNotice>();
            var cut = Render<CollaborationPanel>(parameters => Parameters(parameters)
                .Add(panel => panel.OnReloadRequested, EventCallback.Factory.Create(this, () => reloads++))
                .Add(panel => panel.OnSavedElsewhere, EventCallback.Factory.Create<CollaborationNotice>(this, notice => saved.Add(notice))));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#collaboration-panel")));

            _signal.Notify(_alias.SiteKey, new CollaborationNotice("dashboard", Board, CollaborationNoticeKinds.Saved, Me));          // mine
            _signal.Notify(_alias.SiteKey, new CollaborationNotice("dashboard", "another-board", CollaborationNoticeKinds.Saved, Bo)); // another resource
            _signal.Notify("1:2", new CollaborationNotice("dashboard", Board, CollaborationNoticeKinds.Saved, Bo));                    // another site
            Assert.Empty(cut.FindAll(".collaboration-warning"));

            _signal.Notify(_alias.SiteKey, new CollaborationNotice("dashboard", Board, CollaborationNoticeKinds.Saved, Bo));
            cut.WaitForAssertion(() => Assert.StartsWith("Bo saved this board.", cut.Find(".collaboration-warning span").TextContent.Trim()));
            Assert.Equal(Bo, Assert.Single(saved).ByUserId);

            cut.Find(".collaboration-reload").Click();
            Assert.Equal(1, reloads);
            Assert.Empty(cut.FindAll(".collaboration-warning"));
        }

        [Fact]
        public void An_announced_change_is_shown_and_a_lock_lost_to_a_take_over_is_told()
        {
            _service.Session = Session((Me, "Ada", null), (Bo, "Bo", null));
            var cut = Panel();
            cut.WaitForAssertion(() => Assert.Equal("You hold the edit lock.", cut.Find("#collaboration-lock").TextContent.Trim()));

            _service.Session = _service.Session with { Changes = new[] { new ChangeEntryView(Bo, DateTime.UtcNow, "layout changed") } };
            _signal.Notify(_alias.SiteKey, new CollaborationNotice("dashboard", Board, CollaborationNoticeKinds.Change, Bo));
            cut.WaitForAssertion(() => Assert.Equal("Bo: layout changed", cut.Find("#collaboration-last-change").TextContent.Trim()));

            _service.Holder = (Bo, "Bo"); // an administrator took it over and Bo took it
            _signal.Notify(_alias.SiteKey, new CollaborationNotice("dashboard", Board, CollaborationNoticeKinds.Lock, Bo));
            cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".collaboration-warning span"), element => element.TextContent.Contains("taken over or lapsed", StringComparison.Ordinal)));
            Assert.StartsWith("Bo holds the edit lock", cut.Find("#collaboration-lock").TextContent.Trim());
        }

        [Fact]
        public void A_refused_join_renders_nothing_and_takes_no_lock()
        {
            _service.JoinRefusal = new CollaborationRefusalView("404", "That is no longer there.");

            var cut = Panel();

            cut.WaitForAssertion(() => Assert.Contains("join dashboard/" + Board, _service.Calls));
            Assert.Empty(cut.FindAll("#collaboration-panel"));
            Assert.DoesNotContain("acquire", _service.Calls);
        }

        [Fact]
        public void A_user_the_page_does_not_let_collaborate_is_never_joined()
        {
            var cut = Render<CollaborationPanel>(parameters => Parameters(parameters, enabled: false));

            Assert.Empty(cut.FindAll("#collaboration-panel"));
            Assert.Empty(_service.Calls);
        }

        [Fact]
        public void A_switched_off_installation_is_never_joined()
        {
            // Collaboration is off by default, the join then answers 404, and a WebAssembly site's browser logged it
            // every time the editor or a board opened. The site says so, and the panel asks nothing.
            var off = new PageState { Site = new Site { Settings = new Dictionary<string, string> { ["Collaboration:Enabled"] = "false" } } };

            var cut = Render<CollaborationPanel>(parameters => Parameters(parameters).AddCascadingValue(off));

            Assert.Empty(cut.FindAll("#collaboration-panel"));
            Assert.Empty(_service.Calls);
        }

        [Fact]
        public async Task Closing_the_editor_gives_the_lock_up_and_leaves_the_session()
        {
            _service.Session = Session((Me, "Ada", null));
            var cut = Panel();
            cut.WaitForAssertion(() => Assert.Equal("You hold the edit lock.", cut.Find("#collaboration-lock").TextContent.Trim()));

            await cut.Instance.DisposeAsync();

            Assert.Equal(new[] { "release", "leave" }, _service.Calls.Skip(_service.Calls.Count - 2));
        }

        private IRenderedComponent<CollaborationPanel> Panel() => Render<CollaborationPanel>(parameters => Parameters(parameters));

        private static ComponentParameterCollectionBuilder<CollaborationPanel> Parameters(ComponentParameterCollectionBuilder<CollaborationPanel> parameters, bool enabled = true) =>
            parameters
                .Add(panel => panel.ResourceType, "dashboard")
                .Add(panel => panel.ResourceId, Board)
                .Add(panel => panel.ResourceLabel, "board")
                .Add(panel => panel.Enabled, enabled)
                .Add(panel => panel.CurrentUserId, Me);

        private static EditSessionView Session(params (int UserId, string Name, string Region)[] participants) =>
            new(Guid.NewGuid(), "dashboard", Board, "Open", DateTime.UtcNow,
                participants.Select(participant => new ParticipantView(participant.UserId, participant.Name, DateTime.UtcNow, participant.Region, null)).ToList(),
                Array.Empty<ChangeEntryView>());

        /// <summary>The collaboration API as a script: the session and the lock holder the test sets, every call recorded.</summary>
        private sealed class ScriptedCollaborationService : ICollaborationService
        {
            public EditSessionView Session { get; set; }

            public (int UserId, string Name)? Holder { get; set; }

            public CollaborationRefusalView JoinRefusal { get; set; }

            public List<string> Calls { get; } = new();

            public Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync() => throw new NotSupportedException();

            public Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request) => throw new NotSupportedException();

            public Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId) => throw new NotSupportedException();

            public Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId)
            {
                Calls.Add($"join {resourceType}/{resourceId}");
                return Task.FromResult(JoinRefusal is null ? new CollaborationOutcome<EditSessionView>(Session, null) : new CollaborationOutcome<EditSessionView>(null, JoinRefusal));
            }

            public Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId)
            {
                Calls.Add("session");
                return Task.FromResult(new CollaborationOutcome<EditSessionView>(Session, null));
            }

            public Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion)
            {
                Calls.Add("presence " + activeRegion);
                return Task.FromResult(new CollaborationOutcome<bool>(true, null));
            }

            public Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor)
            {
                Calls.Add("announce " + descriptor);
                return Task.FromResult(new CollaborationOutcome<EditSessionView>(Session, null));
            }

            public Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId)
            {
                Calls.Add("leave");
                return Task.FromResult(new CollaborationOutcome<bool>(true, null));
            }

            public Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId)
            {
                Calls.Add("heartbeat");
                return Task.FromResult(new CollaborationOutcome<bool>(true, null));
            }

            public Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request)
            {
                Calls.Add("acquire");
                Holder ??= (Me, "Ada");
                return Task.FromResult(new CollaborationOutcome<ResourceLockResult>(Result(), null));
            }

            public Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request)
            {
                Calls.Add("renew");
                return Task.FromResult(new CollaborationOutcome<ResourceLockResult>(Result(), null));
            }

            public Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request)
            {
                Calls.Add("release");
                Holder = null;
                return Task.FromResult(new CollaborationOutcome<bool>(true, null));
            }

            public Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId)
            {
                Calls.Add("lock");
                return Task.FromResult(new CollaborationOutcome<ResourceLockResult>(Holder is null ? null : Result(), null));
            }

            private ResourceLockResult Result() => new(Holder!.Value.UserId == Me, Holder.Value.UserId, Holder.Value.Name, DateTime.UtcNow.AddMinutes(2));
        }
    }
}
