using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Filters;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Deletes step execution log records in response to the <see cref="PipelineInstancesDeleting"/> notification.
/// </summary>
[PublicAPI]
internal class DeleteStepExecutionLogRecords : INotificationHandler<PipelineInstancesDeleting>
{
    private readonly IStepExecutionStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteStepExecutionLogRecords"/> class.
    /// </summary>
    public DeleteStepExecutionLogRecords(IStepExecutionStore store)
    {
        _store = store;
    }
    
    /// <inheritdoc />
    public async Task HandleAsync(PipelineInstancesDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteManyAsync(new StepExecutionRecordFilter { PipelineInstanceIds = notification.Ids }, cancellationToken).ConfigureAwait(false);
    }
    
    private async Task DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }
}