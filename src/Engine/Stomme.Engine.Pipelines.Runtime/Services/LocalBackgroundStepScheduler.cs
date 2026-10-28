using Stomme.Engine.Mediator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Invokes steps from a background worker within the context of its pipeline instance using a local background worker.
/// </summary>
public class LocalBackgroundStepScheduler(IJobQueue jobQueue, IServiceScopeFactory scopeFactory) : IBackgroundStepScheduler
{
    public Task<string> CreateAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default)
    {
        var jobId = jobQueue.Create(async ct => await InvokeBackgroundStep(scheduledBackgroundStep, ct).ConfigureAwait(false));
        return Task.FromResult(jobId);
    }

    public Task ScheduleAsync(string jobId, CancellationToken cancellationToken = default)
    {
        jobQueue.Enqueue(jobId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> ScheduleAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default)
    {
        var jobId = jobQueue.Enqueue(async ct => await InvokeBackgroundStep(scheduledBackgroundStep, ct).ConfigureAwait(false));
        return Task.FromResult(jobId);
    }

    public Task UnscheduledAsync(string jobId, CancellationToken cancellationToken = default)
    {
        jobQueue.Dequeue(jobId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        jobQueue.Cancel(jobId);
        return Task.CompletedTask;
    }
    
    private async Task InvokeBackgroundStep(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var backgroundStepInvoker = scope.ServiceProvider.GetRequiredService<IBackgroundStepInvoker>();
        await backgroundStepInvoker.ExecuteAsync(scheduledBackgroundStep, cancellationToken).ConfigureAwait(false);
    }
}