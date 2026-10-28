using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
[Obsolete("Use IPipelineRuntime, IPipelineRunner and IPipelineInvoker services instead.")]
public class PipelineHostFactory : IPipelineHostFactory
{
    private readonly IIdentityGenerator _identityGenerator;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineHostFactory"/> class.
    /// </summary>
    public PipelineHostFactory(IIdentityGenerator identityGenerator, IServiceProvider serviceProvider)
    {
        _identityGenerator = identityGenerator;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task<IPipelineHost> CreateAsync(PipelineGraph pipelineGraph, PipelineHostOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pipeline = pipelineGraph.Pipeline;
        var newPipelineInstanceId = options?.NewPipelineInstanceId ?? _identityGenerator.GenerateId();
        var pipelineState = new PipelineState
        {
            Id = newPipelineInstanceId,
            DefinitionId = pipeline.Identity.DefinitionId,
            DefinitionVersion = pipeline.Identity.Version
        };

        return await CreateAsync(pipelineGraph, pipelineState, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IPipelineHost> CreateAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        var pipelineHost = (IPipelineHost)ActivatorUtilities.CreateInstance<PipelineHost>(_serviceProvider, pipelineGraph, pipelineState);
        return Task.FromResult(pipelineHost);
    }
}