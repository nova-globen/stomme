namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the current pipeline status.
/// </summary>
public enum PipelineStatus
{
    /// <summary>
    /// The pipeline is running (although it may be waiting for external stimuli).   
    /// </summary>
    Running,
    
    /// <summary>
    /// The pipeline completed and is no longer running.
    /// </summary>
    Finished
}