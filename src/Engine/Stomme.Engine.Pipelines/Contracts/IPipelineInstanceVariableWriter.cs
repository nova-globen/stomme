namespace Stomme.Engine.Pipelines;

public interface IPipelineInstanceVariableWriter
{
    /// <summary>
    /// Sets the specified variables in the given <see cref="PipelineExecutionContext"/>.
    /// </summary>
    /// <param name="pipelineExecutionContext">The context of the pipeline execution.</param>
    /// <param name="variables">The collection of variables to set.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A complete list of resolved variables.</returns>
    Task<IEnumerable<ResolvedVariable>> SetVariables(PipelineExecutionContext pipelineExecutionContext, IEnumerable<VariableUpdateValue> variables, CancellationToken cancellationToken = default);
}