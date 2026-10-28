using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime.Bookmarks;

/// <summary>
/// A bookmark that is used to resume a pipeline that is waiting for a background step to complete.
/// </summary>
[Obsolete("Use BackgroundStepStimulus instead.")]
[ForwardedType(typeof(BackgroundStepStimulus))]
public class BackgroundStepBookmark
{
    /// <summary>
    /// Set retroactively after the job has been scheduled. It is used to cancel the job when the bookmark is deleted.
    /// </summary>
    public string? JobId { get; set; }
}