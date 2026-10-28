using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

/// <summary>
/// Represents the order by which to order the results of a query.
/// </summary>
public class StepExecutionRecordOrder<TProp> : OrderDefinition<StepExecutionRecord, TProp>
{
    /// <summary>
    /// Creates a new instance of the <see cref="StepExecutionRecordOrder{TProp}"/> class.
    /// </summary>
    public StepExecutionRecordOrder(Expression<Func<StepExecutionRecord, TProp>> keySelector, OrderDirection direction)
    {
        KeySelector = keySelector;
        Direction = direction;
    }
}