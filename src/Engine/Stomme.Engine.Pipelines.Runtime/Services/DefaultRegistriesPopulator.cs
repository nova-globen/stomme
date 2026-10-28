using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Models;
using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultRegistriesPopulator(
    IPipelineDefinitionStorePopulator pipelineDefinitionStorePopulator, 
    IStepRegistryPopulator stepRegistryPopulator,
    INotificationSender notificationSender) : IRegistriesPopulator
{
    /// <inheritdoc />
    public async Task PopulateAsync(CancellationToken cancellationToken = default)
    {
        // Stage 1: Populate the step registry.
        // Because pipeline definitions can be used as steps, we need to make sure that the step registry is populated before we populate the pipeline definition store.
        await stepRegistryPopulator.PopulateRegistryAsync(cancellationToken).ConfigureAwait(false);
        
        // Stage 2: Populate the pipeline definition store.
        await pipelineDefinitionStorePopulator.PopulateStoreAsync(false, cancellationToken).ConfigureAwait(false);

        // Stage 3: Re-populate the step registry.
        // After the pipeline definition store has been populated, we need to re-populate the step registry to make sure that the step descriptors are up-to-date.
        await stepRegistryPopulator.PopulateRegistryAsync(cancellationToken).ConfigureAwait(false);

        // Stage 4. Re-update the pipeline definition store with the current set of steps.
        // Finally, we need to re-populate the pipeline definition store to make sure that the pipeline definitions are up-to-date.
        var pipelineDefinitions = await pipelineDefinitionStorePopulator.PopulateStoreAsync(true, cancellationToken).ConfigureAwait(false);
        
        // Stage 5: Publish a notification that the pipeline definitions have been reloaded. This ensures other replicated nodes can update their step registry.
        var reloadedPipelineDefinitions = pipelineDefinitions.Select(ReloadedPipelineDefinition.FromDefinition).ToList();
        var notification = new PipelineDefinitionsReloaded(reloadedPipelineDefinitions);
        await notificationSender.SendAsync(notification, cancellationToken).ConfigureAwait(false);
    }
}