#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.AiAssistant
{
    // ============================================================================================================
    // The AI assistant's Shared views: what the chat panel and the API read. BCL-ONLY like the
    // other Shared contracts - the model exchange (Microsoft.Extensions.AI) stays on the server.
    // ============================================================================================================

    /// <summary>A conversation as its user sees it: the transcript and every artifact proposed in it.</summary>
    public sealed record AssistantConversationView(
        Guid Id,
        string Status,
        DateTime StartedUtc,
        DateTime UpdatedUtc,
        IReadOnlyList<AssistantMessageView> Messages,
        IReadOnlyList<AssistantArtifactView> Artifacts);

    /// <summary>One turn: <c>User</c> or <c>Assistant</c>, its text, when.</summary>
    public sealed record AssistantMessageView(string Role, string Content, DateTime AtUtc);

    /// <summary>A proposal: its kind, title and draft, and whether it is still <c>Proposed</c>, <c>Applied</c> or <c>Discarded</c>.</summary>
    public sealed record AssistantArtifactView(Guid Id, string Kind, string Title, string SpecJson, string State, string Agent);

    /// <summary>A conversation in a list: enough to choose one.</summary>
    public sealed record AssistantConversationSummary(Guid Id, string Status, DateTime StartedUtc, DateTime UpdatedUtc, string? FirstMessage, int ArtifactCount);

    /// <summary>What an apply created: the artifact's kind and the id of the pipeline or board the owning module wrote.</summary>
    public sealed record AssistantArtifactApplyResult(Guid ConversationId, Guid ArtifactId, string ArtifactKind, string CreatedResourceId);
}
