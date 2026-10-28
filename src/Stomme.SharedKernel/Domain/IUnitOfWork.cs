namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Unit of Work pattern for coordinating transactional persistence
/// and domain event dispatching.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>
    /// Saves all changes and dispatches domain events.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
