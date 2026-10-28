using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

[UsedImplicitly]
internal class IndexTriggers(ITriggerIndexer triggerIndexer) :
    INotificationHandler<PipelineDefinitionPublished>,
    INotificationHandler<PipelineDefinitionRetracted>
{
    public async Task HandleAsync(PipelineDefinitionPublished notification, CancellationToken cancellationToken) => await triggerIndexer.IndexTriggersAsync(notification.PipelineDefinition, cancellationToken).ConfigureAwait(false);
    public async Task HandleAsync(PipelineDefinitionRetracted notification, CancellationToken cancellationToken) => await triggerIndexer.IndexTriggersAsync(notification.PipelineDefinition, cancellationToken).ConfigureAwait(false);
}