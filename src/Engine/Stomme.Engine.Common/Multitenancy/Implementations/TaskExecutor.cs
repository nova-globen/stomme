using System.Reflection;
using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Common.RecurringTasks;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Common.Multitenancy;

public class TaskExecutor(IDistributedLockProvider distributedLockProvider, IOptions<DistributedLockingOptions> options) : ITaskExecutor, IBackgroundTaskStarter
{
    public async Task ExecuteTaskAsync(ITask task, CancellationToken cancellationToken)
    {
        await ExecuteInternalAsync(task, () => task.ExecuteAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(IBackgroundTask task, CancellationToken cancellationToken)
    {
        await ExecuteInternalAsync(task, () => task.StartAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(IBackgroundTask task, CancellationToken cancellationToken)
    {
        await ExecuteInternalAsync(task, () => task.StopAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteInternalAsync(ITask task, Func<Task> action, CancellationToken cancellationToken)
    {
        var taskType = task.GetType();
        var singleNodeTask = taskType.GetCustomAttribute<SingleNodeTaskAttribute>() != null;

        if (singleNodeTask)
        {
            var resourceName = taskType.AssemblyQualifiedName!;
            await using (await distributedLockProvider.AcquireLockAsync(resourceName, options.Value.LockAcquisitionTimeout, cancellationToken: cancellationToken).ConfigureAwait(false))
                await action().ConfigureAwait(false);
        }
        else
        {
            await action().ConfigureAwait(false);
        }
    }
}