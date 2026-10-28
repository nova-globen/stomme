using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Provides options to the pipeline runtime.
/// </summary>
public class RuntimeOptions
{
    /// <summary>
    /// A list of pipeline builders configured during application startup.
    /// </summary>
    public IDictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>> Pipelines { get; set; } = new Dictionary<string, Func<IServiceProvider, ValueTask<IPipeline>>>();

    /// <summary>
    /// The default pipeline liveness threshold.
    /// </summary>
    /// <remarks>
    /// The liveness threshold is used to determine if a persisted pipeline instance in the <see cref="PipelineSubStatus.Executing"/> state should be considered interrupted or not.
    /// Interrupted pipelines will be attempted to be restarted.
    /// A separate heartbeat process will ensure the <see cref="PipelineInstance.UpdatedAt"/> is updated before this threshold.
    /// If the pipeline instance got removed from memory, e.g. because of an application shutdown, the LastUpdated field will eventually exceed the liveness threshold and therefore be considered to be interrupted.
    /// </remarks>
    public TimeSpan InactivityThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The number of pipeline instances to restart in a single batch.
    /// </summary>
    /// <remarks>
    /// The batch size represents the number of pipeline instance records to load into memory at a time.
    /// This provides control over memory consumption of the application.
    /// </remarks>
    public int RestartInterruptedPipelinesBatchSize { get; set; } = 100;
}