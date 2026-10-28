using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Extracts triggers from pipeline definitions.
/// </summary>
public interface ITriggerIndexer
{
    /// <summary>
    /// Removes triggers matching the specified filter.
    /// </summary>
    Task DeleteTriggersAsync(TriggerFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes triggers of the specified pipeline.
    /// </summary>
    Task<IndexedPipelineTriggers> IndexTriggersAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Indexes triggers of the specified pipeline.
    /// </summary>
    Task<IndexedPipelineTriggers> IndexTriggersAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Returns triggers for the specified pipeline definition.
    /// </summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A collection of triggers.</returns>
    Task<IEnumerable<StoredTrigger>> GetTriggersAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
}