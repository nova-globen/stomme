using Stomme.Engine.Pipelines.Runtime.Steps;

namespace Stomme.Engine.Pipelines.Runtime.Stimuli;

/// <summary>
/// Bookmark payload for the <see cref="DispatchPipeline"/> step.
/// </summary>
/// <param name="ChildInstanceId">The instance ID of the child pipeline that was created by the <see cref="DispatchPipeline"/> step.</param>
public record DispatchPipelineStimulus(string ChildInstanceId);