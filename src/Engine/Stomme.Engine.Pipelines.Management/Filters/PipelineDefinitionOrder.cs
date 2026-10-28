using System.Linq.Expressions;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Management.Entities;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Filters;

/// <summary>
/// Represents the order by which to order the results of a query.
/// </summary>
[PublicAPI]
public class PipelineDefinitionOrder<TProp> : OrderDefinition<PipelineDefinition, TProp>
{
    /// <inheritdoc />
    public PipelineDefinitionOrder()
    {
    }

    /// <summary>
    /// Creates a new instance of the <see cref="PipelineDefinitionOrder{TProp}"/> class.
    /// </summary>
    public PipelineDefinitionOrder(Expression<Func<PipelineDefinition, TProp>> keySelector, OrderDirection direction) : base(keySelector, direction)
    {
    }
}