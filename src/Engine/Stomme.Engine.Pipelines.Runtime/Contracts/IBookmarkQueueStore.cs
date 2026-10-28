using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Provides access to bookmark queue items.
/// </summary>
public interface IBookmarkQueueStore
{
    /// <summary>
    /// Adds or updates the specified <see cref="BookmarkQueueItem"/> in the persistence store.
    /// </summary>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    Task SaveAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the specified <see cref="BookmarkQueueItem"/> to the persistence store.
    /// </summary>
    Task AddAsync(BookmarkQueueItem record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first bookmark queue item matching the specified filter.
    /// </summary>
    Task<BookmarkQueueItem?> FindAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Returns a set of bookmark queue items matching the specified filter.
    /// </summary>
    Task<IEnumerable<BookmarkQueueItem>> FindManyAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of records, ordered by the specified order definition.
    /// </summary>
    Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Returns a page of records, filtered and ordered by the specified order definition.
    /// </summary>
    Task<Page<BookmarkQueueItem>> PageAsync<TOrderBy>(PageArgs pageArgs, BookmarkQueueFilter filter, BookmarkQueueItemOrder<TOrderBy> orderBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a set of bookmark queue items matching the specified filter.
    /// </summary>
    /// <returns>The number of deleted records.</returns>
    Task<long> DeleteAsync(BookmarkQueueFilter filter, CancellationToken cancellationToken = default);
}