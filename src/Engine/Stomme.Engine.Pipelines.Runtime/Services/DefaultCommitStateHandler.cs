using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

public class DefaultCommitStateHandler(
    IPipelineInstanceManager pipelineInstanceManager,
    IBookmarksPersister bookmarkPersister,
    IVariablePersistenceManager variablePersistenceManager,
    IMediator mediator,
    ILogRecordSink<StepExecutionRecord> stepExecutionLogRecordSink,
    ILogRecordSink<PipelineExecutionLogRecord> pipelineExecutionLogRecordSink) : ICommitStateHandler
{
    public async Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default)
    {
        var pipelineState = pipelineInstanceManager.ExtractPipelineState(pipelineExecutionContext);
        await CommitAsync(pipelineExecutionContext, pipelineState, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(PipelineExecutionContext pipelineExecutionContext, PipelineState pipelineState, CancellationToken cancellationToken = default)
    {
        var updateBookmarksRequest = new UpdateBookmarksRequest(pipelineExecutionContext, pipelineExecutionContext.BookmarksDiff, pipelineExecutionContext.CorrelationId);
        await bookmarkPersister.PersistBookmarksAsync(updateBookmarksRequest).ConfigureAwait(false);
        await stepExecutionLogRecordSink.PersistExecutionLogsAsync(pipelineExecutionContext, cancellationToken).ConfigureAwait(false);
        await pipelineExecutionLogRecordSink.PersistExecutionLogsAsync(pipelineExecutionContext, cancellationToken).ConfigureAwait(false);
        await variablePersistenceManager.SaveVariablesAsync(pipelineExecutionContext).ConfigureAwait(false);
        var pipelineInstance = await pipelineInstanceManager.SaveAsync(pipelineState, cancellationToken).ConfigureAwait(false);
        pipelineExecutionContext.ExecutionLog.Clear();
        pipelineExecutionContext.ClearCompletedStepExecutionContexts();
        await pipelineExecutionContext.ExecuteDeferredTasksAsync().ConfigureAwait(false);
        await mediator.SendAsync(new PipelineStateCommitted(pipelineExecutionContext, pipelineState, pipelineInstance), cancellationToken).ConfigureAwait(false);
    }
}