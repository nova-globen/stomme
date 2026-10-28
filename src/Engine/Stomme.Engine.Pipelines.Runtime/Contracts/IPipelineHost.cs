using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a single pipeline instance that can be executed and takes care of publishing various lifecycle events.
/// </summary>
[Obsolete("Use IPipelineRuntime and IPipelineClient services instead.")]
public interface IPipelineHost
{
    /// <summary>
    /// The pipeline graph.
    /// </summary>
    PipelineGraph PipelineGraph { get; }
    
    /// <summary>
    /// The pipeline.
    /// </summary>
    Pipeline Pipeline { get; }

    /// <summary>
    /// Gets or sets the pipeline state.
    /// </summary>
    PipelineState PipelineState { get; set; }
    
    /// <summary>
    /// Gets the last result of the pipeline execution, if any.
    /// </summary>
    object? LastResult { get; }

    /// <summary>
    /// Returns a value indicating whether the specified pipeline can start a new instance or not.
    /// </summary>
    Task<bool> CanStartPipelineAsync(RunPipelineOptions? @params = default, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Starts a new pipeline instance.
    /// </summary>
    Task<RunPipelineResult> RunPipelineAsync(RunPipelineOptions? @params = default, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Cancels the pipeline instance.
    /// </summary>
    Task CancelPipelineAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Persists the pipeline state.
    /// </summary>
    Task PersistStateAsync(CancellationToken cancellationToken = default);
}