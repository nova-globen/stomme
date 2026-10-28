using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

public class StoredTriggerOrder<TProp> : OrderDefinition<StoredTrigger, TProp>
{
    public StoredTriggerOrder(Expression<Func<StoredTrigger, TProp>> keySelector, OrderDirection direction)
    {
        KeySelector = keySelector;
        Direction = direction;
    }
}