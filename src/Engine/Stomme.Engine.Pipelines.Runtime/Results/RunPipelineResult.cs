// using Stomme.Engine.Pipelines.Helpers;
// using Stomme.Engine.Pipelines.Models;
//
// namespace Stomme.Engine.Pipelines.Runtime.Results;
//
// /// <summary>
// /// Represents the result of executing a pipeline.
// /// </summary>
// /// <param name="PipelineInstanceId">The ID of the pipeline instance.</param>
// /// <param name="BookmarksDiff">A diff of the bookmarks.</param>
// /// <param name="Status">The status of the pipeline.</param>
// /// <param name="SubStatus">The sub status of the pipeline.</param>
// /// <param name="Incidents">A collection of incidents that may have occurred during execution.</param>
// public record RunPipelineResult(string PipelineInstanceId, Diff<BookmarkInfo> BookmarksDiff, PipelineStatus Status, PipelineSubStatus SubStatus, ICollection<StepIncident> Incidents);