using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Open.Linq.AsyncExtensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// This implementation saves <see cref="PipelineExecutionLogRecord"/> directly through the store.
/// </summary>
public class StorePipelineExecutionLogSink(IPipelineExecutionLogStore store, ILogRecordExtractor<PipelineExecutionLogRecord> extractor, INotificationSender notificationSender) : ILogRecordSink<PipelineExecutionLogRecord>
{
    /// <inheritdoc />
    public async Task PersistExecutionLogsAsync(PipelineExecutionContext context, CancellationToken cancellationToken)
    {
        var records = await extractor.ExtractLogRecordsAsync(context).ToList().ConfigureAwait(false);
        
        if(records.Count == 0)
            return;
        
        await store.AddManyAsync(records, context.CancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new PipelineExecutionLogUpdated(context), context.CancellationToken).ConfigureAwait(false);
    }
}