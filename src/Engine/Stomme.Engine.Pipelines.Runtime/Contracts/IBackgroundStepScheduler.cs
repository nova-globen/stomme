namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Invokes steps from a background worker within the context of its pipeline instance.
/// </summary>
public interface IBackgroundStepScheduler
{
    /// <summary>
    /// Creates a scheduled job for the specified step for execution in the background without starting the job.
    /// The caller is responsible for starting the job.
    /// </summary>
    /// <param name="scheduledBackgroundStep">The step to schedule fo background execution.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handle representing the asynchronous invocation.</returns>
    Task<string> CreateAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Scheduled the specified job.
    /// </summary>
    Task ScheduleAsync(string jobId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Schedules the specified step for execution in the background.
    /// </summary>
    /// <param name="scheduledBackgroundStep">The step to schedule fo background execution.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handle representing the asynchronous invocation.</returns>
    Task<string> ScheduleAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Removes the specified job from the schedule.
    /// </summary>
    Task UnscheduledAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the specified job.
    /// </summary>
    /// <param name="jobId">the ID of the job to cancel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);
}