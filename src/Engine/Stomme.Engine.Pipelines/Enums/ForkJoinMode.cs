using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Controls when a <see cref="Fork"/> completes.
/// </summary>
public enum ForkJoinMode
{
    /// <summary>
    /// The <see cref="Fork"/> completes after all inbound steps have completed.
    /// </summary>
    WaitAll,
    
    /// <summary>
    /// The <see cref="Fork"/> completes as soon as any of its inbound step completes.
    /// </summary>
    WaitAny
}