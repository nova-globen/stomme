using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime.Stimuli;

/// <summary>
/// Bookmark payload for the <see cref="BulkDispatchPipelines"/> step.
/// </summary>
public class BulkDispatchPipelinesStimulus(string parentInstanceId)
{
    /// <summary>
    /// The ID of the parent pipeline instance that is waiting for child pipelines to complete.
    /// </summary>
    public string ParentInstanceId { get; init; } = parentInstanceId;

    /// <summary>The number of child pipelines that were created by the <see cref="BulkDispatchPipelines"/> step.</summary>
    [ExcludeFromHash] public long ScheduledInstanceIdsCount { get; set; }
    
}