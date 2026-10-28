namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Executes steps that are temporarily detached from the pipeline. This allows steps to execute in the background, for example.
/// </summary>
public interface IBackgroundStepInvoker
{
    /// <summary>
    /// Executes the specified step detached from the pipeline.
    /// </summary>
    /// <param name="scheduledBackgroundStep">The scheduled background step.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handle representing the asynchronous invocation.</returns>
    Task ExecuteAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default);
}