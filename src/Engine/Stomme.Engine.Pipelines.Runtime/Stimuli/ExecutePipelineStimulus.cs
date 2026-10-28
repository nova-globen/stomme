using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime.Stimuli;

/// <summary>
/// Bookmark payload for the <see cref="ExecutePipeline"/> step.
/// </summary>
/// <param name="ChildInstanceId">The instance ID of the child pipeline that was created by the <see cref="ExecutePipeline"/> step.</param>
public record ExecutePipelineStimulus(string ChildInstanceId);