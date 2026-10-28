namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a reason for a cancellation failure.
/// </summary>
public enum CancellationFailureReason
{
    /// <summary>
    /// The pipeline instance was not found.
    /// </summary>
    NotFound,
    /// <summary>
    /// The pipeline instance is already being canceled remotely.
    /// </summary>
    CancellationAlreadyInProgress,
    
    /// <summary>
    /// The pipeline instance is already canceled.
    /// </summary>
    AlreadyFinished,
}