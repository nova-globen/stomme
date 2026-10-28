using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Pipelines.Runtime.Commands;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Handles the <see cref="CancelPipelinesCommand"/>.
/// </summary>
[UsedImplicitly]
public class CancelPipelinesCommandHandler(IPipelineRuntime pipelineRuntime) : ICommandHandler<CancelPipelinesCommand>
{
    /// <inheritdoc />
    public async Task<Unit> HandleAsync(CancelPipelinesCommand command, CancellationToken cancellationToken)
    {
        var pipelineInstanceId = command.Request.PipelineInstanceId;
        var pipelineClient = await pipelineRuntime.CreateClientAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        await pipelineClient.CancelAsync(cancellationToken).ConfigureAwait(false);
        
        return Unit.Instance;
    }
}