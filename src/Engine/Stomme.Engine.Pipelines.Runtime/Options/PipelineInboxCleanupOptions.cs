namespace Stomme.Engine.Pipelines.Runtime.Options;

/// <summary>
/// Options for cleaning up the pipeline inbox.
/// </summary>
public class PipelineInboxCleanupOptions
{
    /// <summary>
    /// The sweep interval at which to clean up the pipeline inbox.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The number of messages to clean up per sweep.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether the pipeline inbox cleanup is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}