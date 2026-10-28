using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// This implementation saves <see cref="StepExecutionRecord"/> directly through the store.
/// </summary>
public class StoreStepExecutionLogSink(
    IStepExecutionStore stepExecutionStore,
    INotificationSender notificationSender)
    : ILogRecordSink<StepExecutionRecord>
{
    /// <inheritdoc />
    public async Task PersistExecutionLogsAsync(PipelineExecutionContext context, CancellationToken cancellationToken = default)
    {
        // Select tainted step execution contexts to avoid saving untainted ones multiple times.
        var stepExecutionContexts = context.StepExecutionContexts.Where(x => x.IsDirty).ToList();

        if (stepExecutionContexts.Count == 0)
            return;

        var records = await Task.WhenAll(stepExecutionContexts.Select(x => x.GetOrMapCapturedStepExecutionRecordAsync())).ConfigureAwait(false);
        await stepExecutionStore.SaveManyAsync(records, cancellationToken).ConfigureAwait(false);

        // Untaint step execution contexts.
        foreach (var stepExecutionContext in stepExecutionContexts)
            stepExecutionContext.ClearTaint();

        await notificationSender.SendAsync(new StepExecutionLogUpdated(context, records), cancellationToken).ConfigureAwait(false);
    }
}