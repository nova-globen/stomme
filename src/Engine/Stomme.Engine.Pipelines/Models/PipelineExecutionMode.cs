namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// The runtime mode to use when running thr pipeline.
/// </summary>
public enum PipelineExecutionMode
{
    /// <summary>
    /// The pipeline will be executed synchronously, blocking the current thread until the pipeline completes or gets suspended.
    /// </summary>
    Synchronous,
    
    /// <summary>
    /// The pipeline will be executed asynchronously, returning immediately.
    /// </summary>
    Asynchronous
}