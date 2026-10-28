using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// A service that manages pipeline instances.
/// </summary>
public interface IPipelineInstanceManager
{
    /// <summary>
    /// Finds the pipeline instance with the specified ID.
    /// </summary>
    Task<PipelineInstance?> FindByIdAsync(string instanceId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds the first pipeline instance that matches the specified filter.
    /// </summary>
    Task<PipelineInstance?> FindAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a pipeline instance with the specified ID exists.
    /// </summary>
    Task<bool> ExistsAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the specified pipeline instance.
    /// </summary>
    Task SaveAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps the specified pipeline state to a pipeline instance and saves it.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to map to a pipeline instance.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The pipeline instance that was saved.</returns>
    Task<PipelineInstance> SaveAsync(PipelineState pipelineState, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts the pipeline state from the specified pipeline execution context, maps it to a pipeline instance and saves it.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context to extract the pipeline state from.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The pipeline instance that was saved.</returns>
    Task<PipelineInstance> SaveAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves the specified pipeline instance.
    /// </summary>
    Task CreateAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps the specified pipeline state to a pipeline instance and saves it.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to map to a pipeline instance.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The pipeline instance that was saved.</returns>
    Task<PipelineInstance> CreateAsync(PipelineState pipelineState, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts the pipeline state from the specified pipeline execution context, maps it to a pipeline instance and saves it.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context to extract the pipeline state from.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The pipeline instance that was saved.</returns>
    Task<PipelineInstance> CreateAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates the specified pipeline instance.
    /// </summary>
    Task UpdateAsync(PipelineInstance pipelineInstance, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Maps the specified pipeline state to a pipeline instance and updates it.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to map to a pipeline instance.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The pipeline instance that was saved.</returns>
    Task<PipelineInstance> UpdateAsync(PipelineState pipelineState, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the first pipeline instance that matches the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use to select the pipeline instance to delete.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>True if a pipeline instance was deleted, otherwise false.</returns>
    Task<bool> DeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all pipeline instances that match the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use to select the pipeline instances to delete.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of pipeline instances that were deleted.</returns>
    Task<long> BulkDeleteAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Extracts the pipeline state from the specified pipeline execution context.
    /// </summary>
    PipelineState ExtractPipelineState(PipelineExecutionContext pipelineExecutionContext);

    /// <summary>
    /// Serializes the specified pipeline state.
    /// </summary>
    string SerializePipelineState(PipelineState pipelineState);
    
    /// <summary>
    /// Instantiates and saves a new pipeline instance.
    /// </summary>
    Task<PipelineInstance> CreateAndCommitPipelineInstanceAsync(Pipeline pipeline, PipelineInstanceOptions? options = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Instantiates a new pipeline instance.
    /// </summary>
    PipelineInstance CreatePipelineInstance(Pipeline pipeline, PipelineInstanceOptions? options = null);
}