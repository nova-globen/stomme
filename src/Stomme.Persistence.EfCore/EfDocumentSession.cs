using Stomme.Persistence.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// EF Core <see cref="IDocumentSession"/> over a dedicated <see cref="DbContext"/>. The change tracker
/// supplies identity-map semantics (<see cref="LoadAsync{TEntity}"/> returns the tracked instance), and
/// the session owns the context — disposing the session disposes the context.
/// </summary>
public sealed class EfDocumentSession(DbContext context) : IDocumentSession
{
    public async Task<TEntity?> LoadAsync<TEntity>(object id, CancellationToken cancellationToken = default)
        where TEntity : class =>
        // FindAsync returns the identity-mapped instance when already tracked, else loads it.
        await context.Set<TEntity>().FindAsync([id], cancellationToken);

    public IQueryable<TEntity> Query<TEntity>()
        where TEntity : class => context.Set<TEntity>();

    public void Store<TEntity>(TEntity entity)
        where TEntity : class
    {
        // A detached entity is a new document; a tracked one is already managed and flushes on save.
        if (context.Entry(entity).State == EntityState.Detached)
        {
            context.Add(entity);
        }
    }

    public void Delete<TEntity>(TEntity entity)
        where TEntity : class => context.Remove(entity);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
