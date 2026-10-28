using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Populates the <see cref="IPipelineDefinitionStore"/> with pipeline definitions provided from <see cref="IPipelinesProvider"/> implementations.
/// </summary>
public interface IPipelineDefinitionStorePopulator
{
    /// <summary>
    /// Populates the <see cref="IPipelineDefinitionStore"/> with pipeline definitions provided from <see cref="IPipelinesProvider"/> implementations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IEnumerable<PipelineDefinition>> PopulateStoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Populates the <see cref="IPipelineDefinitionStore"/> with pipeline definitions provided from <see cref="IPipelinesProvider"/> implementations.
    /// </summary>
    /// <param name="indexTriggers">Whether to index triggers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IEnumerable<PipelineDefinition>> PopulateStoreAsync(bool indexTriggers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a pipeline definition to the store.
    /// </summary>
    /// <param name="materializedPipeline">A materialized pipeline.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task<Result<PipelineDefinition>> AddAsync(MaterializedPipeline materializedPipeline, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a pipeline definition to the store.
    /// </summary>
    /// <param name="materializedPipeline">A materialized pipeline.</param>
    /// <param name="indexTriggers">Whether to index triggers.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task<Result<PipelineDefinition>> AddAsync(MaterializedPipeline materializedPipeline, bool indexTriggers, CancellationToken cancellationToken = default);
}