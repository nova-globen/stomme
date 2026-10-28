using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Handlers.Notifications;

/// <summary>
/// Refreshes the <see cref="IStepRegistry"/> for the <see cref="PipelineDefinitionStepProvider"/> provider whenever an <see cref="PipelineDefinition"/> is published, retracted or deleted.
/// </summary>
[PublicAPI]
public class RefreshStepRegistry(IPipelineDefinitionStepRegistryUpdater pipelineDefinitionStepRegistryUpdater) :
    INotificationHandler<PipelineDefinitionPublished>,
    INotificationHandler<PipelineDefinitionRetracted>,
    INotificationHandler<PipelineDefinitionVersionRetracted>,
    INotificationHandler<PipelineDefinitionDeleted>,
    INotificationHandler<PipelineDefinitionsDeleted>,
    INotificationHandler<PipelineDefinitionVersionDeleted>,
    INotificationHandler<PipelineDefinitionVersionsDeleted>,
    INotificationHandler<PipelineDefinitionVersionsUpdated>
{
    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionPublished notification, CancellationToken cancellationToken)
    {
        return UpdateDefinition(notification.PipelineDefinition.Id, notification.PipelineDefinition.Options.UsableAsStep);
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionRetracted notification, CancellationToken cancellationToken)
    { 
        return UpdateDefinition(notification.PipelineDefinition.Id, notification.PipelineDefinition.Options.UsableAsStep);
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionVersionRetracted notification, CancellationToken cancellationToken)
    { 
        return UpdateDefinition(notification.PipelineDefinition.Id, notification.PipelineDefinition.Options.UsableAsStep);
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionDeleted notification, CancellationToken cancellationToken)
    { 
        pipelineDefinitionStepRegistryUpdater.RemoveDefinitionFromRegistry(notification.DefinitionId);
        return Task.CompletedTask;
    }
    
    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionsDeleted notification, CancellationToken cancellationToken)
    {
        foreach (string id in notification.DefinitionIds)
        {
            pipelineDefinitionStepRegistryUpdater.RemoveDefinitionFromRegistry(id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionVersionDeleted notification, CancellationToken cancellationToken)
    { 
        pipelineDefinitionStepRegistryUpdater.RemoveDefinitionVersionFromRegistry(notification.PipelineDefinition.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionVersionsDeleted notification, CancellationToken cancellationToken)
    {
        foreach (string id in notification.Ids)
        {
            pipelineDefinitionStepRegistryUpdater.RemoveDefinitionVersionFromRegistry(id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionsUpdated notification, CancellationToken cancellationToken)
    {
        foreach (var definition in notification.PipelineDefinitions)
        {
            await UpdateDefinition(definition.Id, definition.Options.UsableAsStep).ConfigureAwait(false);
        }
    }

    private Task UpdateDefinition(string id, bool? usableAsStep)
    {
        // Once a pipeline has been published, it should remain in the step registry unless no longer being marked as a step.
        if (usableAsStep.GetValueOrDefault())
            return pipelineDefinitionStepRegistryUpdater.AddToRegistry(id);

        pipelineDefinitionStepRegistryUpdater.RemoveDefinitionVersionFromRegistry(id);
        return Task.CompletedTask;
    }
}