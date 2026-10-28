#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.AiAssistant;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.AiAssistant.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>
    /// Applies a proposed PIPELINE (the predecessor platform's <c>PipelineArtifactApplier</c>): the draft goes through the Pipelines module's
    /// own create and save-graph commands as the user, and only then is the artifact marked applied - a failed create leaves
    /// it Proposed. It runs only on the user's explicit apply; nothing is auto-applied.
    /// </summary>
    public sealed class PipelineArtifactApplier(IAssistantCommandGateway commands, TimeProvider time)
    {
        public async Task<string> ApplyAsync(AssistantConversationAggregate conversation, Guid artifactId, CancellationToken cancellationToken = default)
        {
            var artifact = ArtifactAppliers.RequireProposed(conversation, artifactId, ArtifactKind.Pipeline);
            var pipelineId = await commands.CreatePipelineAsync(AssistantDraftJson.Deserialize<AssistantPipelineDraft>(artifact.SpecJson), cancellationToken);
            conversation.ApplyArtifact(artifactId, time.GetUtcNow().UtcDateTime);
            return pipelineId;
        }
    }

    /// <summary>
    /// Applies a proposed BOARD (the predecessor platform's <c>DashboardArtifactApplier</c>): through the Dashboards module's own create and
    /// add-widget commands as the user - which refuse a themed chart spec again - and only then is the artifact marked applied.
    /// </summary>
    public sealed class DashboardArtifactApplier(IAssistantCommandGateway commands, TimeProvider time)
    {
        public async Task<Guid> ApplyAsync(AssistantConversationAggregate conversation, Guid artifactId, CancellationToken cancellationToken = default)
        {
            var artifact = ArtifactAppliers.RequireProposed(conversation, artifactId, ArtifactKind.Dashboard);
            var dashboardId = await commands.CreateDashboardAsync(AssistantDraftJson.Deserialize<AssistantDashboardDraft>(artifact.SpecJson), cancellationToken);
            conversation.ApplyArtifact(artifactId, time.GetUtcNow().UtcDateTime);
            return dashboardId;
        }
    }

    internal static class ArtifactAppliers
    {
        public static GeneratedArtifact RequireProposed(AssistantConversationAggregate conversation, Guid artifactId, ArtifactKind kind)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            var artifact = conversation.Artifacts.FirstOrDefault(candidate => candidate.Id == artifactId)
                ?? throw new AssistantConflictException($"Artifact {artifactId} is not part of this conversation.");
            if (artifact.Kind != kind)
            {
                throw new AssistantConflictException($"Artifact {artifactId} is a {artifact.Kind} artifact, not a {kind}.");
            }

            if (artifact.State != ArtifactState.Proposed)
            {
                throw new AssistantConflictException($"Only a proposed artifact can be applied; artifact {artifactId} is {artifact.State}.");
            }

            return artifact;
        }
    }

    /// <summary>
    /// The conversation lifecycle the chat panel and the API use (the predecessor platform's <c>AssistantConversationService</c>): start, read,
    /// send, apply, discard - always the CURRENT user's conversation on the CURRENT site (another's answers as absent, never
    /// as forbidden, so ids cannot be probed).
    /// </summary>
    /// <remarks>
    /// <b>CHANGED:</b> the user is the request's principal, always (the predecessor platform accepted a principal or fell back to a user name or
    /// <c>"system"</c>); a turn is stored only once the agent has answered, so a refused message (the guardrail, a spent quota,
    /// an unconfigured site) leaves the transcript as it was; and before a proposal is staged, every dataset it binds is read
    /// again AS THE USER and attached as the conversation's context - one the user cannot see withholds the proposal and says so,
    /// so a model cannot bind a board to data its user was never shown.
    /// </remarks>
    public sealed class AssistantConversationService(
        IAssistantConversationRepository conversations,
        IAssistantAgent agent,
        IAssistantQueryGateway workspace,
        PipelineArtifactApplier pipelineApplier,
        DashboardArtifactApplier dashboardApplier,
        IAssistantCaller caller,
        IAssistantModelAccess models,
        TimeProvider time,
        IUnitOfWork unitOfWork)
    {
        public const string Withheld = "A proposal was withheld: it binds a dataset you cannot see.";

        /// <exception cref="AssistantUnavailableException">The assistant is off for the installation or this site.</exception>
        public async Task<AssistantConversationView> StartAsync(CancellationToken cancellationToken = default)
        {
            var (siteId, tenantId, userId) = await RequireCallerAsync(cancellationToken);
            if (!await models.IsAvailableAsync(cancellationToken))
            {
                throw new AssistantUnavailableException(AssistantUnavailableReason.Disabled, "The assistant is switched off for this site.");
            }

            var conversation = AssistantConversationAggregate.Start(Guid.NewGuid(), tenantId, siteId, userId, Now());
            await conversations.AddAsync(conversation, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToView(conversation);
        }

        public async Task<IReadOnlyList<AssistantConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
        {
            var (siteId, _, userId) = await RequireCallerAsync(cancellationToken);
            return (await conversations.ListForUserAsync(siteId, userId, cancellationToken))
                .Select(conversation => new AssistantConversationSummary(
                    conversation.Id,
                    conversation.Status.ToString(),
                    conversation.StartedUtc,
                    conversation.UpdatedUtc,
                    conversation.Messages.FirstOrDefault(message => message.Role == MessageRole.User)?.Content,
                    conversation.Artifacts.Count))
                .ToList();
        }

        public async Task<AssistantConversationView?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            await LoadAsync(id, cancellationToken) is { } conversation ? ToView(conversation) : null;

        /// <summary>One turn: the user's message, the agent's reply, its vetted proposals. Null when there is no such conversation.</summary>
        /// <exception cref="AssistantGuardrailException">The guardrail refused the message; nothing was stored.</exception>
        /// <exception cref="AssistantUnavailableException">The model cannot be used here; nothing was stored.</exception>
        public async Task<AssistantConversationView?> SendAsync(Guid id, string message, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            if (await LoadAsync(id, cancellationToken) is not { } conversation)
            {
                return null;
            }

            // What the aggregate would refuse is refused BEFORE a model call is spent on it; the aggregate itself is not
            // touched until the agent has answered.
            if (conversation.Status != ConversationStatus.Active)
            {
                throw new AssistantConflictException("The conversation is closed; start a new one.");
            }

            if (message.Length > AssistantConversationAggregate.MaxMessageLength)
            {
                throw new ArgumentException($"A message holds at most {AssistantConversationAggregate.MaxMessageLength} characters.", nameof(message));
            }

            if (conversation.Messages.Count + 2 > AssistantConversationAggregate.MaxMessages)
            {
                throw new AssistantConflictException("The conversation is full; start a new one.");
            }

            var askedAt = Now();
            var history = conversation.Messages.Select(turn => new AssistantMessage(ToChatRole(turn.Role), turn.Content)).Append(new AssistantMessage(ChatRole.User, message)).ToList();
            var reply = await agent.InvokeAsync(new AssistantRequest(conversation.Id, history), cancellationToken);
            conversation.AddMessage(MessageRole.User, message, askedAt);

            var proposals = new List<(ArtifactDraft Draft, IReadOnlyList<Guid> Datasets)>();
            var withheld = false;
            foreach (var draft in reply.Artifacts)
            {
                var datasets = (draft.ReferencedDatasetIds ?? Array.Empty<Guid>()).Distinct().ToList();
                if (await AllVisibleAsync(datasets, cancellationToken))
                {
                    proposals.Add((draft, datasets));
                }
                else
                {
                    withheld = true;
                }
            }

            var text = string.IsNullOrWhiteSpace(reply.Message.Content) ? string.Empty : reply.Message.Content.Trim();
            if (withheld)
            {
                text = text.Length == 0 ? Withheld : text + "\n\n" + Withheld;
            }

            conversation.AddMessage(MessageRole.Assistant, text.Length == 0 ? "(no answer)" : Bound(text), Now());
            foreach (var (draft, datasets) in proposals)
            {
                foreach (var dataset in datasets)
                {
                    conversation.AttachDataContext(DataContextRef.Create(dataset), Now());
                }

                conversation.ProposeArtifact(Guid.NewGuid(), draft.Kind, draft.Title, draft.SpecJson, AgentTrace.Create(agent.Name, reply.Message.Content), datasets, Now());
            }

            await conversations.UpdateAsync(conversation, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken); // where the conversation's domain events are dispatched
            return ToView(conversation);
        }

        /// <summary>Applies a proposal through its owning module; null when there is no such conversation.</summary>
        public async Task<AssistantArtifactApplyResult?> ApplyArtifactAsync(Guid conversationId, Guid artifactId, CancellationToken cancellationToken = default)
        {
            if (await LoadAsync(conversationId, cancellationToken) is not { } conversation)
            {
                return null;
            }

            var artifact = conversation.Artifacts.FirstOrDefault(candidate => candidate.Id == artifactId)
                ?? throw new AssistantConflictException($"Artifact {artifactId} is not part of conversation {conversationId}.");
            var created = artifact.Kind switch
            {
                ArtifactKind.Pipeline => await pipelineApplier.ApplyAsync(conversation, artifactId, cancellationToken),
                ArtifactKind.Dashboard => (await dashboardApplier.ApplyAsync(conversation, artifactId, cancellationToken)).ToString(),
                _ => throw new AssistantConflictException($"A {artifact.Kind} artifact is applied as part of its board or page, not on its own."),
            };

            await conversations.UpdateAsync(conversation, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken); // where the conversation's domain events are dispatched
            return new AssistantArtifactApplyResult(conversationId, artifactId, artifact.Kind.ToString(), created);
        }

        /// <summary>Discards a proposal; false when there is no such conversation.</summary>
        public async Task<bool> DiscardArtifactAsync(Guid conversationId, Guid artifactId, CancellationToken cancellationToken = default)
        {
            if (await LoadAsync(conversationId, cancellationToken) is not { } conversation)
            {
                return false;
            }

            var artifact = conversation.Artifacts.FirstOrDefault(candidate => candidate.Id == artifactId)
                ?? throw new AssistantConflictException($"Artifact {artifactId} is not part of conversation {conversationId}.");
            if (artifact.State != ArtifactState.Proposed || conversation.Status != ConversationStatus.Active)
            {
                throw new AssistantConflictException($"Only a proposed artifact of an open conversation can be discarded; artifact {artifactId} is {artifact.State}.");
            }

            conversation.DiscardArtifact(artifactId, Now());
            await conversations.UpdateAsync(conversation, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken); // where the conversation's domain events are dispatched
            return true;
        }

        private async Task<bool> AllVisibleAsync(IReadOnlyList<Guid> datasets, CancellationToken cancellationToken)
        {
            foreach (var dataset in datasets)
            {
                if (await workspace.GetDataSummaryAsync(dataset, cancellationToken) is null)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<AssistantConversationAggregate?> LoadAsync(Guid id, CancellationToken cancellationToken)
        {
            var (siteId, _, userId) = await RequireCallerAsync(cancellationToken);
            return await conversations.GetForUserAsync(id, siteId, userId, cancellationToken);
        }

        private async Task<(int SiteId, Stomme.Tenancy.Domain.TenantId TenantId, int UserId)> RequireCallerAsync(CancellationToken cancellationToken)
        {
            var (siteId, tenantId, userId) = await caller.ResolveAsync(cancellationToken);
            if (siteId <= 0 || userId <= 0 || tenantId is not { } tenant)
            {
                throw new UnauthorizedAccessException("The assistant is available to a signed-in user on a site.");
            }

            return (siteId, tenant, userId);
        }

        private DateTime Now() => time.GetUtcNow().UtcDateTime;

        /// <summary>A reply is kept within the aggregate's message bound rather than lost to it.</summary>
        private static string Bound(string text) =>
            text.Length <= AssistantConversationAggregate.MaxMessageLength ? text : text[..AssistantConversationAggregate.MaxMessageLength];

        private static ChatRole ToChatRole(MessageRole role) => role switch
        {
            MessageRole.User => ChatRole.User,
            MessageRole.Assistant => ChatRole.Assistant,
            _ => ChatRole.System,
        };

        public static AssistantConversationView ToView(AssistantConversationAggregate conversation) =>
            new(
                conversation.Id,
                conversation.Status.ToString(),
                conversation.StartedUtc,
                conversation.UpdatedUtc,
                conversation.Messages.Select(message => new AssistantMessageView(message.Role.ToString(), message.Content, message.AtUtc)).ToList(),
                conversation.Artifacts.Select(artifact => new AssistantArtifactView(artifact.Id, artifact.Kind.ToString(), artifact.Title, artifact.SpecJson, artifact.State.ToString(), artifact.Trace.AgentName)).ToList());
    }
}
