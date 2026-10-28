using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Extracts pipeline state from a pipeline execution context and vice versa.
/// </summary>
public interface IPipelineStateExtractor
{
    /// <summary>
    /// Extracts pipeline state from a pipeline execution context.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context to map.</param>
    /// <returns>The mapped pipeline state.</returns>
    PipelineState Extract(PipelineExecutionContext pipelineExecutionContext);

    /// <summary>
    /// Applies pipeline state to a pipeline execution context.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context to map onto.</param>
    /// <param name="state">The pipeline state to apply.</param>
    /// <returns>The updated pipeline execution context.</returns>
    Task<PipelineExecutionContext> ApplyAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState state);
}