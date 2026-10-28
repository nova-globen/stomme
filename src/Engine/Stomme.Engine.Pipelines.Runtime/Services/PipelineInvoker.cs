using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineInvoker(IPipelineGraphBuilder pipelineGraphBuilder, IPipelineRunner pipelineRunner) : IPipelineInvoker
{
    /// <inheritdoc />
    public async Task<RunPipelineResult> InvokeAsync(Pipeline pipeline, RunPipelineOptions? options = default, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await pipelineGraphBuilder.BuildAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return await InvokeAsync(pipelineGraph, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> InvokeAsync(PipelineGraph pipelineGraph, RunPipelineOptions? options = default, CancellationToken cancellationToken = default)
    {
        return await pipelineRunner.RunAsync(pipelineGraph, options, cancellationToken).ConfigureAwait(false);
    }
}