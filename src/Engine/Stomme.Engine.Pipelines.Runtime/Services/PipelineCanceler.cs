using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineCanceler(
    IPipelineExecutionPipeline pipelineExecutionPipeline, 
    IPipelineStateExtractor pipelineStateExtractor, 
    IMediator mediator, 
    IServiceProvider serviceProvider) : IPipelineCanceler
{
    /// <inheritdoc />
    public async Task<PipelineState> CancelPipelineAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(serviceProvider, pipelineGraph, pipelineState, cancellationToken: cancellationToken).ConfigureAwait(false);
        await CancelPipelineAsync(pipelineExecutionContext, cancellationToken).ConfigureAwait(false);
        return pipelineStateExtractor.Extract(pipelineExecutionContext);
    }

    /// <inheritdoc />
    public async Task CancelPipelineAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default)
    {
        await mediator.SendAsync(new PipelineCancelling(pipelineExecutionContext.Id), cancellationToken).ConfigureAwait(false);
        var pipelineBuilder = new PipelineExecutionPipelineBuilder(serviceProvider);
        pipelineExecutionPipeline.ConfigurePipelineBuilder(pipelineBuilder);
        pipelineBuilder.ReplaceTerminal<CancelPipelineMiddleware>();
        var executionPipeline = pipelineBuilder.Build();
        await executionPipeline(pipelineExecutionContext).ConfigureAwait(false);
        await mediator.SendAsync(new PipelineCancelled(pipelineExecutionContext.Id), cancellationToken).ConfigureAwait(false);
    }
}