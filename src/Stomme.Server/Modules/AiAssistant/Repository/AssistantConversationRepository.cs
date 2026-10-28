#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.AiAssistant.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.AiAssistant.Persistence
{
    /// <summary>
    /// The conversations, read by site and user. A read by id is answered only for the conversation's own site and user:
    /// another user's conversation - or another site's - gets the same "not found" as one that does not exist (the §J.BL-80
    /// item 4 precedent), so ids cannot be probed.
    /// </summary>
    public interface IAssistantConversationRepository : IRepository<AssistantConversationAggregate, Guid>
    {
        Task<IReadOnlyList<AssistantConversationAggregate>> ListForUserAsync(int siteId, int userId, CancellationToken ct = default);

        /// <summary>The conversation IF it belongs to <paramref name="siteId"/> and <paramref name="userId"/>; otherwise null.</summary>
        Task<AssistantConversationAggregate?> GetForUserAsync(Guid id, int siteId, int userId, CancellationToken ct = default);
    }

    /// <summary>The storage-blind repository over the module store (D79): one provider-neutral query, no store of its own.</summary>
    public sealed class AssistantConversationRepository(IRepository<AssistantConversationAggregate, Guid> inner) : IAssistantConversationRepository
    {
        public async Task<IReadOnlyList<AssistantConversationAggregate>> ListForUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            (await inner.FindAsync(conversation => conversation.SiteId == siteId && conversation.UserId == userId, ct))
                .OrderByDescending(conversation => conversation.UpdatedUtc)
                .ToList();

        public async Task<AssistantConversationAggregate?> GetForUserAsync(Guid id, int siteId, int userId, CancellationToken ct = default)
        {
            var conversation = await inner.GetByIdAsync(id, ct);
            return conversation is not null && conversation.SiteId == siteId && conversation.UserId == userId ? conversation : null;
        }

        public Task<AssistantConversationAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<AssistantConversationAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task<IReadOnlyList<AssistantConversationAggregate>> FindAsync(System.Linq.Expressions.Expression<Func<AssistantConversationAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public Task AddAsync(AssistantConversationAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(AssistantConversationAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(AssistantConversationAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);
    }
}
