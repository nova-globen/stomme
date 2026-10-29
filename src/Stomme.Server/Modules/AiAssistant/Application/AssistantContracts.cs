#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Domain;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.AiAssistant.Application
{
    /// <summary>One turn as the agents see it; the role is <see cref="ChatRole"/>, so it maps straight onto the model exchange.</summary>
    public sealed record AssistantMessage(ChatRole Role, string Content);

    /// <summary>
    /// A draft an agent proposes, in the owning module's own serialized form, with the datasets it binds (the conversation
    /// vets and attaches them before staging it). Nothing here writes to a store.
    /// </summary>
    public sealed record ArtifactDraft(ArtifactKind Kind, string Title, string SpecJson, IReadOnlyList<Guid>? ReferencedDatasetIds = null);

    /// <summary>A resource kind a request may explicitly target, checked by the guardrail's authorization step.</summary>
    public enum AssistantResourceKind
    {
        Connection = 0,
        Pipeline = 1,
        Dashboard = 2,
        Dataset = 3,
    }

    /// <summary>A resource a request explicitly targets ("change dashboard X").</summary>
    public sealed record AssistantResourceRef(AssistantResourceKind Kind, Guid ResourceId);

    /// <summary>
    /// A request to the assistant: the conversation, the messages so far, the resources it targets. <b>CHANGED:</b> the predecessor platform
    /// passed the user id in the request; here the user is the request's principal, always - a caller cannot ask on
    /// somebody else's behalf, the rule D102's meter follows.
    /// </summary>
    public sealed record AssistantRequest(Guid ConversationId, IReadOnlyList<AssistantMessage> Messages, IReadOnlyList<AssistantResourceRef>? ReferencedResources = null);

    /// <summary>An agent's reply: the message, and any drafts it proposes (left Proposed).</summary>
    public sealed record AssistantReply(AssistantMessage Message, IReadOnlyList<ArtifactDraft> Artifacts)
    {
        public static AssistantReply Text(AssistantMessage message) => new(message, Array.Empty<ArtifactDraft>());
    }

    /// <summary>Why the guardrail refused a request - surfaced to the user, never fed back to a model.</summary>
    public enum AssistantGuardrailReason
    {
        PromptInjection = 0,
        RateLimitExceeded = 1,
        Unauthorized = 2,
    }

    /// <summary>A request refused before any model call. The message is safe to show the user.</summary>
    public sealed class AssistantGuardrailException(AssistantGuardrailReason reason, string message) : Exception(message)
    {
        public AssistantGuardrailReason Reason { get; } = reason;
    }

    /// <summary>
    /// The guardrail's authorization port: may the CURRENT user use this resource? The module's default refuses every
    /// explicit reference it cannot check (fail-closed); the resource's owning module answers through the key check its
    /// own commands use.
    /// </summary>
    public interface IAssistantAuthorizer
    {
        Task<bool> IsAuthorizedAsync(AssistantResourceRef resource, CancellationToken cancellationToken = default);
    }

    /// <summary>Checks a request before any model call; throws <see cref="AssistantGuardrailException"/> on the first refusal.</summary>
    public interface IAssistantGuardrail
    {
        Task EnsureAllowedAsync(AssistantRequest request, int siteId, int userId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Why the model could not be reached for this site - the assistant switched off (for the installation or the site), not
    /// configured, its key unreadable, or the site's AI quota spent. Stated to the user; never a model call.
    /// </summary>
    public enum AssistantUnavailableReason
    {
        Disabled = 0,
        NotConfigured = 1,
        KeyUnreadable = 2,
        QuotaExceeded = 3,
        MeteringUnavailable = 4,
        UnknownProvider = 5,

        /// <summary>The site's outbound guard refused the model endpoint's address - a private, loopback or internal one the site has not allowed.</summary>
        EndpointNotPermitted = 6,
    }

    /// <summary>The model could not be used; <see cref="Reason"/> says why, the message names a setting and never a value.</summary>
    public sealed class AssistantUnavailableException : Exception
    {
        public AssistantUnavailableException(AssistantUnavailableReason reason, string message)
            : base(message) => Reason = reason;

        public AssistantUnavailableException(AssistantUnavailableReason reason, string message, Exception innerException)
            : base(message, innerException) => Reason = reason;

        public AssistantUnavailableReason Reason { get; }
    }
}
