using Stomme.EventBus;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Fallback <see cref="IDeadLetterQueue"/> for transports without queryable dead-letter storage (e.g.
/// the in-memory transport used in dev/tests): always empty, retries/discards are no-ops. A real
/// transport-backed implementation (the RavenDB one) overrides this when registered.
/// </summary>
internal sealed class NullDeadLetterQueue : IDeadLetterQueue
{
    public Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterMessage>>([]);

    public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
