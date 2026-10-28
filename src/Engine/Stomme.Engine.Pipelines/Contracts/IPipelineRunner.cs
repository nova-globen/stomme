using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Runs a given pipeline by scheduling its root step.
/// </summary>
public interface IPipelineRunner
{
    Task<RunPipelineResult> RunAsync(IStep step, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync(IPipeline pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult<TResult>> RunAsync<TResult>(PipelineBase<TResult> pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync<T>(RunPipelineOptions? options = null, CancellationToken cancellationToken = default) where T : IPipeline, new();
    Task<TResult> RunAsync<T, TResult>(RunPipelineOptions? options = null, CancellationToken cancellationToken = default) where T : PipelineBase<TResult>, new();
    Task<RunPipelineResult> RunAsync(Pipeline pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync(Pipeline pipeline, PipelineState pipelineState, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync(PipelineGraph pipelineGraph, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, RunPipelineOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunPipelineResult> RunAsync(PipelineExecutionContext pipelineExecutionContext);
}