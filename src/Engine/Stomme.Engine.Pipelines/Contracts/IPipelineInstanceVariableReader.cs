namespace Stomme.Engine.Pipelines;

/// <summary>
/// Enumerates variables for a pipeline instance.
/// </summary>
public interface IPipelineInstanceVariableReader
{
    /// <summary>
    /// Retrieves all variables from the specified <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="pipelineExecutionContext">The context of the pipeline execution.</param>
    /// <param name="excludeTags"></param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a collection of <see cref="ResolvedVariable"/> instances.</returns>
    Task<IEnumerable<ResolvedVariable>> GetVariables(PipelineExecutionContext pipelineExecutionContext, IEnumerable<string>? excludeTags = default, CancellationToken cancellationToken = default);
}