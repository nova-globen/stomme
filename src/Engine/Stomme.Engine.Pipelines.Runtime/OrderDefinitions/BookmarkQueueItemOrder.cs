using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

/// <summary>
/// Represents the order by which to order the results of a query.
/// </summary>
[UsedImplicitly]
public class BookmarkQueueItemOrder<TProp> : OrderDefinition<BookmarkQueueItem, TProp>
{
    public BookmarkQueueItemOrder(Expression<Func<BookmarkQueueItem, TProp>> keySelector, OrderDirection direction)
    {
        KeySelector = keySelector;
        Direction = direction;
    }
}