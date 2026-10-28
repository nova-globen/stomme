using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Handlers.Notifications;

/// <summary>
/// Deletes pipeline instances when a pipeline definition or version is deleted.
/// </summary>
[UsedImplicitly]
public class DeletePipelineInstances :
    INotificationHandler<PipelineDefinitionDeleting>,
    INotificationHandler<PipelineDefinitionVersionDeleting>,
    INotificationHandler<PipelineDefinitionsDeleting>,
    INotificationHandler<PipelineDefinitionVersionsDeleting>
{
    private readonly IPipelineInstanceManager _pipelineInstanceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeletePipelineInstances"/> class.
    /// </summary>
    public DeletePipelineInstances(IPipelineInstanceManager pipelineInstanceManager)
    {
        _pipelineInstanceManager = pipelineInstanceManager;
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionDeleting notification, CancellationToken cancellationToken)
    {
        await DeletePipelineInstancesAsync(new PipelineInstanceFilter { DefinitionId = notification.DefinitionId }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionDeleting notification, CancellationToken cancellationToken)
    {
        await DeletePipelineInstancesAsync(new PipelineInstanceFilter { DefinitionVersionId = notification.PipelineDefinition.Id }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsDeleting notification, CancellationToken cancellationToken)
    {
        await DeletePipelineInstancesAsync(new PipelineInstanceFilter { DefinitionIds = notification.DefinitionIds }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionsDeleting notification, CancellationToken cancellationToken)
    {
        await DeletePipelineInstancesAsync(new PipelineInstanceFilter { Ids = notification.Ids }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeletePipelineInstancesAsync(PipelineInstanceFilter filter, CancellationToken cancellationToken) => await _pipelineInstanceManager.BulkDeleteAsync(filter, cancellationToken).ConfigureAwait(false);
}