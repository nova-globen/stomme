namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a scheduled background step
/// </summary>
/// <param name="PipelineInstanceId">The ID of the pipeline instance containing the step to execute.</param>
/// <param name="StepNodeId">The ID of the step to execute.</param>
/// <param name="BookmarkId">The ID of the bookmark to resume.</param>
public record ScheduledBackgroundStep(string PipelineInstanceId, string StepNodeId, string BookmarkId);