using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Invokes a pipeline in a transactional manner (i.e., runs the pipeline in the current context and not via the <see cref="IPipelineRuntime"/>).
/// </summary>
public interface IPipelineInvoker
{
    /// <summary>
    /// Invokes the pipeline.
    /// </summary>
    Task<RunPipelineResult> InvokeAsync(Pipeline pipeline, RunPipelineOptions? options = default, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Invokes the pipeline.
    /// </summary>
    Task<RunPipelineResult> InvokeAsync(PipelineGraph pipelineGraph, RunPipelineOptions? options = default, CancellationToken cancellationToken = default);
}