using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Models;
using Stomme.Engine.Pipelines.Runtime.Commands;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

[UsedImplicitly]
public class DispatchStimulusCommandHandler(IStimulusSender stimulusSender) : ICommandHandler<DispatchStimulusCommand>
{
    public async Task<Unit> HandleAsync(DispatchStimulusCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (request.StepTypeName != null)
        {
            await stimulusSender.SendAsync(request.StepTypeName, request.Stimulus!, request.Metadata, cancellationToken).ConfigureAwait(false);
            return Unit.Instance;
        }

        if (request.StimulusHash != null)
        {
            await stimulusSender.SendAsync(request.StimulusHash!, request.Metadata, cancellationToken).ConfigureAwait(false);
            return Unit.Instance;
        }

        throw new InvalidOperationException("Either StepTypeName or StimulusHash must be specified.");
    }
}