using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a host of a pipeline instance to interact with.
/// </summary>
[Obsolete("Use IPipelineRuntime and IPipelineClient services instead.")]
public class PipelineHost : IPipelineHost
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PipelineHost> _logger;
    private CancellationTokenSource? _linkedTokenSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineHost"/> class.
    /// </summary>
    public PipelineHost(
        IServiceScopeFactory serviceScopeFactory,
        PipelineGraph pipelineGraph,
        PipelineState pipelineState,
        ILogger<PipelineHost> logger)
    {
        PipelineGraph = pipelineGraph;
        PipelineState = pipelineState;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public PipelineGraph PipelineGraph { get; }

    /// <inheritdoc />
    public Pipeline Pipeline => PipelineGraph.Pipeline;

    /// <inheritdoc />
    public PipelineState PipelineState { get; set; }

    /// <inheritdoc />
    public object? LastResult { get; set; }

    /// <inheritdoc />
    public async Task<bool> CanStartPipelineAsync(RunPipelineOptions? @params = default, CancellationToken cancellationToken = default)
    {
        var strategyType = Pipeline.Options.ActivationStrategyType;

        if (strategyType == null)
            return true;

        var scope = _serviceScopeFactory.CreateAsyncScope();
        await using var _scopeAsyncDisposal = scope.ConfigureAwait(false);
        var instantiationStrategies = scope.ServiceProvider.GetServices<IPipelineActivationStrategy>();
        var strategy = instantiationStrategies.FirstOrDefault(x => x.GetType() == strategyType);

        if (strategy == null)
            return true;

        var correlationId = @params?.CorrelationId;
        var strategyContext = new PipelineInstantiationStrategyContext(Pipeline, correlationId, cancellationToken);
        return await strategy.GetAllowActivationAsync(strategyContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunPipelineAsync(RunPipelineOptions? @params = null, CancellationToken cancellationToken = default)
    {
        if (PipelineState.Status != PipelineStatus.Running)
        {
            _logger.LogWarning("Attempt to resume pipeline {PipelineInstanceId} that is not in the Running state. The actual state is {ActualPipelineStatus}", PipelineState.Id, PipelineState.Status);
            return new(null!, PipelineState, Pipeline, null, Journal.Empty);
        }
        
        var runOptions = new RunPipelineOptions
        {
            PipelineInstanceId = PipelineState.Id,
            CorrelationId = @params?.CorrelationId,
            Input = @params?.Input,
            Properties = @params?.Properties,
            StepHandle = @params?.StepHandle,
            BookmarkId = @params?.BookmarkId,
            TriggerStepId = @params?.TriggerStepId,
            ParentPipelineInstanceId = @params?.ParentPipelineInstanceId
        };
        _linkedTokenSource = new();
        var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _linkedTokenSource.Token).Token;

        var scope = _serviceScopeFactory.CreateAsyncScope();
        await using var _scopeAsyncDisposal = scope.ConfigureAwait(false);
        var pipelineRunner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
        var pipelineResult = await pipelineRunner.RunAsync(PipelineGraph, PipelineState, runOptions, linkedCancellationToken).ConfigureAwait(false);

        PipelineState = pipelineResult.PipelineState;
        await PersistStateAsync(scope, cancellationToken).ConfigureAwait(false);
        
        _linkedTokenSource.Dispose();
        _linkedTokenSource = null;
        
        return pipelineResult;
    }

    /// <inheritdoc />
    public async Task CancelPipelineAsync(CancellationToken cancellationToken = default)
    {
        // If the pipeline is currently executing, cancel it. This will cause the pipeline to be cancelled gracefully.
        if (_linkedTokenSource != null)
        {
            _linkedTokenSource.Cancel();
            return;
        }
        
        // Otherwise, cancel the pipeline by executing the canceler. This will set up a pipeline execution pipeline that will cancel the pipeline.
        var scope = _serviceScopeFactory.CreateAsyncScope();
        await using var _scopeAsyncDisposal = scope.ConfigureAwait(false);
        var serviceProvider = scope.ServiceProvider;
        var pipelineCanceler = serviceProvider.GetRequiredService<IPipelineCanceler>();
        PipelineState = await pipelineCanceler.CancelPipelineAsync(PipelineGraph, PipelineState, cancellationToken).ConfigureAwait(false);
        await PersistStateAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PersistStateAsync(CancellationToken cancellationToken = default)
    {
        var scope = _serviceScopeFactory.CreateAsyncScope();
        await using var _scopeAsyncDisposal = scope.ConfigureAwait(false);
        await PersistStateAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistStateAsync(IServiceScope scope, CancellationToken cancellationToken = default)
    {
        var pipelineInstanceManager = scope.ServiceProvider.GetRequiredService<IPipelineInstanceManager>();
        await pipelineInstanceManager.SaveAsync(PipelineState, cancellationToken).ConfigureAwait(false);
    }
}
