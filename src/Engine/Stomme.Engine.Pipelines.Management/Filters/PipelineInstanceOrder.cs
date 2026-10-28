using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Filters;

/// <summary>
/// Represents the order by which to order the results of a query.
/// </summary>
public class PipelineInstanceOrder<TProp> : OrderDefinition<PipelineInstance, TProp>
{
    /// <inheritdoc />
    public PipelineInstanceOrder()
    {
    }

    /// <summary>
    /// Creates a new instance of the <see cref="PipelineInstanceOrder{TProp}"/> class.
    /// </summary>
    public PipelineInstanceOrder(Expression<Func<PipelineInstance, TProp>> keySelector, OrderDirection direction) : base(keySelector, direction)
    {
    }
}