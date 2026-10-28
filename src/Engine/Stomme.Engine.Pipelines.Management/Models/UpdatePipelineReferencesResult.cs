using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents the result of updating pipeline references.
/// </summary>
public class UpdatePipelineReferencesResult(IEnumerable<PipelineDefinition> updatedPipelines)
{
    /// <summary>
    /// Gets a collection of pipeline graphs that have been updated.
    /// </summary>
    /// <value>
    /// A read-only collection of <see cref="PipelineGraph"/> instances representing the updated pipelines.
    /// </value>
    public IReadOnlyCollection<PipelineDefinition> UpdatedPipelines { get; } = updatedPipelines.ToList();
}