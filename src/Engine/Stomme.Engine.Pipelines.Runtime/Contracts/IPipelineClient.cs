using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a client that can interact with a pipeline instance.
/// </summary>
public interface IPipelineClient
{
    /// <summary>
    /// Gets the ID of the pipeline instance.
    /// </summary>
    string PipelineInstanceId { get; }
    
    /// <summary>
    /// Creates a new pipeline instance for the specified pipeline definition version.
    /// </summary>
    Task<CreatePipelineInstanceResponse> CreateInstanceAsync(CreatePipelineInstanceRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Executes the pipeline instance and waits for it to complete or reach a suspend point.
    /// </summary>
    Task<RunPipelineInstanceResponse> RunInstanceAsync(RunPipelineInstanceRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new pipeline instance and executes it.
    /// </summary>
    Task<RunPipelineInstanceResponse> CreateAndRunInstanceAsync(CreateAndRunPipelineInstanceRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Cancels the execution of a pipeline instance.
    /// </summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Exports the <see cref="PipelineState"/> of the specified pipeline instance.
    /// </summary>
    Task<PipelineState> ExportStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the specified <see cref="PipelineState"/>.
    /// </summary>
    Task ImportStateAsync(PipelineState pipelineState, CancellationToken cancellationToken = default);

    Task<bool> InstanceExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the pipeline instance. This method ensures that the instance is properly stopped before deletion.
    /// </summary>
    Task<bool> DeleteAsync(CancellationToken cancellationToken = default);
}