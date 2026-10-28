using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime.Bookmarks;

/// <summary>
/// Bookmark payload for the <see cref="DispatchPipeline"/> step.
/// </summary>
/// <param name="ChildInstanceId">The instance ID of the child pipeline that was created by the <see cref="DispatchPipeline"/> step.</param>
[Obsolete("Use DispatchPipelineStimulus instead.")]
[ForwardedType(typeof(DispatchPipelineStimulus))]
public record DispatchPipelineBookmark(string ChildInstanceId);