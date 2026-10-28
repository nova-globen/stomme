using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Filters;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Deletes pipeline execution log records in response to the <see cref="PipelineInstancesDeleting"/> notification.
/// </summary>
[PublicAPI]
internal class DeletePipelineExecutionLogRecords : INotificationHandler<PipelineInstancesDeleting>
{
    private readonly IPipelineExecutionLogStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeletePipelineExecutionLogRecords"/> class.
    /// </summary>
    public DeletePipelineExecutionLogRecords(IPipelineExecutionLogStore store)
    {
        _store = store;
    }
    
    /// <inheritdoc />
    public async Task HandleAsync(PipelineInstancesDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteManyAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceIds = notification.Ids }, cancellationToken).ConfigureAwait(false);
    }
    
    private async Task DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }
}