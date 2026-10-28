using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a local implementation of the distributed runtime for running pipelines.
/// It does not support clustering and is intended for single-node deployments only.
/// For distributed deployments, use Proto.Actor or another distributed runtime.
/// </summary>
public partial class LocalPipelineRuntime : IPipelineRuntime
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IIdentityGenerator _identityGenerator;

    /// <summary>
    /// Represents a local implementation of the distributed runtime for running pipelines.
    /// It does not support clustering and is intended for single-node deployments only.
    /// For distributed deployments, use Proto.Actor or another distributed runtime.
    /// </summary>
    public LocalPipelineRuntime(IServiceProvider serviceProvider, IIdentityGenerator identityGenerator)
    {
        _serviceProvider = serviceProvider;
        _identityGenerator = identityGenerator;
        _obsoleteApi = new(() => ObsoletePipelineRuntime.Create(serviceProvider, CreateClientAsync));
    }

    /// <inheritdoc />
    public async ValueTask<IPipelineClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        return await CreateClientAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IPipelineClient> CreateClientAsync(string? pipelineInstanceId, CancellationToken cancellationToken = default)
    {
        pipelineInstanceId ??= _identityGenerator.GenerateId();
        var client = (IPipelineClient)ActivatorUtilities.CreateInstance(_serviceProvider, typeof(LocalPipelineClient), pipelineInstanceId);
        return new(client);
    }
}