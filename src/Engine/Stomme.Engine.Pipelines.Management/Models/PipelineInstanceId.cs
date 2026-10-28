using System.Linq.Expressions;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents a pipeline instance ID.
/// </summary>
public class PipelineInstanceId
{
    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// Returns a model representing the pipeline instance ID of the specified <see cref="PipelineInstance"/>.
    /// </summary>
    public static PipelineInstanceId FromInstance(PipelineInstance pipelineInstance)
    {
        return new()
        {
            Id = pipelineInstance.Id
        };
    }

    /// <summary>
    /// Returns a model representing the pipeline instance ID of the specified <see cref="PipelineInstance"/>.
    /// </summary>
    public static Expression<Func<PipelineInstance, PipelineInstanceId>> FromInstanceExpression()
    {
        return pipelineInstance => new()
        {
            Id = pipelineInstance.Id
        };
    }
}