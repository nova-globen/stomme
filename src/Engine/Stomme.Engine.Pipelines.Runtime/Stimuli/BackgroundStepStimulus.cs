using Stomme.Engine.Pipelines.Attributes;

namespace Stomme.Engine.Pipelines.Runtime.Stimuli;

/// <summary>
/// A bookmark that is used to resume a pipeline that is waiting for a background step to complete.
/// </summary>
public class BackgroundStepStimulus
{
    /// <summary>
    /// Set retroactively after the job has been scheduled. It is used to cancel the job when the bookmark is deleted.
    /// </summary>
    [ExcludeFromHash]public string? JobId { get; set; }
}