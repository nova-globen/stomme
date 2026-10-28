using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime.Bookmarks;

/// <summary>
/// Bookmark payload for the <see cref="BulkDispatchPipelines"/> step.
/// </summary>
[Obsolete("Use BulkDispatchPipelinesStimulus instead.")]
[ForwardedType(typeof(BulkDispatchPipelinesStimulus))]
public class BulkDispatchPipelinesBookmark(string parentInstanceId)
{
    /// <summary>
    /// The ID of the parent pipeline instance that is waiting for child pipelines to complete.
    /// </summary>
    public string ParentInstanceId { get; init; } = parentInstanceId;

    /// <summary>The number of child pipelines that were created by the <see cref="BulkDispatchPipelines"/> step.</summary>
    [ExcludeFromHash] public long ScheduledInstanceIdsCount { get; set; }
    
}