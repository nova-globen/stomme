using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Refreshes the <see cref="IStepRegistry"/> for the <see cref="PipelineDefinitionStepProvider"/> provider whenever pipeline definitions are reloaded.
/// </summary>
[PublicAPI]
public class RefreshStepRegistry(IPipelineDefinitionStepRegistryUpdater pipelineDefinitionStepRegistryUpdater) : INotificationHandler<PipelineDefinitionsReloaded>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsReloaded notification, CancellationToken cancellationToken)
    {
        foreach (var reloadedPipelineDefinition in notification.ReloadedPipelineDefinitions)
            await UpdateDefinition(reloadedPipelineDefinition.DefinitionVersionId, reloadedPipelineDefinition.UsableAsStep).ConfigureAwait(false);
    }

    private Task UpdateDefinition(string definitionVersionId, bool? usableAsStep)
    {
        // A pipeline should remain in the step registry unless no longer being marked as a step.
        if (usableAsStep.GetValueOrDefault())
            return pipelineDefinitionStepRegistryUpdater.AddToRegistry(definitionVersionId);

        pipelineDefinitionStepRegistryUpdater.RemoveDefinitionVersionFromRegistry(definitionVersionId);
        return Task.CompletedTask;
    }
}