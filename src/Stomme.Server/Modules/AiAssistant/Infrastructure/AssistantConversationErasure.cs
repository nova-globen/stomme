#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.AiAssistant.Persistence;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.AiAssistant.Infrastructure
{
    /// <summary>
    /// The assistant's right-to-erasure participant: a conversation is the user's own text, so it is
    /// DELETED - every conversation the user holds on the site. Idempotent: a second run finds nothing.
    /// </summary>
    public sealed class AssistantConversationErasure(
        IAssistantConversationRepository conversations,
        IUnitOfWork unitOfWork,
        ILogger<AssistantConversationErasure> logger) : ISubjectErasureParticipant
    {
        public string Name => "AI assistant conversations";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete("AssistantConversationAggregate") with { Summary = "your conversations with the AI assistant" },
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (subject.UserId <= 0 || subject.SiteId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var deleted = 0;
            foreach (var conversation in await conversations.ListForUserAsync(subject.SiteId, subject.UserId, ct))
            {
                await conversations.DeleteAsync(conversation, ct);
                deleted++;
            }

            if (deleted > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("Deleted {Count} assistant conversation(s) of user {UserId} on site {SiteId}.", deleted, subject.UserId, subject.SiteId);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, deleted) };
        }
    }
}
