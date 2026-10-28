using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Defines the operations for managing variables associated with a pipeline instance.
/// </summary>
public interface IPipelineInstanceVariableManager
{
    /// <summary>
    /// Retrieves all variables for the specified pipeline instance.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance.</param>
    /// <param name="excludeTags"></param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A collection of <see cref="ResolvedVariable"/> instances.</returns>
    Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(string pipelineInstanceId, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all variables for the specified pipeline instance.
    /// </summary>
    /// <param name="pipelineInstance">The pipeline instance from which to retrieve variables.</param>
    /// <param name="excludeTags">A collection of tags to exclude from the variable results, if any.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A collection of <see cref="ResolvedVariable"/> instances.</returns>
    Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineInstance pipelineInstance, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all variables for the specified pipeline state.
    /// </summary>
    /// <param name="pipelineState">The pipeline state to retrieve variables from.</param>
    /// <param name="excludeTags">Optional tags to exclude from the result.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A collection of <see cref="ResolvedVariable"/> instances.</returns>
    Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineState pipelineState, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all variables from the specified <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="pipelineExecutionContext">The context of the pipeline execution.</param>
    /// <param name="excludeTags"></param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A collection of <see cref="ResolvedVariable"/> instances.</returns>
    Task<IEnumerable<ResolvedVariable>> GetVariablesAsync(PipelineExecutionContext pipelineExecutionContext, IEnumerable<string>? excludeTags = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sets the specified variables in the specified pipeline instance.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance.</param>
    /// <param name="variables">The collection of variables to set.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A complete list of resolved variables.</returns>
    Task<IEnumerable<ResolvedVariable>> SetVariablesAsync(string pipelineInstanceId, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sets the specified variables in the given <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="pipelineExecutionContext">The context of the pipeline execution.</param>
    /// <param name="variables">The collection of variables to set.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A complete list of resolved variables.</returns>
    Task<IEnumerable<ResolvedVariable>> SetVariablesAsync(PipelineExecutionContext pipelineExecutionContext, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default);
}