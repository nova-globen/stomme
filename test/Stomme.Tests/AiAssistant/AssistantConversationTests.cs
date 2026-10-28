#nullable enable
using System;
using System.Linq;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.AiAssistant
{
    /// <summary>
    /// BL-97 increment 9 - the predecessor platform's <c>AssistantConversationTests</c>, by name, on the lifted conversation: it opens active
    /// with its event; a proposed artifact is a Proposed draft and is never auto-applied; only a proposed artifact is applied,
    /// once; raw rows are refused as model context; an artifact may reference only datasets already attached. the predecessor platform's
    /// repository round trip is <see cref="AssistantConversationStoreConformanceTests"/>, on four stores. <b>CHANGED:</b> the
    /// conversation belongs to a site and a Stomme user id, and refuses neither.
    /// </summary>
    public sealed class AssistantConversationTests
    {
        private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        private const int Site = 1;
        private const int User = 7;

        private static AssistantConversationAggregate NewConversation() => AssistantConversationAggregate.Start(Guid.NewGuid(), TenantId.Default, Site, User, Now);

        private static AgentTrace Trace(string agent) => AgentTrace.Create(agent, "drafted");

        [Fact]
        public void Start_raises_ConversationStartedEvent_and_opens_the_conversation()
        {
            var id = Guid.NewGuid();
            var conversation = AssistantConversationAggregate.Start(id, TenantId.Default, Site, User, Now);

            Assert.Equal(ConversationStatus.Active, conversation.Status);
            Assert.Equal(User, conversation.UserId);
            Assert.Equal(Site, conversation.SiteId);
            var started = Assert.IsType<ConversationStartedEvent>(Assert.Single(conversation.DomainEvents));
            Assert.Equal(id, started.ConversationId);
            Assert.Equal(User, started.UserId);
        }

        [Fact]
        public void A_conversation_belongs_to_a_site_and_a_signed_in_user()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AssistantConversationAggregate.Start(Guid.NewGuid(), TenantId.Default, 0, User, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => AssistantConversationAggregate.Start(Guid.NewGuid(), TenantId.Default, Site, -1, Now));
            Assert.Throws<ArgumentException>(() => AssistantConversationAggregate.Start(Guid.Empty, TenantId.Default, Site, User, Now));
        }

        [Fact]
        public void ProposeArtifact_stages_a_proposed_draft_and_never_auto_applies()
        {
            var conversation = NewConversation();
            var artifact = conversation.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Pipeline, "Daily import", "{}", Trace("pipeline-architect"), null, Now);

            Assert.Equal(ArtifactState.Proposed, artifact.State);
            Assert.Equal(ArtifactState.Proposed, Assert.Single(conversation.Artifacts).State);
            Assert.Contains(conversation.DomainEvents, e => e is ArtifactGeneratedEvent);
            Assert.DoesNotContain(conversation.DomainEvents, e => e is ArtifactAppliedEvent);
        }

        [Fact]
        public void ApplyArtifact_transitions_only_a_proposed_artifact_and_raises_the_event()
        {
            var conversation = NewConversation();
            var artifact = conversation.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Dashboard, "Overview", "{}", Trace("dashboard-designer"), null, Now);

            conversation.ApplyArtifact(artifact.Id, Now);

            Assert.Equal(ArtifactState.Applied, artifact.State);
            Assert.Contains(conversation.DomainEvents, e => e is ArtifactAppliedEvent);
            Assert.Throws<InvalidOperationException>(() => conversation.ApplyArtifact(artifact.Id, Now));
            Assert.Throws<InvalidOperationException>(() => conversation.DiscardArtifact(artifact.Id, Now));
        }

        [Fact]
        public void AttachDataContext_rejects_sharing_raw_rows_summaries_only()
        {
            var conversation = NewConversation();
            Assert.Throws<InvalidOperationException>(() => conversation.AttachDataContext(DataContextRef.Create(Guid.NewGuid(), summaryOnly: false), Now));

            var dataset = Guid.NewGuid();
            conversation.AttachDataContext(DataContextRef.Create(dataset), Now);
            conversation.AttachDataContext(DataContextRef.Create(dataset), Now);   // idempotent per dataset
            Assert.Single(conversation.DataContext);
        }

        [Fact]
        public void ProposeArtifact_rejects_references_to_datasets_outside_the_users_reach()
        {
            var conversation = NewConversation();
            var attached = Guid.NewGuid();
            conversation.AttachDataContext(DataContextRef.Create(attached), Now);

            var ok = conversation.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Widget, "Chart", "{}", Trace("widget-generator"), new[] { attached }, Now);
            Assert.Equal(attached, Assert.Single(ok.ReferencedDatasetIds));

            Assert.Throws<InvalidOperationException>(() => conversation.ProposeArtifact(
                Guid.NewGuid(), ArtifactKind.Widget, "Chart", "{}", Trace("widget-generator"), new[] { Guid.NewGuid() }, Now));
        }

        [Fact]
        public void A_closed_conversation_takes_nothing_more()
        {
            var conversation = NewConversation();
            conversation.AddMessage(MessageRole.User, "hello", Now);
            conversation.Close(Now);

            Assert.Throws<InvalidOperationException>(() => conversation.AddMessage(MessageRole.User, "again", Now));
            Assert.Throws<InvalidOperationException>(() => conversation.ProposeArtifact(Guid.NewGuid(), ArtifactKind.Widget, "Chart", "{}", Trace("widget-generator"), null, Now));
            Assert.Single(conversation.Messages);
        }

        [Fact]
        public void A_message_is_bounded_and_never_empty()
        {
            var conversation = NewConversation();
            Assert.Throws<ArgumentException>(() => conversation.AddMessage(MessageRole.User, "  ", Now));
            Assert.Throws<ArgumentException>(() => conversation.AddMessage(MessageRole.User, new string('x', AssistantConversationAggregate.MaxMessageLength + 1), Now));
            conversation.AddMessage(MessageRole.User, new string('x', AssistantConversationAggregate.MaxMessageLength), Now);
            Assert.Equal(AssistantConversationAggregate.MaxMessageLength, conversation.Messages.Single().Content.Length);
        }
    }
}
