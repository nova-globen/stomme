using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Matches;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Parameters;
using Stomme.Engine.Pipelines.Runtime.Params;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Results;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a pipeline runtime that can create <see cref="IPipelineClient"/> instances connected to a pipeline instance.
/// </summary>
public interface IPipelineRuntime
{
    /// <summary>
    /// Creates a new <see cref="IPipelineClient"/> instance.
    /// </summary>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A new <see cref="IPipelineClient"/> instance.</returns>
    /// <remarks>The pipeline instance doesn't exist yet, and a new pipeline instance ID will be generated.</remarks>
    ValueTask<IPipelineClient> CreateClientAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new <see cref="IPipelineClient"/> instance.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance for which to create a client. If <c>null</c>, a new pipeline instance ID will be generated.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A new <see cref="IPipelineClient"/> instance.</returns>
    /// <remarks>The pipeline instance itself doesn't have to exist yet.</remarks>
    ValueTask<IPipelineClient> CreateClientAsync(string? pipelineInstanceId, CancellationToken cancellationToken = default);
    
    
    /// <summary>
    /// Returns a value whether the specified pipeline definition can create a new instance.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<CanStartPipelineResult> CanStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = default);

    /// <summary>
    /// Creates a new pipeline instance of the specified definition ID and executes it.
    /// </summary>
    /// <param name="definitionId">The pipeline definition ID to run.</param>
    /// <param name="options">Options for starting the pipeline.</param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<PipelineExecutionResult> StartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = default);

    /// <summary>
    /// Starts all pipelines with triggers matching the specified step type and bookmark payload.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<ICollection<PipelineExecutionResult>> StartPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = default);

    /// <summary>
    /// Tries to start a pipeline and returns the result if successful.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<PipelineExecutionResult?> TryStartPipelineAsync(string definitionId, StartPipelineRuntimeParams? options = default);

    /// <summary>
    /// Resumes an existing pipeline instance.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance to resume.</param>
    /// <param name="options">Options for resuming the pipeline.</param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<PipelineExecutionResult?> ResumePipelineAsync(string pipelineInstanceId, ResumePipelineRuntimeParams? options = default);

    /// <summary>
    /// Resumes all pipelines that are bookmarked on the specified step type. 
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<ICollection<PipelineExecutionResult>> ResumePipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = default);

    /// <summary>
    /// Starts all pipelines and resumes existing pipeline instances based on the specified step type and bookmark payload.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<TriggerPipelinesResult> TriggerPipelinesAsync(string stepTypeName, object bookmarkPayload, TriggerPipelinesOptions? options = default);

    /// <summary>
    /// Executes a pending pipeline.
    /// </summary>
    /// <param name="match">A pipeline match to execute.</param>
    /// <param name="options">Options for executing the pipeline.</param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<PipelineExecutionResult> ExecutePipelineAsync(PipelineMatch match, ExecutePipelineParams? options = default);

    /// <summary>
    /// Cancels the execution of a pipeline.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance to cancel.</param>
    /// <param name="cancellationToken"></param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<CancellationResult> CancelPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all the pipelines that can be started or resumed based on a query model.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<IEnumerable<PipelineMatch>> FindPipelinesAsync(PipelinesFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the <see cref="PipelineState"/> of the specified pipeline instance.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<PipelineState?> ExportPipelineStateAsync(string pipelineInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the specified <see cref="PipelineState"/>.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task ImportPipelineStateAsync(PipelineState pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the specified bookmark.
    /// </summary>
    /// <param name="bookmark">The bookmark to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task UpdateBookmarkAsync(StoredBookmark bookmark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the number of pipeline instances based on the provided query args.
    /// </summary>
    [Obsolete("Use the client API instead, retrieved from CreateClientAsync")]
    Task<long> CountRunningPipelinesAsync(CountRunningPipelinesRequest request, CancellationToken cancellationToken = default);
}