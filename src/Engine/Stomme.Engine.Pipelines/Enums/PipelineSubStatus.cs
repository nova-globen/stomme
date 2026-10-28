namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the pipeline sub-status.
/// </summary>
public enum PipelineSubStatus
{
    /// <summary>
    /// The pipeline is pending execution.
    /// </summary>
    Pending,
    
    /// <summary>
    /// The pipeline is currently executing.   
    /// </summary>
    Executing,
    
    /// <summary>
    /// The pipeline is currently suspended and waiting for external stimuli to resume.   
    /// </summary>
    Suspended,
    
    /// <summary>
    /// The pipeline completed successfully.
    /// </summary>
    Finished,
    
    /// <summary>
    /// The pipeline was cancelled.
    /// </summary>
    Cancelled,
    
    /// <summary>
    /// The pipeline has faulted.
    /// </summary>
    Faulted,
}