using Stomme.Engine.Pipelines.Runtime.Requests;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultPipelineRestarter(IPipelineDispatcher pipelineDispatcher, ILogger<DefaultPipelineRestarter> logger) : IPipelineRestarter
{
    /// <inheritdoc />
    public async Task RestartPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default)
    {
        var request = new DispatchPipelineInstanceRequest(pipelineInstanceId);
        var options = new DispatchPipelineOptions();
        
        logger.LogInformation("Restarting pipeline {PipelineInstanceId}", pipelineInstanceId);
        await pipelineDispatcher.DispatchAsync(request, options, cancellationToken).ConfigureAwait(false);
    }
}