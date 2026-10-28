using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultStepExecutionManager : IStepExecutionManager
{
    private readonly IStepExecutionStore _store;
    private readonly INotificationSender _notificationSender;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultStepExecutionManager"/> class.
    /// </summary>
    public DefaultStepExecutionManager(IStepExecutionStore store, INotificationSender notificationSender)
    {
        _store = store;
        _notificationSender = notificationSender;
    }

    /// <inheritdoc />
    public async Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var records = (await _store.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();

        foreach (var record in records)
        {
            await _store.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
            await _notificationSender.SendAsync(new StepExecutionRecordDeleted(record), cancellationToken).ConfigureAwait(false);
        }

        return records.Count;
    }

    /// <inheritdoc />
    public async Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await _notificationSender.SendAsync(new StepExecutionRecordUpdated(record), cancellationToken).ConfigureAwait(false);
    }
}