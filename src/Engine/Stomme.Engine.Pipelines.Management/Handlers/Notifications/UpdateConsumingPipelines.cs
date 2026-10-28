using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;

namespace Stomme.Engine.Pipelines.Management.Handlers.Notifications;

/// <summary>
/// Updates consuming pipelines when a pipeline definition is published.
/// </summary>
public class UpdateConsumingPipelines(IPipelineReferenceUpdater pipelineReferenceUpdater) : INotificationHandler<PipelineDefinitionPublished>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionPublished notification, CancellationToken cancellationToken)
    {
        var definition = notification.PipelineDefinition;
        var result = await pipelineReferenceUpdater.UpdatePipelineReferencesAsync(definition, cancellationToken).ConfigureAwait(false);
        notification.AffectedPipelines.PipelineDefinitions.AddRange(result.UpdatedPipelines);
    }
}