using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

/// <summary>
/// Represents the order by which to order the results of a query.
/// </summary>
public class PipelineExecutionLogRecordOrder<TProp> : OrderDefinition<PipelineExecutionLogRecord, TProp>
{
    /// <summary>
    /// Creates a new instance of the <see cref="PipelineExecutionLogRecordOrder{TProp}"/> class.
    /// </summary>
    public PipelineExecutionLogRecordOrder(Expression<Func<PipelineExecutionLogRecord, TProp>> keySelector, OrderDirection direction)
    {
        KeySelector = keySelector;
        Direction = direction;
    }
}